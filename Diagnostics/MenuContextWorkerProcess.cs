using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MonitorFanControl;

public static class MenuContextWorkerProcess
{
    private static readonly AsyncLocal<string?> WorkerSession = new();
    internal static string? WorkerNonce => WorkerSession.Value;
    private const int ReadyTimeoutMs = 5000;
    private const int FinishTimeoutMs = 15000;
    internal static string HidReleaseMarkerName(string nonce) => @"Local\MonitorFanControl.Menu39.HidReleaseStarted." + nonce;
    private sealed record Message(string Kind, MenuDeviceIdentity? Identity = null,
        MenuCleanupResult? Cleanup = null, string? Detail = null);

    public static Task<IMenuWorker> StartAsync(Action<MenuLogEntry> log, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var child = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        child.ArgumentList.Add("--menu39-worker");
        child.ArgumentList.Add(nonce);
        child.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SafeLog(log, new(0, "worker-session", nonce));
        return StartCoreAsync(child, nonce, log, token, nativeParent: true);
    }

    // The harness overload exercises real independent process lifetime without
    // opening HID. Its transport deliberately cannot send physical commands.
    public static Task<IMenuWorker> StartAsync(ProcessStartInfo child, string nonce,
        Action<MenuLogEntry> log, CancellationToken token) => StartCoreAsync(child, nonce, log, token, nativeParent: false);

    public static Task<IMenuWorker> StartAsync(ProcessStartInfo child, string nonce, IMenuTransport testParentTransport,
        Action<MenuLogEntry> log, CancellationToken token) =>
        StartCoreAsync(child, nonce, log, token, nativeParent: false, testParentTransport);

    private static async Task<IMenuWorker> StartCoreAsync(ProcessStartInfo child, string nonce,
        Action<MenuLogEntry> log, CancellationToken token, bool nativeParent, IMenuTransport? testParentTransport = null)
    {
        ValidateNonce(nonce);
        // Retain this marker before process start: it must survive even if the
        // worker exits between its ready message and parent transport setup.
        using var releaseStarted = nativeParent ? new EventWaitHandle(false, EventResetMode.ManualReset, HidReleaseMarkerName(nonce)) : null;
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        readyTimeout.CancelAfter(ReadyTimeoutMs);
        if (nativeParent) await MenuContextPlatformProcesses.EnsureCompatibleAsync(readyTimeout.Token);
        child.UseShellExecute = false;
        child.CreateNoWindow = true;
        var pipe = new NamedPipeServerStream(PipeName(nonce), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        IMenuTransport? transport = null;
        try
        {
            if (nativeParent) HidFanController.RetainMenuContextHidReservation();
            process = Process.Start(child) ?? throw new InvalidOperationException("Could not start independent menu cleanup worker.");
            await pipe.WaitForConnectionAsync(readyTimeout.Token);
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var ready = await ReadAsync(reader, readyTimeout.Token);
            if (ready.Kind != "ready" || ready.Identity is null)
                throw new MenuPreconditionException(SafeReadyReason(ready.Detail));
            transport = nativeParent ? HidFanController.OpenMenuContextTransport(nonce) : testParentTransport ?? new HarnessParentTransport(ready.Identity);
            if (transport.Identity != ready.Identity) throw new MenuPreconditionException("Parent and cleanup worker bridge identities differ.");
            return new Worker(process, pipe, reader, writer, transport, log);
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync();
            await pipe.DisposeAsync();
            if (process is not null)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { SafeLog(log, new(0, "worker", "Startup failed; worker exit was not confirmed.")); }
                process.Dispose();
            }
            throw;
        }
    }

    public static async Task<int> RunAsync(string nonce, int parentPid, Func<IMenuTransport> factory, Action<MenuLogEntry> log)
    {
        ValidateNonce(nonce);
        if (parentPid <= 0 || parentPid == Environment.ProcessId) return 2;
        var clock = new MenuSystemClock();
        using var lifetime = new CancellationTokenSource(MenuContextSession.WorkerLifetimeMs);
        using var parentLost = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, parentLost.Token);
        using var pipe = new NamedPipeClientStream(".", PipeName(nonce), PipeDirection.InOut, PipeOptions.Asynchronous);
        IMenuTransport? transport = null;
        var armed = false;
        MenuCleanupResult? cleanup = null;
        var noCleanupRequired = false;
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentPid);
            // Capture a process handle before ready so PID reuse after ready
            // cannot redirect the parent's exit observation to another process.
            _ = parent.Handle;
            _ = ObserveParentExitAsync(parent, parentLost, lifetime.Token);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                connect.CancelAfter(ReadyTimeoutMs);
                await pipe.ConnectAsync(connect.Token);
            }
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            WorkerSession.Value = nonce;
            transport = factory();
            WorkerSession.Value = null;
            await WriteBoundedAsync(writer, new("ready", Identity: transport.Identity), stop.Token);
            while (true)
            {
                // Idle phase waits use the absolute lifetime, not a three-second
                // transaction timeout: each listening phase lasts five seconds.
                var message = await ReadAsync(reader, stop.Token);
                if (message.Kind == "arm" && !armed)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    armed = true;
                    SafeLog(log, new(clock.Milliseconds, "worker-armed", "Independent auto1/close cleanup is armed."));
                    await WriteBoundedAsync(writer, new("armed"), stop.Token);
                }
                else if (message.Kind == "finish")
                {
                    if (armed)
                    {
                        cleanup = await CleanupIndependentAsync(transport, clock, log);
                        armed = false;
                    }
                    else
                    {
                        noCleanupRequired = true;
                        cleanup = new(false, "Worker stopped before arming; no cleanup commands were required.");
                    }
                    await WriteBoundedAsync(writer, new("finished", Cleanup: cleanup), CancellationToken.None);
                    break;
                }
                else throw new InvalidDataException("Unsupported menu worker lifecycle message.");
            }
        }
        catch (Exception ex)
        {
            WorkerSession.Value = null;
            SafeLog(log, new(clock.Milliseconds, "worker-stop", ex.GetType().Name));
            if (armed && transport is not null)
            {
                cleanup = await CleanupIndependentAsync(transport, clock, log);
                armed = false;
            }
            if (pipe.IsConnected)
            {
                try
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    await WriteBoundedAsync(writer, new("error", Cleanup: cleanup,
                        Detail: ex is MenuPreconditionException ? ex.Message : "Worker preconditions could not be inspected."), CancellationToken.None);
                }
                catch (Exception) { }
            }
            return cleanup?.CommandsSubmitted == true ? 0 : 1;
        }
        finally
        {
            lifetime.Cancel();
            parent?.Dispose();
            if (transport is not null) await transport.DisposeAsync();
        }
        return cleanup?.CommandsSubmitted == true || noCleanupRequired ? 0 : 1;
    }

    private static async Task<MenuCleanupResult> CleanupIndependentAsync(IMenuTransport transport,
        IMenuClock clock, Action<MenuLogEntry> log)
    {
        // This token is independent of the parent's canceled token and of the
        // session lifetime. Core cleanup bounds each attempt separately.
        using var limit = new CancellationTokenSource(12000);
        try
        {
            // The parent may have died during the MCU settling interval. Its
            // abandoned transaction mutex alone does not prove that gap elapsed.
            SafeLog(log, new(clock.Milliseconds, "cleanup-takeover-gap-start", "180ms before independent cleanup"));
            await clock.DelayAsync(180, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            SafeLog(log, new(clock.Milliseconds, "cleanup-takeover-gap-end", "180ms"));
            return await MenuContextSession.CleanupAsync(transport, clock, entry => SafeLog(log, entry), limit.Token);
        }
        catch (Exception ex) { return new(false, "Bounded cleanup failed; physical result is unconfirmed: " + ex.GetType().Name); }
    }

    private static async Task ObserveParentExitAsync(Process parent, CancellationTokenSource lost, CancellationToken token)
    {
        try { await parent.WaitForExitAsync(token); lost.Cancel(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { try { lost.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private sealed class Worker(Process process, NamedPipeServerStream pipe, StreamReader reader,
        StreamWriter writer, IMenuTransport transport, Action<MenuLogEntry> log) : IMenuWorker
    {
        private readonly SemaphoreSlim _rpc = new(1, 1);
        private bool _armed;
        private bool _finished;
        private bool _disposed;
        private MenuCleanupResult? _result;
        public IMenuTransport Transport { get; } = new ArmGuard(transport, () => false);

        internal void InitializeGuard() => ((ArmGuard)Transport).CanOpen = () => _armed && !_finished && !process.HasExited;

        public async Task ArmAsync(CancellationToken token)
        {
            InitializeGuard();
            var response = await RequestAsync(new("arm"), token);
            if (response.Kind != "armed") throw new IOException(response.Detail ?? "Cleanup worker arm was not acknowledged.");
            _armed = true;
        }

        public async Task<MenuCleanupResult> FinishAsync(CancellationToken token)
        {
            if (_finished) return _result ?? new(false, "Worker finish was not confirmed.");
            // No further parent command is accepted once finish starts.
            _finished = true;
            var confirmedExited = false;
            using var finish = new CancellationTokenSource(FinishTimeoutMs);
            try
            {
                var response = await RequestAsync(new("finish"), finish.Token, FinishTimeoutMs);
                _result = response.Cleanup ?? new(false, response.Detail ?? "Worker cleanup was not reported.");
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync(); // EOF independently activates armed worker cleanup.
                _result = new(false, "Worker cleanup result is unconfirmed: " + ex.GetType().Name);
            }
            try
            {
                await process.WaitForExitAsync(finish.Token);
                confirmedExited = true;
                if (process.ExitCode != 0) _result = new(_result.CommandsSubmitted, $"Worker exited with code {process.ExitCode}; completion unconfirmed.", WorkerFailed: true);
            }
            catch (Exception ex) { _result = new(false, "Worker exit was not confirmed: " + ex.GetType().Name); }
            if (_armed && confirmedExited && _result.CommandsSubmitted != true)
            {
                var workerFailure = _result.Detail;
                try
                {
                    if (transport is not IMenuCleanupTakeover takeover)
                        throw new IOException("Parent transport cannot reserve worker-loss cleanup.");
                    using var reservation = new CancellationTokenSource(5000);
                    await takeover.ReserveAfterWorkerExitAsync(reservation.Token).WaitAsync(TimeSpan.FromSeconds(5), reservation.Token);
                    SafeLog(log, new(0, "parent-cleanup-takeover", "Worker exit confirmed; parent transport reserved for automatic1/close only."));
                    var fallback = await CleanupIndependentAsync(transport, new MenuSystemClock(), log);
                    _result = new(fallback.CommandsSubmitted, workerFailure + " Parent fallback: " + fallback.Detail, WorkerFailed: true);
                }
                catch (Exception ex)
                {
                    _result = new(false, workerFailure + " Parent fallback unavailable: " + ex.GetType().Name, WorkerFailed: true);
                }
            }
            SafeLog(log, new(0, "worker-finished", _result.Detail));
            return _result;
        }

        private async Task<Message> RequestAsync(Message message, CancellationToken token, int timeoutMs = 3000)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            await _rpc.WaitAsync(timeout.Token);
            try
            {
                await WriteBoundedAsync(writer, message, timeout.Token);
                return await ReadAsync(reader, timeout.Token);
            }
            finally { _rpc.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await FinishAsync(CancellationToken.None);
            await transport.DisposeAsync();
            reader.Dispose();
            try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            await pipe.DisposeAsync();
            process.Dispose();
            _rpc.Dispose();
        }
    }

    private sealed class ArmGuard(IMenuTransport inner, Func<bool> canOpen) : IMenuTransport
    {
        internal Func<bool> CanOpen { get; set; } = canOpen;
        public MenuDeviceIdentity Identity => inner.Identity;
        public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token)
        {
            if (report.Length >= 17 && report.AsSpan(9, 8).SequenceEqual(new byte[] { 0x4f, 0x85, 3, 2, 0xa8, 0, 0, 0x0d }) && !CanOpen())
                throw new InvalidOperationException("The independent cleanup worker is not armed for menu opening.");
            return inner.TransferAsync(report, reply, token);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HarnessParentTransport(MenuDeviceIdentity identity) : IMenuTransport
    {
        public MenuDeviceIdentity Identity => identity;
        public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token) =>
            throw new InvalidOperationException("The lifecycle harness parent has no hardware transport.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<Message> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var character = new char[1];
        using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = false;
        while (true)
        {
            var count = await reader.ReadAsync(character.AsMemory(), started ? messageTimeout.Token : token);
            if (count == 0) throw new EndOfStreamException("Menu parent/worker pipe closed.");
            if (!started) { messageTimeout.CancelAfter(3000); started = true; }
            if (character[0] == '\n') break;
            if (text.Length >= 4096) throw new InvalidDataException("Oversized menu worker message.");
            if (character[0] != '\r') text.Append(character[0]);
        }
        return JsonSerializer.Deserialize<Message>(text.ToString()) ?? throw new InvalidDataException("Empty menu worker message.");
    }

    private static async Task WriteBoundedAsync(StreamWriter writer, Message message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(3000);
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
    }

    private static void SafeLog(Action<MenuLogEntry> log, MenuLogEntry entry)
    {
        // A broken redirected stdout or a failed log must never skip cleanup.
        try { log(entry); } catch (Exception) { }
    }
    private static string PipeName(string nonce) => "MonitorFanControl.Menu39." + nonce;
    private static string SafeReadyReason(string? reason) => reason switch
    {
        "Another menu diagnostic session is active." or
        "Another HID diagnostic is active." or
        "Another NVIDIA/DDC diagnostic is active." or
        "The menu diagnostic requires exactly one LG 9C33 I2C bridge." or
        "Opening the menu diagnostic bridge failed; the device may be occupied or unavailable." or
        "The menu diagnostic bridge identity changed." or
        "The menu diagnostic bridge descriptor changed; no report sent." or
        "Reading the menu bridge descriptor failed." or
        "Menu bridge setup exceeded its bounded readiness wait." => reason,
        _ => "Worker preconditions could not be inspected."
    };
    private static void ValidateNonce(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid menu worker session nonce.");
    }
}
