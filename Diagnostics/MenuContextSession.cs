using System.Diagnostics;
using System.IO;

namespace MonitorFanControl;

public sealed record MenuDeviceIdentity(int Count, ushort Vid, ushort Pid, ushort Revision,
    ushort UsagePage, ushort Usage, ushort InputLength, ushort OutputLength);
public interface IMenuTransport : IAsyncDisposable
{
    MenuDeviceIdentity Identity { get; }
    Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token);
}
public interface IMenuCleanupTakeover
{
    // Called only after the session's independent worker is confirmed exited.
    // Failure to reserve the transport must prevent every fallback submission.
    Task ReserveAfterWorkerExitAsync(CancellationToken token);
}
public interface IMenuClock
{
    long Milliseconds { get; }
    Task DelayAsync(int milliseconds, CancellationToken token);
}
public sealed class MenuSystemClock : IMenuClock
{
    private readonly Stopwatch _time = Stopwatch.StartNew();
    public long Milliseconds => _time.ElapsedMilliseconds;
    public Task DelayAsync(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);
}
public sealed record MenuLogEntry(long Milliseconds, string Kind, string Detail, string Phase = "lifecycle")
{
    // Original monotonic event time when recovery defers external logging.
    public long QpcTimestamp { get; init; }
}
public sealed record MenuCleanupResult(bool CommandsSubmitted, string Detail, bool WorkerFailed = false);
public sealed class MenuPreconditionException(string safeReason) : Exception(safeReason);
public interface IMenuWorker : IAsyncDisposable
{
    IMenuTransport Transport { get; }
    Task ArmAsync(CancellationToken token);
    Task<MenuCleanupResult> FinishAsync(CancellationToken token);
}
public enum MenuRunState { PreconditionFailed, Completed, Interrupted, Error }
public enum MenuObservation { Missing, Present, Absent, Unclear }
public sealed record MenuPhaseObservation(MenuObservation Noise, MenuObservation Menu);
public sealed record MenuObservations(MenuPhaseObservation A, MenuPhaseObservation B, MenuPhaseObservation C);
public sealed record MenuSessionResult(MenuRunState State, string Detail, int? CachedRpm,
    MenuCleanupResult? Cleanup, MenuObservations? Observations = null,
    MenuObservation ClosureAfterCleanup = MenuObservation.Missing)
{
    public bool PhysicalClosureConfirmed => ClosureAfterCleanup == MenuObservation.Missing
        ? Observations?.C.Menu == MenuObservation.Absent : ClosureAfterCleanup == MenuObservation.Absent;
    public string AcousticResult
    {
        get
        {
            if (State != MenuRunState.Completed || Observations is not { } o) return "undetermined";
            if (o.A.Noise == MenuObservation.Absent) return "uninformative-baseline";
            if (o.A.Noise != MenuObservation.Present || o.A.Menu != MenuObservation.Absent ||
                o.B.Menu != MenuObservation.Present || o.C.Menu != MenuObservation.Absent ||
                o.B.Noise is MenuObservation.Missing or MenuObservation.Unclear ||
                o.C.Noise is MenuObservation.Missing or MenuObservation.Unclear) return "undetermined";
            if (o.B.Noise == MenuObservation.Absent)
                return o.C.Noise == MenuObservation.Absent ? "quiet-observed-through-five-second-C" : "quiet-observed-in-B-only";
            return "no-quiet-observed-in-B";
        }
    }
}
public static class MenuContextSession
{
    public const int WorkerLifetimeMs = 60000;
    public const int OperationTimeoutMs = 3000;
    // 15s worker finish + 5s reservation + 12s independent admission deadline
    // + four uncancelled gaps (each capped at 1s), with scheduling margin.
    public const int FinishTimeoutMs = 38000;
    public static async Task<MenuSessionResult> RunAsync(Func<CancellationToken, Task<IMenuWorker>> startWorker,
        IMenuClock clock, Func<CancellationToken, Task<MenuObservation>> confirmPreparedNoise,
        Action<MenuLogEntry> log, CancellationToken token = default)
    {
        IMenuWorker? worker = null;
        var armed = false;
        int? cached = null;
        var state = MenuRunState.PreconditionFailed;
        var detail = "Preconditions were not completed.";
        MenuCleanupResult? cleanup = null;
        var began = clock.Milliseconds;
        var currentPhase = "preflight";
        void Emit(MenuLogEntry entry) => log(entry with { Phase = currentPhase });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(WorkerLifetimeMs - 15000);
        try
        {
            worker = await startWorker(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            CheckDeadline();
            var protocol = new MenuProtocol(worker.Transport, clock, Emit);
            await protocol.ValidateAsync(deadline.Token);
            cached = await protocol.CachedRpmAsync(deadline.Token);
            CheckDeadline();
            currentPhase = "preparation";
            await worker.ArmAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            armed = true;
            await protocol.SetAsync(MenuCommand.Automatic, deadline.Token);
            await protocol.SetAsync(MenuCommand.Close, deadline.Token);
            Emit(new(clock.Milliseconds, "preparation", "Automatic1 and closed menu requested; physical state unconfirmed."));
            var baseline = await confirmPreparedNoise(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            Emit(new(clock.Milliseconds, "prepared-noise", baseline.ToString()));
            if (baseline != MenuObservation.Present)
                detail = "Prepared target noise absent or uncertain; comparison not started.";
            else
            {
                await Phase("A");
                CheckDeadline();
                currentPhase = "A-to-B";
                await protocol.SetAsync(MenuCommand.Open, deadline.Token);
                await Phase("B");
                currentPhase = "B-to-C";
                await protocol.SetAsync(MenuCommand.Close, deadline.Token);
                await Phase("C");
                currentPhase = "postflight";
                cached = await protocol.CachedRpmAsync(deadline.Token);
                state = MenuRunState.Completed;
                detail = "Software sequence completed; acoustic result and physical closure need separate observations.";
            }
            async Task Phase(string phase)
            {
                deadline.Token.ThrowIfCancellationRequested();
                CheckDeadline();
                currentPhase = phase;
                Emit(new(clock.Milliseconds, "phase-start", phase));
                await clock.DelayAsync(5000, deadline.Token);
                Emit(new(clock.Milliseconds, "phase-end", phase));
            }
        }
        catch (OperationCanceledException)
        {
            state = token.IsCancellationRequested ? MenuRunState.Interrupted : MenuRunState.Error;
            detail = token.IsCancellationRequested ? "Interrupted; bounded cleanup requested." : "Session deadline expired.";
        }
        catch (Exception ex)
        {
            state = armed ? MenuRunState.Error : MenuRunState.PreconditionFailed;
            // Exception messages can contain device paths/serials. Log only the type.
            detail = ex is MenuPreconditionException || ex is InvalidDataException
                ? ex.Message : $"{(armed ? "Session" : "Precondition")} failed ({ex.GetType().Name}).";
        }
        finally
        {
            currentPhase = "cleanup";
            deadline.Cancel();
            if (worker is not null)
            {
                try
                {
                    cleanup = await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(FinishTimeoutMs));
                    if (armed && (!cleanup.CommandsSubmitted || cleanup.WorkerFailed))
                    {
                        state = MenuRunState.Error;
                        detail = cleanup.WorkerFailed ? "Independent cleanup worker failed; parent cleanup status is recorded separately; physical closure unconfirmed."
                            : "Cleanup commands could not all be submitted; physical closure unconfirmed.";
                    }
                }
                catch (Exception ex)
                {
                    cleanup = new(false, $"Cleanup unconfirmed ({ex.GetType().Name}).");
                    state = MenuRunState.Error;
                    detail = "Worker completion unconfirmed; close the menu using the monitor's ordinary control if needed.";
                }
                try { await worker.DisposeAsync(); }
                catch (Exception ex) { state = MenuRunState.Error; detail = $"Worker disposal unconfirmed ({ex.GetType().Name})."; }
            }
        }
        Emit(new(clock.Milliseconds, "result", $"{state}: {detail}"));
        return new(state, detail, cached, cleanup);
        void CheckDeadline()
        {
            if (clock.Milliseconds - began >= WorkerLifetimeMs - 15000)
                throw new TimeoutException("Monotonic session deadline expired.");
        }
    }
    public static async Task<MenuCleanupResult> CleanupAsync(IMenuTransport transport, IMenuClock clock,
        Action<MenuLogEntry> log, CancellationToken token)
    {
        var protocol = new MenuProtocol(transport, clock, entry => log(entry with { Phase = "cleanup" }));
        var auto = false;
        var close = false;
        // Both actions are attempted in each round, regardless of a preceding failure.
        for (var attempt = 0; attempt < 2; attempt++)
            foreach (var command in new[] { MenuCommand.Automatic, MenuCommand.Close })
            {
                try
                {
                    await protocol.SetAsync(command, token);
                    if (command == MenuCommand.Automatic) auto = true; else close = true;
                }
                catch (Exception ex) { log(new(clock.Milliseconds, "cleanup-error", $"{command}: {ex.GetType().Name}", "cleanup")); }
            }
        return new(auto && close, auto && close ? "Automatic1 and close submitted; physical result unconfirmed." : "One or both cleanup commands not submitted; physical result unconfirmed.");
    }
}

// This closed vocabulary has no mode value or arbitrary factory payload entry point.
internal enum MenuCommand { Automatic, Open, Close }
internal sealed class MenuProtocol(IMenuTransport transport, IMenuClock clock, Action<MenuLogEntry> log,
    int operationTimeoutMs = MenuContextSession.OperationTimeoutMs)
{
    private byte _tag = (byte)Random.Shared.Next(1, 220);
    public async Task ValidateAsync(CancellationToken token)
    {
        if (transport.Identity != new MenuDeviceIdentity(1, 0x043e, 0x9c33, 0x0111, 0xff00, 1, 65, 65))
            throw new InvalidDataException("Unexpected controller identity or descriptor.");
        if ((await ExchangeAsync(Frame(1, 0), token)).Length == 0)
            throw new InvalidDataException("Empty bridge identity response.");
        await WriteAsync([0x51, 0x82, 1, 0x10, 0xac], token);
        var sanity = await ExchangeAsync(Frame(4, 11), token);
        byte checksum = 0x50;
        foreach (var value in sanity) checksum ^= value;
        if (sanity.Length != 11 || sanity[0] != 0x6e || sanity[1] != 0x88 || sanity[2] != 2 || sanity[3] != 0 || sanity[4] != 0x10 || checksum != 0)
            throw new InvalidDataException("Invalid brightness sanity response.");
        var range = await ReadFieldAsync(3, token);
        var target = await ReadFieldAsync(5, token);
        if (!range.SequenceEqual(new byte[] { 0x09, 0xc4, 0x01, 0xf4 }) || !target.SequenceEqual(new byte[] { 0x04, 0x4c }))
            throw new InvalidDataException("Unexpected configured range or target.");
    }
    public async Task<int> CachedRpmAsync(CancellationToken token)
    {
        var data = await ReadFieldAsync(4, token);
        var rpm = (data[0] << 8) | data[1];
        log(new(clock.Milliseconds, "cached-rpm", $"{rpm}; reference only; not a fresh tachometer or protection confirmation."));
        return rpm;
    }
    public Task SetAsync(MenuCommand command, CancellationToken token) => WriteAsync(Ddc(command switch
    {
        MenuCommand.Automatic => [3, 0x18, 6, 1],
        MenuCommand.Open => [3, 2, 0xa8, 0, 0],
        MenuCommand.Close => [3, 2, 0x10, 0x5b, 0],
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    }), token);
    private async Task<byte[]> ReadFieldAsync(byte field, CancellationToken token)
    {
        await WriteAsync(Ddc([3, 0x18, field]), token);
        var data = await ExchangeAsync(Frame(4, 16), token);
        var length = field == 5 ? 2 : 4;
        if (data.Length < 4 + length || data[0] != 0x18 || data[1] != field || data[2] != length || data[3] != 0)
            throw new InvalidDataException("Invalid factory getter response.");
        return data.AsSpan(4, length).ToArray();
    }
    internal async Task WriteAsync(byte[] packet, CancellationToken token)
    {
        log(new(clock.Milliseconds, "command-start", Convert.ToHexString(packet)));
        var submitted = false;
        try
        {
            await transport.TransferAsync(Frame(3, (byte)packet.Length, packet), false, token)
                .WaitAsync(TimeSpan.FromMilliseconds(operationTimeoutMs), token);
            log(new(clock.Milliseconds, "command-submitted", Convert.ToHexString(packet)));
            submitted = true;
        }
        catch (Exception ex) { log(new(clock.Milliseconds, "command-error", $"{Convert.ToHexString(packet)}: {ex.GetType().Name}")); throw; }
        finally
        {
            // A failed/cancelled write may already be queued. Never cancel its quiet gap.
            // The observer/journal is fallible. Its failure must propagate only
            // after this additional recorded gap, even when native IO succeeded.
            try { log(new(clock.Milliseconds, "gap-start", "180ms after possible command3 submission")); }
            finally { await clock.DelayAsync(180, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
            log(new(clock.Milliseconds, "gap-end", "180ms"));
            log(new(clock.Milliseconds, "command-end", submitted ? "submitted; physical state unconfirmed" : "failed or uncertain submission"));
        }
    }
    private async Task<byte[]> ExchangeAsync(byte[] frame, CancellationToken token)
    {
        log(new(clock.Milliseconds, "exchange-start", $"bridge command {frame[4]}"));
        var data = await transport.TransferAsync(frame, true, token)
            .WaitAsync(TimeSpan.FromMilliseconds(operationTimeoutMs), token);
        if (data.Length < 5 || data[0] != 0 || data[1] < 4 || data[1] > 64 || data.Length < data[1] + 1 ||
            data[2] != frame[2] || data[3] != 0x39 || data[4] != 0)
            throw new InvalidDataException("Malformed or foreign bridge response.");
        log(new(clock.Milliseconds, "exchange-end", "validated; identity text omitted"));
        return data.AsSpan(5, data[1] - 4).ToArray();
    }
    private byte[] Frame(byte command, byte count, byte[]? packet = null)
    {
        packet ??= [];
        var frame = new byte[65];
        frame[1] = (byte)(8 + packet.Length); frame[2] = ++_tag; frame[3] = 0x39;
        frame[4] = command; frame[5] = count; frame[7] = 0x0b; frame[8] = 0x37;
        packet.CopyTo(frame, 9);
        return frame;
    }
    internal static byte[] Ddc(byte[] payload)
    {
        var packet = new byte[payload.Length + 3]; packet[0] = 0x4f; packet[1] = (byte)(0x80 | payload.Length);
        payload.CopyTo(packet, 2);
        byte checksum = 0x6e;
        foreach (var value in packet.AsSpan(0, packet.Length - 1)) checksum ^= value;
        packet[^1] = checksum;
        return packet;
    }
}
