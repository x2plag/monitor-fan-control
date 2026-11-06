using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorFanControl;

internal sealed partial class HidFanController
{
    internal enum Lg39BridgeAction { Probe, FanReadback, BoundedFanTest, BoundedHoldTest, RestoreAutomatic, RestoreWatchdog, HoldRestoreWatchdog, ManualHold, ManualAutomatic }

    // Diagnostic only: this interface is the MCU's I2C bridge, not its SC fan interface.
    // Writes are limited to volatile modes 0/1/2 and opening/closing the service window.
    public static async Task ProbeLg39I2cBridgeAsync(Lg39BridgeAction action = Lg39BridgeAction.Probe,
        string? watchdogReadyEvent = null, string? stopEventName = null,
        string? manualReadyEventName = null, int ownerProcessId = 0)
    {
        using var requestedStop = new CancellationTokenSource();
        using var stopEvent = stopEventName is null ? null : EventWaitHandle.OpenExisting(stopEventName);
        var stopRegistration = stopEvent is null ? null : ThreadPool.RegisterWaitForSingleObject(
            stopEvent, (_, _) => { try { requestedStop.Cancel(); } catch (ObjectDisposedException) { } }, null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await RunBridge();
        }
        finally { stopRegistration?.Unregister(null); }

        async Task RunBridge()
        {
        var restoreOnly = action is Lg39BridgeAction.RestoreAutomatic or Lg39BridgeAction.RestoreWatchdog or Lg39BridgeAction.HoldRestoreWatchdog or Lg39BridgeAction.ManualAutomatic;
        var holdTest = action == Lg39BridgeAction.BoundedHoldTest;
        var fanTest = action is Lg39BridgeAction.BoundedFanTest or Lg39BridgeAction.BoundedHoldTest;
        var paths = EnumerateHidPaths().Where(path => path.Contains(
            "vid_043e&pid_9c33&mi_00", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length != 1)
            throw new FanToolException($"Expected one LG 9C33 I2C bridge; found {paths.Length}.");
        // The restore-only worker must survive a parent crash while its semaphore is held.
        // It sends no mode2. Recovery reads start after the parent's bounded read phase.
        var lockTaken = !restoreOnly && InterprocessExchangeLock.WaitOne(TimeSpan.FromSeconds(2));
        if (!restoreOnly && !lockTaken)
            throw new FanToolException("Another monitor diagnostic is using the HID interface.");
        Process? watchdog = null;
        try
        {
            using var handle = Native.CreateFileW(paths[0], Native.GenericRead | Native.GenericWrite,
                Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
                Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
                throw Native.Error("Opening LG I2C bridge");
            var attributes = new Native.HidAttributes { Size = (uint)Marshal.SizeOf<Native.HidAttributes>() };
            if (!Native.HidD_GetAttributes(handle, ref attributes) ||
                attributes.VendorId != 0x043e || attributes.ProductId != 0x9c33 ||
                attributes.VersionNumber != 0x0111)
                throw new FanToolException("The bridge identity differs from the inspected Renesas 1.11 controller.");
            if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
                throw Native.Error("Reading LG bridge descriptor");
            try
            {
                if (Native.HidP_GetCaps(preparsed, out var caps) != 0x00110000 ||
                    caps.UsagePage != 0xff00 || caps.Usage != 1 ||
                    caps.InputReportByteLength != 65 || caps.OutputReportByteLength != 65)
                    throw new FanToolException("The bridge HID descriptor changed; no report sent.");
            }
            finally { Native.HidD_FreePreparsedData(preparsed); }

            await using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            byte tag = (byte)Random.Shared.Next(1, 220);
            const byte family = 0x39;

            byte[] Frame(byte command, byte count, byte[]? payload = null)
            {
                payload ??= [];
                if (payload.Length > 56)
                    throw new InvalidOperationException("Oversized bridge request");
                var report = new byte[65];
                report[1] = (byte)(8 + payload.Length);
                report[2] = ++tag;
                report[3] = family;
                report[4] = command;
                report[5] = count;
                report[7] = 0x0b; // Native START/STOP/read termination flags; zero performs no transfer.
                report[8] = 0x37; // Seven-bit DDC slave; never a flash or arbitrary peripheral.
                payload.CopyTo(report, 9);
                return report;
            }

            async Task<byte[]> Exchange(byte[] report, CancellationToken cancellationToken = default)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await stream.WriteAsync(report, timeout.Token);
                for (var attempt = 0; attempt < 16; attempt++)
                {
                    var reply = new byte[65];
                    var count = await stream.ReadAsync(reply, timeout.Token);
                    if (count < 5 || reply[0] != 0 || reply[2] != report[2] || reply[3] != family)
                        continue;
                    var length = reply[1];
                    if (length < 4 || length > 64 || count < length + 1)
                        throw new FanToolException("Invalid I2C bridge response length.");
                    Console.WriteLine($"Bridge reply: command={report[4]:X2}; status={reply[4]:X2}; dataLength={length - 4}");
                    if (reply[4] != 0)
                        throw new FanToolException($"USB-I2C bridge returned status {reply[4]:X2}.");
                    return reply.AsSpan(5, length - 4).ToArray();
                }
                throw new FanToolException("No matching I2C bridge response.");
            }

            // Command1 is the native host's identification/I2C-open handshake.
            // Identity text is deliberately omitted from logs.
            var identity = await Exchange(Frame(1, 0));
            Console.WriteLine($"Bridge identity response received ({identity.Length} bytes).");

            async Task WriteDdc(byte[] payload, CancellationToken cancellationToken = default)
            {
                var packet = new byte[payload.Length + 3];
                packet[0] = 0x4f;
                packet[1] = (byte)(0x80 | payload.Length);
                payload.CopyTo(packet, 2);
                byte sum = 0x6e;
                foreach (var value in packet.AsSpan(0, packet.Length - 1)) sum ^= value;
                packet[^1] = sum;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await stream.WriteAsync(Frame(3, (byte)packet.Length, packet), timeout.Token);
                // Command3 has no completion reply. USB write completion only queues work;
                // a second request can overwrite the MCU's pending I2C transaction.
                await Task.Delay(180, timeout.Token);
            }

            async Task<byte[]> ReadFanField(byte field, CancellationToken cancellationToken = default)
            {
                if (field is not (2 or 3 or 4 or 5))
                    throw new InvalidOperationException("Fan getter is not allowlisted.");
                await WriteDdc([3, 0x18, field], cancellationToken);
                var reply = await Exchange(Frame(4, 16), cancellationToken);
                var expectedLength = field == 2 ? 1 : field == 5 ? 2 : 4;
                if (reply.Length < 4 + expectedLength || reply[0] != 0x18 || reply[1] != field ||
                    reply[3] != 0 || reply[2] != expectedLength)
                    throw new FanToolException("USB factory fan response failed validation.");
                return reply.AsSpan(4, reply[2]).ToArray();
            }

            // User-requested manual session: no service-window messages, no LG Switch,
            // no timed return while the owner remains alive. Only stop/exit restores auto.
            if (action is Lg39BridgeAction.ManualHold or Lg39BridgeAction.ManualAutomatic)
            {
                async Task SendAutomaticWithoutOsd()
                {
                    try { await WriteDdc([3, 0x18, 6, 1]); }
                    finally { await WriteDdc([3, 0x18, 6, 1]); }
                    Console.WriteLine("Automatic mode1 sent without opening service OSD. Live RPM not verified.");
                }
                if (action == Lg39BridgeAction.ManualAutomatic)
                {
                    await SendAutomaticWithoutOsd();
                    return;
                }
                if (stopEvent is null || manualReadyEventName is null || ownerProcessId <= 0)
                    throw new FanToolException("Manual hold requires explicit stop/readiness events and a live owner.");
                using var ready = EventWaitHandle.OpenExisting(manualReadyEventName);
                using var owner = Process.GetProcessById(ownerProcessId);
                if (!string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new FanToolException("Manual session owner is not this control application.");
                var range = await ReadFanField(3);
                var target = await ReadFanField(5);
                if (!range.AsSpan().SequenceEqual(new byte[] { 9, 0xc4, 1, 0xf4 }) ||
                    !target.AsSpan().SequenceEqual(new byte[] { 4, 0x4c }))
                    throw new FanToolException("Manual control requires the inspected500..2500 range and target1100.");
                try
                {
                    requestedStop.Token.ThrowIfCancellationRequested();
                    if (owner.HasExited) return;
                    await WriteDdc([3, 0x18, 6, 1], requestedStop.Token);
                    // Start each activation from automatic control rather than stacking reductions.
                    await Task.Delay(TimeSpan.FromSeconds(6), requestedStop.Token);
                    if (owner.HasExited) return;
                    var baselineValue = await ReadFanField(4, requestedStop.Token);
                    var baselineRpm = (baselineValue[0] << 8) | baselineValue[1];
                    if (baselineRpm is < 1000 or > 1200)
                        throw new FanToolException("Automatic baseline is not1000..1200RPM; no new reduction sent.");
                    Console.WriteLine($"Normal-mode baseline cachedRPM={baselineRpm}; waiting for measured change rather than a fixed pulse.");
                    using var rampDeadline = CancellationTokenSource.CreateLinkedTokenSource(requestedStop.Token);
                    rampDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var rampElapsed = Stopwatch.StartNew();
                    var reduced = false;
                    try
                    {
                        await WriteDdc([3, 0x18, 6, 2], rampDeadline.Token);
                        while (true)
                        {
                            if (owner.HasExited) return;
                            var sample = await ReadFanField(4, rampDeadline.Token);
                            var rpm = (sample[0] << 8) | sample[1];
                            var field2 = await ReadFanField(2, rampDeadline.Token);
                            Console.WriteLine($"Normal ramp t={rampElapsed.Elapsed.TotalSeconds:F2}s cachedRPM={rpm}; nativeField2={field2[0]}");
                            if (rpm < 850)
                                throw new FanToolException("RPM fell below the bounded activation threshold; automatic mode requested.");
                            if (rpm <= baselineRpm - 40)
                            {
                                reduced = true;
                                break;
                            }
                            await Task.Delay(250, rampDeadline.Token);
                        }
                    }
                    catch (OperationCanceledException) when (rampDeadline.IsCancellationRequested && !requestedStop.IsCancellationRequested) { }
                    if (!reduced)
                        throw new FanToolException("Normal-mode RPM did not decrease within20 seconds. Manual hold was not enabled.");
                    await WriteDdc([3, 0x18, 6, 0], requestedStop.Token);
                    Console.WriteLine("MANUAL_HOLD_ACTIVE: measured RPM decrease preceded mode0 request; waiting for user stop. No live RPM/temperature verification during hold.");
                    ready.Set();
                    using var ownerWaitCancellation = new CancellationTokenSource();
                    try
                    {
                        await Task.WhenAny(owner.WaitForExitAsync(ownerWaitCancellation.Token),
                            Task.Delay(Timeout.Infinite, requestedStop.Token));
                    }
                    finally { ownerWaitCancellation.Cancel(); }
                }
                catch (OperationCanceledException) when (requestedStop.IsCancellationRequested)
                {
                    Console.WriteLine("Manual session cancelled by user.");
                }
                finally { await SendAutomaticWithoutOsd(); }
                return;
            }

            async Task RestoreAutomatic(bool allowRecovery = true, bool closeWindow = true)
            {
                // Always attempt the mode write even if closing the OSD fails, and vice versa.
                try
                {
                    await WriteDdc([3, 0x18, 6, 1]);
                    if (allowRecovery)
                    {
                        // Start a fresh service context; RPM can otherwise remain stale.
                        await WriteDdc([3, 2, 0x10, 0x5b, 0]);
                        await WriteDdc([3, 2, 0xa8, 0, 0]);
                        await WriteDdc([3, 0x18, 6, 1]);
                        var elapsed = Stopwatch.StartNew();
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        var recovered = false;
                        try
                        {
                            while (true)
                            {
                                var value = await ReadFanField(4, deadline.Token);
                                var rpm = (value[0] << 8) | value[1];
                                Console.WriteLine($"Automatic recovery cachedRPM={rpm}");
                                if (elapsed.Elapsed.TotalSeconds >= 2 && rpm is >= 1050 and <= 1150)
                                {
                                    recovered = true;
                                    break;
                                }
                                await Task.Delay(300, deadline.Token);
                            }
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                        if (!recovered)
                            throw new FanToolException("Automatic mode was sent but RPM did not recover near1100 within12 seconds. No further lowering is allowed.");
                    }
                }
                finally
                {
                    if (closeWindow) await WriteDdc([3, 2, 0x10, 0x5b, 0]);
                }
                Console.WriteLine(allowRecovery
                    ? "Automatic fan mode1 restored; fresh service-context RPM recovered near1100; service window closed."
                    : "Automatic mode1 command sent; recovery verification belongs to the independent worker.");
            }

            if (restoreOnly)
            {
                if (action is Lg39BridgeAction.RestoreWatchdog or Lg39BridgeAction.HoldRestoreWatchdog)
                {
                    if (watchdogReadyEvent is null)
                        throw new FanToolException("Missing watchdog readiness event.");
                    using var ready = EventWaitHandle.OpenExisting(watchdogReadyEvent);
                    ready.Set();
                    await Task.Delay(TimeSpan.FromSeconds(action == Lg39BridgeAction.HoldRestoreWatchdog ? 22 : 8));
                }
                try { await RestoreAutomatic(); }
                finally
                {
                    await Task.Delay(300);
                    await RestoreAutomatic(allowRecovery: false);
                }
                return;
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await stream.WriteAsync(Frame(3, 5, [0x51, 0x82, 0x01, 0x10, 0xac]), timeout.Token);
            await Task.Delay(180);
            var brightness = await Exchange(Frame(4, 11));
            Console.WriteLine($"Brightness reply: {Convert.ToHexString(brightness)}");
            byte checksum = 0x50;
            foreach (var value in brightness) checksum ^= value;
            if (brightness.Length != 11 || brightness[0] != 0x6e || brightness[1] != 0x88 ||
                brightness[2] != 2 || brightness[3] != 0 || brightness[4] != 0x10 || checksum != 0)
                throw new FanToolException("Bridge brightness response failed DDC framing/checksum validation.");
            Console.WriteLine($"USB I2C bridge verified. Brightness={(brightness[8] << 8) | brightness[9]}. No fan setting was sent.");
            if (action is Lg39BridgeAction.FanReadback or Lg39BridgeAction.BoundedFanTest or Lg39BridgeAction.BoundedHoldTest)
            {
                var values = new Dictionary<byte, byte[]>();
                foreach (byte field in new byte[] { 3, 4, 5 })
                {
                    var reply = await ReadFanField(field);
                    values[field] = reply;
                    Console.WriteLine($"Fan field{field}: {Convert.ToHexString(reply)}");
                    var first = (reply[0] << 8) | reply[1];
                    if (field == 3)
                        Console.WriteLine($"Configured RPM range: {((reply[2] << 8) | reply[3])}..{first}");
                    else if (field == 4)
                        Console.WriteLine($"Cached RPM samples: {first}, {((reply[2] << 8) | reply[3])}. These are not synchronous tachometer reads.");
                    else
                        Console.WriteLine($"Native target RPM: {first}");
                }
                if (fanTest)
                {
                    static int Word(byte[] value) => (value[0] << 8) | value[1];
                    if (!values[3].AsSpan().SequenceEqual(new byte[] { 0x09, 0xc4, 0x01, 0xf4 }) ||
                        Word(values[5]) != 1100 || Word(values[4]) is < 1000 or > 1200)
                        throw new FanToolException("Fan test requires the inspected 500..2500 range, target1100 and cached RPM1000..1200. No test mode sent.");

                    var eventName = @"Local\MonitorFanControl.Lg39Restore." + Guid.NewGuid().ToString("N");
                    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                    var executable = Path.Combine(AppContext.BaseDirectory, "MonitorFanControl.exe");
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(holdTest ? "--restore39-hold-watchdog" : "--restore39-native-watchdog");
                    start.ArgumentList.Add(eventName);
                    watchdog = Process.Start(start) ?? throw new FanToolException("Could not launch independent automatic-mode restore worker.");
                    if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new FanToolException("Restore worker did not open the bridge. No fan test mode sent.");
                    var restoreLease = Stopwatch.StartNew();
                    Console.WriteLine($"Independent restore worker ready; its automatic-mode deadline is{(holdTest ? 22 : 8)} seconds.");
                    try
                    {
                        requestedStop.Token.ThrowIfCancellationRequested();
                        await WriteDdc([3, 0x18, 6, 1]);
                        await WriteDdc([3, 2, 0xa8, 0, 0]);
                        await Task.Delay(700);
                        var before = Word(await ReadFanField(4));
                        Console.WriteLine($"Pre-test cached RPM: {before}");
                        if (before is < 1000 or > 1200)
                            throw new FanToolException("RPM outside test baseline. No mode2 sent.");
                        if (restoreLease.Elapsed >= TimeSpan.FromSeconds(2))
                            throw new FanToolException("Pre-test setup was too slow for the independent restore deadline. No mode2 sent.");
                        requestedStop.Token.ThrowIfCancellationRequested();
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestedStop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(holdTest ? 2.4 : 3));
                        var elapsed = Stopwatch.StartNew();
                        var samples = new List<int>();
                        try
                        {
                            await WriteDdc([3, 0x18, 6, 2], deadline.Token);
                            while (true)
                            {
                                var rpm = Word(await ReadFanField(4, deadline.Token));
                                samples.Add(rpm);
                                Console.WriteLine($"Test t={elapsed.Elapsed.TotalSeconds:F2}s cachedRPM={rpm}");
                                if (rpm < 950) break;
                                await Task.Delay(200, deadline.Token);
                            }
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                        {
                            Console.WriteLine("Bounded ramp deadline reached.");
                        }
                        Console.WriteLine($"Bounded test ended after {elapsed.Elapsed.TotalSeconds:F2}s; cached samples=[{string.Join(',', samples)}]. No fixed target RPM was set.");
                        if (holdTest)
                        {
                            if (samples.Count == 0 || samples[^1] is < 900 or > 1200)
                                throw new FanToolException("No suitable cached baseline for a bounded hold. Returning to automatic mode.");
                            // Reference mode0 skips regulation without zeroing the PWM driver.
                            // Its frozen cache is deliberately not collected as evidence of speed.
                            await WriteDdc([3, 0x18, 6, 0]);
                            var holdElapsed = Stopwatch.StartNew();
                            Console.WriteLine("Mode0 hold started for10 seconds. RPM readback is suspended and cannot verify holding speed.");
                            await Task.Delay(TimeSpan.FromSeconds(10));
                            await WriteDdc([3, 0x18, 6, 1]);
                            Console.WriteLine($"Mode0 hold ended after{holdElapsed.Elapsed.TotalSeconds:F2}s; automatic mode1 resumed for fresh samples.");
                            using var observationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var observationElapsed = Stopwatch.StartNew();
                            var observed = new List<int>();
                            try
                            {
                                while (true)
                                {
                                    var rpm = Word(await ReadFanField(4, observationDeadline.Token));
                                    observed.Add(rpm);
                                    Console.WriteLine($"After resume t={observationElapsed.Elapsed.TotalSeconds:F2}s cachedRPM={rpm}");
                                    await Task.Delay(220, observationDeadline.Token);
                                }
                            }
                            catch (OperationCanceledException) when (observationDeadline.IsCancellationRequested) { }
                            Console.WriteLine($"Post-resume samples=[{string.Join(',', observed)}]. These include filtering and automatic correction; no sustained chosen RPM is certified.");
                        }
                    }
                    catch (OperationCanceledException) when (requestedStop.IsCancellationRequested)
                    {
                        Console.WriteLine("User requested automatic mode before the ramp started.");
                    }
                    // Parent restores immediately. The already-open worker keeps the service
                    // context active, confirms recovery and closes OSD even if the parent exits.
                    finally { await RestoreAutomatic(allowRecovery: false, closeWindow: false); }
                }
            }
        }
        finally
        {
            if (lockTaken) InterprocessExchangeLock.Release();
            if (watchdog is not null)
            {
                using (watchdog)
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await watchdog.WaitForExitAsync(wait.Token);
                    if (watchdog.ExitCode != 0)
                        throw new FanToolException($"Independent restore worker failed (exit{watchdog.ExitCode}); run --restore39-native-auto.");
                    Console.WriteLine("Independent restore worker completed successfully.");
                }
            }
        }
        }
    }
}
