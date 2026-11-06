using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

// The owned-writer variant retains the exact writer identity before ready, and
// revokes, then confirms writer exit or retires an idle cooperative writer
// before cleanup can enter the same gate and retained reservations.
public static class ShortRampGuardianProcess
{
    private const int ReadyTimeoutMs = 5000;
    private const int FinishTimeoutMs = 6000;
    private sealed record Message(string Kind, MenuDeviceIdentity? Identity = null,
        MenuCleanupResult? Cleanup = null, long StopTimestamp = 0, string? Detail = null,
        ShortRampOwnedProcessIdentity? Process = null, string? Nonce = null, ShortRampPurpose? Purpose = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contract = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceReleasePreparation? Release = null);

    // The supplied transport is the only IO boundary. Tests use a journal behind
    // the production execution gate and unique test kernel objects, never HID.
    public static async Task<IShortRampWorker> StartAsync(ProcessStartInfo child, string nonce,
        ShortRampPermit permit, IMenuTransport parentTransport, Action<MenuLogEntry> log, CancellationToken token,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ValidateNonce(nonce);
        ShortRampPurposePolicy.Validate(purpose);
        if (permit.Purpose != purpose) throw new InvalidDataException("Foreign guardian purpose.");
        if (permit.Nonce != nonce || permit.Identity != parentTransport.Identity)
            throw new InvalidDataException("Guardian session identity does not match its execution permit.");
        using var readyLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
        readyLimit.CancelAfter(ReadyTimeoutMs);
        child.UseShellExecute = false;
        child.CreateNoWindow = true;
        var pipe = new NamedPipeServerStream(PipeName(nonce), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            process = Process.Start(child) ?? throw new IOException("Independent short guardian could not start.");
            _ = process.Handle; // Retain the spawned process identity before any acknowledgment.
            await pipe.WaitForConnectionAsync(readyLimit.Token);
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var boot = await ReadAsync(reader, readyLimit.Token, purpose, nonce);
            var exact = new ShortRampOwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, nonce);
            if (boot.Kind != "boot" || boot.Process != exact || boot.Nonce != nonce || process.HasExited)
                throw new InvalidDataException("Guardian PID/start-time/nonce handshake failed before native setup.");
            permit.RegisterGuardian(exact.Pid, exact.StartUtcTicks);
            await WriteAsync(writer, new("start", Process: exact, Nonce: nonce), readyLimit.Token, purpose, nonce);
            var ready = await ReadAsync(reader, readyLimit.Token, purpose, nonce);
            if (ready.Kind != "ready" || ready.Identity != parentTransport.Identity || process.HasExited)
                throw new MenuPreconditionException("Independent short guardian readiness was not confirmed. " + ready.Detail);
            return new Worker(process, pipe, reader, writer, permit, parentTransport, log);
        }
        catch
        {
            permit.Revoke();
            await pipe.DisposeAsync(); // EOF also makes a connected child stop independently.
            if (process is not null)
            {
                try
                {
                    using var reap = new CancellationTokenSource(ShortRampOwnedWriterProcess.TerminateAndReapMs);
                    await Task.Run(() => { if (!process.HasExited) process.Kill(); }).WaitAsync(reap.Token);
                    await process.WaitForExitAsync(reap.Token);
                }
                catch (Exception) { SafeLog(log, new(0, "guardian-start-failed", "Spawned guardian exit is unconfirmed.")); }
                process.Dispose();
            }
            throw;
        }
    }

    public static async Task<int> RunAsync(string nonce, int parentPid, ShortRampPermit permit,
        Func<IMenuTransport> transportFactory, Action<MenuLogEntry> log, Action<Process>? requestWriterTermination = null,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ValidateNonce(nonce);
        if (permit.Purpose != purpose || !Enum.IsDefined(purpose) || parentPid <= 0 || parentPid == Environment.ProcessId || permit.Nonce != nonce) return 2;
        // External callbacks can block indefinitely. Buffer until all hardware
        // recovery attempts have finished, preserving original monotonic times.
        var outputLog = log;
        var bufferedLog = new List<MenuLogEntry>();
        log = entry => bufferedLog.Add(entry with { QpcTimestamp = entry.QpcTimestamp == 0 ? Stopwatch.GetTimestamp() : entry.QpcTimestamp });
        var clock = new MenuSystemClock();
        using var lifetime = new CancellationTokenSource(permit.IsPersistent ? Timeout.Infinite : purpose == ShortRampPurpose.ContextualClosedHold ? 120000 : 60000);
        using var parentLost = new CancellationTokenSource();
        using var absoluteStop = new CancellationTokenSource();
        using var released = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, parentLost.Token, absoluteStop.Token, released.Token);
        var releaseObserver = permit.ObserveReferenceReleaseAsync(released, lifetime.Token);
        using var pipe = new NamedPipeClientStream(".", PipeName(nonce), PipeDirection.InOut, PipeOptions.Asynchronous);
        Process? parent = null;
        IMenuTransport? transport = null;
        var prepared = false;
        var pulseArmed = false;
        var noCleanupRequired = false;
        MenuCleanupResult? cleanup = null;
        var failed = false;
        Task? deadlineObserver = null;
        ShortRampOwnedWriterProcess.OwnedWriterHandle? ownedWriter = null;
        try
        {
            parent = Process.GetProcessById(parentPid);
            _ = parent.Handle;
            if (permit.IsReference) permit.ValidateReferenceParent(parent);
            _ = ObserveParentExitAsync(parent, parentLost, lifetime.Token);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                connect.CancelAfter(ReadyTimeoutMs);
                await pipe.ConnectAsync(connect.Token);
            }
            using var self = Process.GetCurrentProcess();
            var startUtcTicks = self.StartTime.ToUniversalTime().Ticks;
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var exact = new ShortRampOwnedProcessIdentity(Environment.ProcessId, startUtcTicks, nonce);
            await WriteAsync(writer, new("boot", Process: exact, Nonce: nonce), stop.Token, purpose, nonce);
            var start = await ReadAsync(reader, stop.Token, purpose, nonce);
            if (start.Kind != "start" || start.Process != exact || start.Nonce != nonce || permit.GuardianIdentity != exact)
                throw new InvalidDataException("Guardian launch was not authenticated before native setup.");
            if (permit.OwnedWriter is not null) ownedWriter = ShortRampOwnedWriterProcess.OwnedWriterHandle.Retain(permit);
            transport = transportFactory(); // Reservation must be owned before ready.
            if (transport.Identity != permit.Identity) throw new InvalidDataException("Guardian transport identity does not match its permit.");
            permit.ConfirmGuardianReady();
            if (permit.IsClosedHold)
                deadlineObserver = RevokeAtClosedHoldDeadlineAsync(permit, absoluteStop, lifetime.Token, ownedWriter);
            await WriteAsync(writer, new("ready", Identity: transport.Identity), stop.Token, purpose, nonce);
            while (true)
            {
                var message = await ReadAsync(reader, stop.Token, purpose, nonce);
                if (permit.IsPersistent && message.Kind == "health")
                {
                    stop.Token.ThrowIfCancellationRequested();
                    await WriteAsync(writer, new("healthy"), stop.Token, purpose, nonce);
                }
                else if (message.Kind == "prepare" && !prepared && !pulseArmed)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    prepared = true;
                    await WriteAsync(writer, new("prepared"), stop.Token, purpose, nonce);
                }
                else if (message.Kind == (purpose == ShortRampPurpose.NoReductionControl ? "control-clock" : "pulse") && prepared && !pulseArmed)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var armed = permit.Arm(Environment.ProcessId, startUtcTicks);
                    pulseArmed = true;
                    // Install the absolute observer before acknowledging arm. No
                    // message or heartbeat changes the original QPC deadline.
                    if (!permit.IsClosedHold)
                        deadlineObserver = RevokeAtAbsoluteDeadlineAsync(permit, armed.StopTimestamp, absoluteStop, lifetime.Token);
                    SafeLog(log, new(clock.Milliseconds, purpose == ShortRampPurpose.NoReductionControl ? "guardian-control-clock-armed" : "guardian-pulse-armed", armed.StopTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    await WriteAsync(writer, new(purpose == ShortRampPurpose.NoReductionControl ? "control-clock-armed" : "pulse-armed", StopTimestamp: armed.StopTimestamp), stop.Token, purpose, nonce);
                }
                else if (message.Kind == "release-prepare" && prepared && pulseArmed && message.Release is not null)
                {
                    permit.AcknowledgeReferenceRelease(message.Release, writer: false);
                    await WriteAsync(writer, new("release-ready", Process: exact, Release: message.Release), stop.Token, purpose, nonce);
                }
                else if (message.Kind == "finish")
                {
                    permit.Revoke();
                    if (prepared)
                    {
                        cleanup = await CleanupIndependentAsync(permit, transport, clock, log, ownedWriter, requestWriterTermination);
                        permit.RecordGuardianCleanup(cleanup);
                        prepared = false;
                    }
                    else
                    {
                        noCleanupRequired = true;
                        cleanup = new(false, "Guardian stopped before preparation; no cleanup commands were required.");
                    }
                    await WriteAsync(writer, new("finished", Cleanup: cleanup), CancellationToken.None, purpose, nonce);
                    break;
                }
                else throw new InvalidDataException("Unsupported short guardian lifecycle message.");
            }
        }
        catch (Exception ex)
        {
            permit.Revoke(); // Irreversible event is set before any cleanup admission.
            SafeLog(log, new(clock.Milliseconds, "guardian-stop", ex.GetType().Name));
            if (permit.ReferenceReleased) { noCleanupRequired = true; }
            else if (prepared && transport is not null)
            {
                cleanup = await CleanupIndependentAsync(permit, transport, clock, log, ownedWriter, requestWriterTermination);
                permit.RecordGuardianCleanup(cleanup);
                prepared = false;
            }
            else failed = true;
            if (pipe.IsConnected)
            {
                try
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    await WriteAsync(writer, new("stopped", Cleanup: cleanup,
                        Detail: ex.GetType().Name + ": " + ex.Message.Split(['\r', '\n'])[0][..Math.Min(ex.Message.Split(['\r', '\n'])[0].Length, 512)]), CancellationToken.None, purpose, nonce);
                }
                catch (Exception) { }
            }
        }
        finally
        {
            permit.Revoke();
            lifetime.Cancel();
            await releaseObserver;
            if (deadlineObserver is not null)
                try { await deadlineObserver; } catch (OperationCanceledException) { }
            parent?.Dispose();
            ownedWriter?.Dispose();
            if (transport is not null)
                try { await transport.DisposeAsync(); }
                catch (Exception ex) { failed = true; SafeLog(log, new(clock.Milliseconds, "guardian-dispose-failed", ex.GetType().Name)); }
            if (!failed) permit.ConfirmReferenceFinalized(writer: false);
            foreach (var entry in bufferedLog) SafeLog(outputLog, entry);
        }
        if (permit.ReferenceReleased) return failed ? 1 : 0;
        return !failed && (!permit.IsClosedHold || cleanup?.WorkerFailed != true) &&
            (cleanup?.CommandsSubmitted == true || noCleanupRequired) ? 0 : 1;
    }

    private static async Task RevokeAtAbsoluteDeadlineAsync(ShortRampPermit permit, long stopTimestamp,
        CancellationTokenSource expired, CancellationToken token)
    {
        while (true)
        {
            var remaining = stopTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) break;
            var wait = Math.Max(1, (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
            await Task.Delay(wait, token);
        }
        permit.Revoke();
        expired.Cancel();
    }

    private static async Task RevokeAtClosedHoldDeadlineAsync(ShortRampPermit permit,
        CancellationTokenSource expired, CancellationToken token, ShortRampOwnedWriterProcess.OwnedWriterHandle? writer)
    {
        // Independent of the pipe, logger, PCM and parent timers. The only
        // extension is the one native-success publication in the shared permit.
        // Missing/partial/late native transition retains the ramp boundary.
        while (true)
        {
            if (permit.ReferenceReleased) return;
            var state = permit.State;
            var deadline = state == ShortRampPermitState.Unarmed ? permit.OuterRecoveryTimestamp : permit.IndependentStopTimestamp;
            if (state is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup || Stopwatch.GetTimestamp() >= deadline ||
                permit.IsPersistent && (permit.PersistentHealthExpired || writer?.HasExited == true)) break;
            await Task.Delay(permit.IsPersistent && permit.PersistentQuietActive ? 25 : 1, token);
        }
        permit.Revoke();
        expired.Cancel();
    }

    private static async Task<MenuCleanupResult> CleanupIndependentAsync(ShortRampPermit permit, IMenuTransport transport,
        IMenuClock clock, Action<MenuLogEntry> log,
        ShortRampOwnedWriterProcess.OwnedWriterHandle? ownedWriter = null, Action<Process>? requestWriterTermination = null)
    {
        var pending = new List<MenuLogEntry>();
        try
        {
            return await CleanupCoreAsync(permit, transport, clock,
                entry => pending.Add(entry with { QpcTimestamp = Stopwatch.GetTimestamp() }), ownedWriter, requestWriterTermination);
        }
        finally { foreach (var entry in pending) SafeLog(log, entry); }
    }
    internal static async Task<MenuCleanupResult> CleanupCoreAsync(ShortRampPermit permit, IMenuTransport transport,
        IMenuClock clock, Action<MenuLogEntry> log,
        ShortRampOwnedWriterProcess.OwnedWriterHandle? ownedWriter = null, Action<Process>? requestWriterTermination = null,
        IShortRampQpcClock? nativeClock = null)
    {
        permit.ThrowIfReferenceReleased();
        permit.Revoke();
        permit.ThrowIfReferenceReleased();
        if (ownedWriter is not null && !await ownedWriter.PrepareCleanupAsync(log, requestWriterTermination))
            return new(false, "Owned writer termination/reap is unconfirmed; no cleanup IO admitted.", WorkerFailed: true);
        if (permit.OwnedWriter is not null && !permit.OwnedWriterExitConfirmed && !permit.CooperativeWriterRetired && !permit.WriterTookCleanup)
            return new(false, "Original writer exit is unconfirmed; no cleanup IO admitted.", WorkerFailed: true);
        permit.BeginCleanup();
        nativeClock ??= new ShortRampSystemQpcClock();
        using var limit = new CancellationTokenSource(permit.UsesContextualRecovery ? permit.RemainingOuterMs : 4000);
        try
        {
            // An abandoned native transaction mutex does not prove that the
            // original180ms settling interval elapsed after parent death.
            SafeLog(log, new(clock.Milliseconds, "guardian-takeover-gap-start", "180ms after possible orphaned write"));
            await WaitForOrphanNativeQuietAsync(permit.Purpose, clock, nativeClock);
            if (permit.UsesContextualRecovery)
                return await ContextualRecovery.RunAsync(permit, transport, clock, nativeClock, e => SafeLog(log, e), limit.Token);
            var outerExpired = permit.IsClosedHold && nativeClock.Timestamp > permit.OuterDeadlineTimestamp;
            SafeLog(log, new(clock.Milliseconds, "guardian-takeover-gap-end", "180ms"));
            var result = await ShortRampSession.CleanupAsync(transport, clock, e => SafeLog(log, e), limit.Token,
                prioritizeAutomatic: permit.OwnedWriter is not null,
                strictFailures: permit.IsClosedHold);
            if (permit.IsClosedHold && (outerExpired || nativeClock.Timestamp > permit.OuterDeadlineTimestamp))
                result = result with { Detail = result.Detail + " Original guardian-ready22s cleanup cap exceeded.", WorkerFailed = true };
            return result;
        }
        catch (Exception ex) { return new(false, "Bounded guardian cleanup unconfirmed: " + ex.GetType().Name,
            WorkerFailed: permit.IsClosedHold); }
    }

    public static Task WaitForOrphanNativeQuietAsync(ShortRampPurpose purpose, IMenuClock clock, IShortRampQpcClock nativeClock)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (purpose is not (ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold or ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet))
            return clock.DelayAsync(180, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        if (nativeClock.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(nativeClock));
        return WaitMeasuredAsync().WaitAsync(TimeSpan.FromSeconds(1));

        async Task WaitMeasuredAsync()
        {
            var quiet = new ShortRampNativeQuietHorizon(nativeClock, ShortRampExecutionGate.NativeQuietGapMs,
                "Orphan quiet clock did not advance; settling is unconfirmed.");
            while (!quiet.Elapsed)
            {
                var remainingMs = quiet.RemainingMilliseconds;
                try { await clock.DelayAsync(remainingMs, CancellationToken.None); }
                catch (Exception ex) { quiet.RecordWaitFailure(ex); }
                quiet.ObserveProgress(nativeClock.Timestamp);
            }
            quiet.ThrowWaitFailure();
        }
    }

    private static async Task ObserveParentExitAsync(Process parent, CancellationTokenSource lost, CancellationToken token)
    {
        try { await parent.WaitForExitAsync(token); lost.Cancel(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { try { lost.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private sealed class Worker : IShortRampWorker
    {
        private readonly Process process;
        private readonly NamedPipeServerStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly ShortRampPermit permit;
        private readonly IMenuTransport inner;
        private readonly Action<MenuLogEntry> log;
        private readonly SemaphoreSlim _rpc = new(1, 1);
        private volatile bool _prepared;
        private bool _pulseArmed;
        private volatile bool _finished;
        private bool _disposed;
        private MenuCleanupResult? _result;
        private IMenuTransport? _guard;
        private readonly Task<MenuCleanupResult> _exactExitRecovery;
        private readonly Task _persistentGuardianWatch;
        private Task<MenuCleanupResult>? _finishTask;
        private readonly object _finishLock = new();
        internal Worker(Process process, NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer,
            ShortRampPermit permit, IMenuTransport inner, Action<MenuLogEntry> log)
        {
            this.process = process; this.pipe = pipe; this.reader = reader; this.writer = writer;
            this.permit = permit; this.inner = inner; this.log = log;
            // This task never depends on a PCM callback, parent lifecycle read,
            // or terminal report. It retains the exact spawned process handle.
            _exactExitRecovery = Task.Run(ObserveExactExitAsync);
            _persistentGuardianWatch = permit.IsPersistent ? Task.Run(ObservePersistentGuardianAsync) : Task.CompletedTask;
        }
        private async Task ObservePersistentGuardianAsync()
        {
            try
            {
                while (!process.HasExited)
                {
                    if (permit.ReferenceReleased) return;
                    // Once stop has frozen its epoch, normal health RPCs end.
                    // Actual native route progress must continue independently
                    // of the lifecycle pipe or a blocked external observer.
                    if (permit.PersistentStopQpc > 0 && Stopwatch.GetTimestamp() - permit.PersistentRecoveryProgressQpc >= Stopwatch.Frequency * 2)
                    {
                        await TerminateAndReapAsync();
                        return; // Retained exact-exit observer owns W fallback.
                    }
                    await Task.Delay(25);
                }
            }
            catch (Exception) { permit.Revoke(); }
        }
        public bool ReferenceReleased => permit.ReferenceReleased;
        public ShortRampPurpose Purpose => permit.Purpose;
        public Task<ReferenceActivationReceipt> ActivateReferenceAsync(CancellationToken token) => inner is ShortRampOwnedWriterProcess owned &&
            permit.IsReference && _prepared && _pulseArmed && !_finished && !process.HasExited
            ? owned.ActivateReferenceAsync(token) : throw new InvalidOperationException("Authenticated reference worker is unavailable.");
        public int? NativeMode2Count => permit.NativeMode2Count;
        public int? NativeMode0Count => permit.NativeMode0Count;
        public int? NativeNormalCloseCount => permit.NativeNormalCloseCount;
        public ShortRampNativePhaseEvidence? NativePhaseEvidence => permit.NativePhaseEvidence;
        public ShortRampAutomaticProgress? AutomaticProgress => permit.AutomaticProgress;
        public ShortRampContextualProgress? ContextualProgress => permit.ContextualProgress;
        public ContextualRecoveryEvidence? ContextualEvidence => permit.ContextualEvidence;
        public int RecoveryWaitMilliseconds => permit.UsesContextualRecovery ? permit.IsPersistent && permit.PersistentStopQpc == 0 ? ShortRampPermit.PersistentRecoveryMs : permit.RemainingOuterMs : FinishTimeoutMs;
        public ShortRampObservedPulseBoundary? PulseBoundaryObservation => permit.PulseBoundaryObservation;
        public IMenuTransport Transport => _guard ??= new Guard(inner, () => _prepared && !_finished && !process.HasExited,
            () => !_finished && !process.HasExited, permit.Purpose);
        public async Task ArmAsync(CancellationToken token)
        {
            if (_prepared || _finished || process.HasExited) throw new InvalidOperationException("Guardian cannot prepare this session.");
            var response = await RequestAsync(new("prepare"), token);
            if (response.Kind != "prepared") throw new IOException("Guardian preparation was not acknowledged.");
            _prepared = true;
        }
        public async Task ArmPulseAsync(CancellationToken token)
        {
            if (!_prepared || _pulseArmed || _finished || process.HasExited) throw new InvalidOperationException("Guardian cannot arm this pulse.");
            var response = await RequestAsync(new(permit.Purpose == ShortRampPurpose.NoReductionControl ? "control-clock" : "pulse"), token);
            if (response.Kind != (permit.Purpose == ShortRampPurpose.NoReductionControl ? "control-clock-armed" : "pulse-armed") || response.StopTimestamp <= Stopwatch.GetTimestamp() ||
                response.StopTimestamp != permit.Snapshot.StopTimestamp)
                throw new IOException("Independent absolute pulse deadline was not acknowledged.");
            _pulseArmed = true;
        }
        public void Revoke() => permit.Revoke();
        public async Task CheckHealthAsync(CancellationToken token)
        {
            if (permit.ReferenceReleased) return;
            if (!permit.IsPersistent || _finished || process.HasExited || permit.State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
                throw new IOException("Independent controller has stopped.");
            try
            {
                var response = await RequestAsync(new("health"), token, 250);
                if (response.Kind != "healthy")
                    throw new IOException("Independent guardian health was not acknowledged.");
            }
            catch
            {
                var alreadyStopped = permit.State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup;
                permit.Revoke();
                // A stalled guardian cannot retain an unbounded manual state.
                // Only the retained spawned handle may be terminated; its exact
                // exit observer owns the already prepared writer fallback.
                if (!alreadyStopped && !permit.ReferenceReleased)
                {
                    await TerminateAndReapAsync();
                }
                throw;
            }
            if (inner is not ShortRampOwnedWriterProcess owned) throw new IOException("Prepared writer is missing.");
            // W loss leaves healthy G responsible for cleanup. Killing G here
            // would turn that single W fault into a compound failure.
            await owned.CheckHealthAsync(token);
            permit.RenewPersistentHealth();
        }
        private Task<ReferenceReleaseResult>? _releaseTask;
        public Task<ReferenceReleaseResult> ReleaseReferenceAsync(ReferenceActivationReceipt receipt, CancellationToken token)
        {
            lock (_finishLock) return _releaseTask ??= ReleaseCoreAsync(receipt, token);
        }
        private async Task<ReferenceReleaseResult> ReleaseCoreAsync(ReferenceActivationReceipt receipt, CancellationToken token)
        {
            if (!_prepared || !_pulseArmed || _finished || inner is not ShortRampOwnedWriterProcess owned)
                throw new InvalidOperationException("Exact prepared owners are required for release.");
            using var preparationLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparationLimit.CancelAfter(1200); // Inside the existing two-second health lease.
            try
            {
                var preparation = permit.PrepareReferenceRelease(receipt);
                await owned.PrepareReleaseAsync(preparation, preparationLimit.Token);
                var ready = await RequestAsync(new("release-prepare", Release: preparation), preparationLimit.Token, 500);
                if (ready.Kind != "release-ready" || ready.Release != preparation || ready.Process != permit.GuardianIdentity ||
                    process.HasExited || !owned.ExactProcessAlive)
                    throw new IOException("Both exact live release acknowledgments are required.");
                permit.CommitReferenceRelease(preparation, preparationLimit.Token);
            }
            catch { permit.Revoke(); throw; }
            _finished = true;
            return await FinalizeReleaseAsync();
        }
        private async Task<ReferenceReleaseResult> FinalizeReleaseAsync()
        {
            if (!permit.ReferenceReleased) throw new InvalidOperationException("Release was not committed.");
            var owner = permit.OwnedWriter!; var guardian = permit.GuardianIdentity!;
            try
            {
                MenuCleanupResult result;
                try { result = await _exactExitRecovery.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    await pipe.DisposeAsync();
                    await TerminateAndReapAsync();
                    result = await _exactExitRecovery.WaitAsync(TimeSpan.FromSeconds(3));
                    return new(true, false, owner, guardian, "Released; cooperative owner finalization timed out. Exact resources reaped.");
                }
                return new(true, !result.WorkerFailed && permit.OwnedWriterExitConfirmed &&
                    permit.ReferenceWriterFinalized && permit.ReferenceGuardianFinalized,
                    owner, guardian, result.WorkerFailed ? "Released; exact successful owner finalization is unconfirmed." : "Applied and released; both exact children exited.");
            }
            catch (Exception ex) { return new(true, false, owner, guardian, "Released; finalization unconfirmed: " + ex.GetType().Name); }
        }
        public Task<MenuCleanupResult> FinishAsync(CancellationToken token)
        {
            lock (_finishLock) return _finishTask ??= FinishCoreAsync();
        }
        private async Task<MenuCleanupResult> FinishCoreAsync()
        {
            permit.ThrowIfReferenceReleased();
            _finished = true;
            Revoke(); // Never wait for RPC before making queued mode2 impossible.
            using var finish = new CancellationTokenSource(RecoveryWaitMilliseconds);
            try
            {
                if (!process.HasExited)
                    _ = await RequestAsync(new("finish"), finish.Token, RecoveryWaitMilliseconds);
            }
            catch (Exception) { } // Exact-exit observer owns shared result/recovery.
            try { return _result = await _exactExitRecovery.WaitAsync(finish.Token); }
            catch (OperationCanceledException)
            {
                // Output/disposal can hang after recovery. Reap only this exact
                // spawned guardian, then let its already prepared peer finish.
                await pipe.DisposeAsync();
                try
                {
                    await TerminateAndReapAsync();
                    return _result = await _exactExitRecovery.WaitAsync(TimeSpan.FromMilliseconds(permit.UsesContextualRecovery ? permit.RemainingOuterMs : 4000));
                }
                catch (Exception ex) { return _result = new(false, "Exact guardian finalization unconfirmed: " + ex.GetType().Name, WorkerFailed: true); }
            }
        }
        private async Task TerminateAndReapAsync()
        {
            using var reap = new CancellationTokenSource(ShortRampOwnedWriterProcess.TerminateAndReapMs);
            await Task.Run(() => { if (!process.HasExited) process.Kill(); }).WaitAsync(reap.Token);
            await process.WaitForExitAsync(reap.Token);
        }
        private async Task<MenuCleanupResult> ObserveExactExitAsync()
        {
            await process.WaitForExitAsync();
            permit.Revoke();
            if (permit.ReferenceReleased)
            {
                await pipe.DisposeAsync();
                var failedRelease = process.ExitCode != 0 || !permit.ReferenceGuardianFinalized;
                if (inner is ShortRampOwnedWriterProcess releasedWriter)
                {
                    try { await releasedWriter.DisposeAsync(); }
                    catch (Exception) { failedRelease = true; }
                }
                return new(false, "Intentional reference release; no automatic restoration requested.",
                    WorkerFailed: failedRelease || !permit.ReferenceWriterFinalized);
            }
            // Cancel and observe the serialized pipe operation; no terminal
            // read is allowed to sit ahead of independent prepared recovery.
            await pipe.DisposeAsync();
            var shared = permit.GuardianCleanup;
            var result = shared ?? new(false, "Exact guardian exited without a complete shared cleanup report.", WorkerFailed: true);
            var failed = process.ExitCode != 0 || shared is null;
            if (_prepared && !result.CommandsSubmitted)
            {
                try
                {
                    if (inner is not IMenuCleanupTakeover takeover) throw new IOException("Parent transport cannot reserve cleanup takeover.");
                    var reserveMs = permit.OwnedWriter is null ? 150 : 450;
                    using var reserve = new CancellationTokenSource(reserveMs);
                    await takeover.ReserveAfterWorkerExitAsync(reserve.Token).WaitAsync(TimeSpan.FromMilliseconds(reserveMs), reserve.Token);
                    // No synchronous external observer participates in recovery.
                    var fallback = await CleanupIndependentAsync(permit, inner, new MenuSystemClock(), _ => { });
                    result = new(fallback.CommandsSubmitted, result.Detail + " Prepared fallback: " + fallback.Detail, WorkerFailed: true);
                }
                catch (Exception ex) { result = new(false, result.Detail + " Prepared takeover refused: " + ex.GetType().Name, WorkerFailed: true); }
            }
            // A blocked parent observer must not leave the retired/takeover W
            // idle for its60s receive timer after hardware recovery completed.
            if (inner is ShortRampOwnedWriterProcess owned)
                try { await owned.DisposeAsync(); }
                catch (Exception ex) { result = result with { Detail = result.Detail + " Exact writer finalization unconfirmed: " + ex.GetType().Name, WorkerFailed = true }; }
            if (permit.IsClosedHold && Stopwatch.GetTimestamp() > permit.OuterDeadlineTimestamp)
                result = result with { Detail = result.Detail + " Original guardian-ready finalization cap exceeded.", WorkerFailed = true };
            return result with { WorkerFailed = failed || result.WorkerFailed };
        }
        private async Task<Message> RequestAsync(Message message, CancellationToken token, int timeoutMs = 1000)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeoutMs);
            await _rpc.WaitAsync(limit.Token);
            try { await WriteAsync(writer, message, limit.Token, permit.Purpose, permit.Nonce); return await ReadAsync(reader, limit.Token, permit.Purpose, permit.Nonce); }
            finally { _rpc.Release(); }
        }
        private Task? _disposeTask;
        public ValueTask DisposeAsync()
        {
            lock (_finishLock) return new(_disposeTask ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (permit.ReferenceReleased) await (_releaseTask ?? FinalizeReleaseAsync());
                else await FinishAsync(CancellationToken.None);
            }
            finally
            {
                try { await inner.DisposeAsync(); }
                finally
                {
                    reader.Dispose();
                    try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                    try { await pipe.DisposeAsync(); }
                    finally { await _persistentGuardianWatch; process.Dispose(); _rpc.Dispose(); }
                }
            }
        }
    }
    private sealed class Guard(IMenuTransport inner, Func<bool> canOpen, Func<bool> workerAlive, ShortRampPurpose purpose) : IMenuTransport
    {
        public MenuDeviceIdentity Identity => inner.Identity;
        public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token)
        {
            ShortRampExecutionGate.ValidateForQueue(report, reply, purpose);
            var kind = ShortRampExecutionGate.Classify(report, reply);
            if (kind == ShortRampFrame.Open && !canOpen()) throw new InvalidOperationException("Independent guardian is not prepared.");
            if (kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close) && !workerAlive())
                throw new InvalidOperationException("Independent guardian is unavailable.");
            return inner.TransferAsync(report, reply, token);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task<Message> ReadAsync(StreamReader reader, CancellationToken token, ShortRampPurpose purpose, string nonce)
    {
        var line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException("Short guardian lifecycle pipe closed.");
        if (line.Length > 4096) throw new InvalidDataException("Oversized guardian lifecycle message.");
        var message = JsonSerializer.Deserialize<Message>(line) ?? throw new InvalidDataException("Empty guardian lifecycle message.");
        if (message.Purpose != purpose || message.Nonce != nonce) throw new InvalidDataException("Guardian lifecycle purpose or nonce was not authenticated.");
        if (purpose == ShortRampPurpose.ReferencePersistentQuiet && message.Contract != ShortRampPermit.ReferenceContract)
            throw new InvalidDataException("Guardian reference contract/profile ABI was not authenticated.");
        return message;
    }
    private static async Task WriteAsync(StreamWriter writer, Message message, CancellationToken token, ShortRampPurpose purpose, string nonce)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(1000);
        await writer.WriteLineAsync(JsonSerializer.Serialize(message with { Purpose = purpose, Nonce = nonce,
            Contract = purpose == ShortRampPurpose.ReferencePersistentQuiet ? ShortRampPermit.ReferenceContract : null }).AsMemory(), limit.Token);
        await writer.FlushAsync(limit.Token);
    }
    private static void SafeLog(Action<MenuLogEntry> log, MenuLogEntry entry) { try { log(entry); } catch (Exception) { } }
    private static string PipeName(string nonce) => "MonitorFanControl.ShortRamp." + nonce;
    private static void ValidateNonce(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid short guardian nonce.");
    }
}
