using System.IO;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

public interface IShortRampWorker : IMenuWorker
{
    ShortRampPurpose Purpose => ShortRampPurpose.Mode2;
    bool ReferenceReleased => false;
    Task<ReferenceReleaseResult> ReleaseReferenceAsync(ReferenceActivationReceipt receipt, CancellationToken token) =>
        throw new InvalidOperationException("Worker has no authenticated release boundary.");
    string SoftwareProfileId => ShortRampSoftwareProfile.ForPurpose(Purpose).Id;
    Task<ReferenceActivationReceipt> ActivateReferenceAsync(CancellationToken token) =>
        throw new InvalidOperationException("Worker does not expose authenticated reference activation.");
    int? NativeMode2Count => null;
    int? NativeMode0Count => null;
    int? NativeNormalCloseCount => null;
    ShortRampNativePhaseEvidence? NativePhaseEvidence => null;
    Task ArmPulseAsync(CancellationToken token);
    void Revoke();
    // Optional independent lifecycle evidence in the capture's shared QPC domain.
    // A terminal state seen after a blocked callback is not its earliest timestamp.
    ShortRampObservedPulseBoundary? PulseBoundaryObservation => null;
    ShortRampAutomaticProgress? AutomaticProgress => null;
    ShortRampContextualProgress? ContextualProgress => null;
    ContextualRecoveryEvidence? ContextualEvidence => null;
    int RecoveryWaitMilliseconds => 6000;
    Task CheckHealthAsync(CancellationToken token) => Task.CompletedTask;
}

public sealed record ShortRampObservedPulseBoundary(long? RevokedQpc, long? CleanupStartedQpc,
    long QpcFrequency, bool EarliestCleanupBoundaryEstablished);
public sealed record ShortRampPulseTiming(long ArmBeganMilliseconds, long RequestedDeadlineMilliseconds,
    long? ParentRevokeRequestedMilliseconds, long? ParentCleanupRequestedMilliseconds,
    ShortRampObservedPulseBoundary? WorkerObservation);
public sealed record ShortRampClosedHoldObservation(long? RevokeRequestedQpc, long? CleanupRequestedQpc,
    long QpcFrequency, ShortRampObservedPulseBoundary? WorkerObservation);

// Only a real user's explicit observation may populate this input. A microphone,
// submitted close command, or fabricated/default metadata is not such evidence.
public sealed record HumanOsdClosureObservation(DateTimeOffset ObservedAtUtc, string Evidence)
{
    internal bool IsCurrent => !string.IsNullOrWhiteSpace(Evidence) &&
        ObservedAtUtc <= DateTimeOffset.UtcNow && ObservedAtUtc >= DateTimeOffset.UtcNow.AddMinutes(-30);
}

public sealed record ShortRampSessionResult(MenuRunState State, string Detail, int? CachedRpm,
    MenuCleanupResult? Cleanup, bool PulseRequested)
{
    public ShortRampOsdAdmissionMetadata? OsdAdmission { get; init; }
    // Software profile qualification and the explicit OSD admission basis live in
    // ShortRampQualifiedResult; this result cannot establish a physical state.
    public bool LiveReady => false;
    public bool PhysicalClosureConfirmed => false;
    [JsonConverter(typeof(JsonStringEnumConverter<ShortRampPurpose>))]
    public ShortRampPurpose Purpose { get; init; } = ShortRampPurpose.Mode2;
    public string CommandPurpose => Purpose.ToString();
    public bool ControlClockArmed { get; init; }
    public bool Mode2Requested => PulseRequested;
    public ShortRampPulseTiming? PulseTiming { get; init; }
    public ShortRampPulseTiming? ControlTiming { get; init; }
    public int? NativeMode2Count { get; init; }
    public int? NativeMode0Count { get; init; }
    public int? NativeNormalCloseCount { get; init; }
    public ShortRampNativePhaseEvidence? NativePhaseEvidence { get; init; }
    public ShortRampClosedHoldObservation? ClosedHoldObservation { get; init; }
    public ShortRampAutomaticProgress? AutomaticProgress { get; init; }
    public ShortRampContextualProgress? ContextualProgress { get; init; }
    public ContextualRecoveryEvidence? ContextualEvidence { get; init; }
}

public static class ShortRampSession
{
    public const int RequestedPulseMs = 1200;
    public const int GuardianStopMs = 1500;
    public const int MinimumBaselineRpm = 1000;
    public const int MaximumBaselineRpm = 1200;
    public const int BoundedFloorRpm = 950;
    // Parent active transfers have zero transaction wait. From arm, execution
    // admission <=100 + pulse write100/nativegap180/recordedgap180 + GET
    // write100/nativegap180/recordedgap180/reply100 =1120ms.
    public const int PulseAndFloorBudgetMs = 1120;
    public const int ControlPassiveOffsetMs = 384;
    public const int ControlSlotAndFloorBudgetMs = 944;
    private const int FloorGetBudgetMs = 560;
    private const int TransferTimeoutMs = 450;
    public const int ClosedHoldRampMs = 2400;
    public const int ClosedHoldMaximumMs = 10000;
    public const int ClosedHoldFinalGetOffsetMs = 1200;
    public const int ClosedHoldNormalRecoveryLeadMs = 1000;

    public static Task<ShortRampSessionResult> RunAsync(HumanOsdClosureObservation? observation,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock,
        Func<CancellationToken, Task<bool>> confirmPreparedNoise,
        Action<MenuLogEntry> log, CancellationToken token = default,
        Func<string, CancellationToken, Task>? qualifyPhase = null,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
        => RunWithAdmissionAsync(ShortRampOsdAdmission.FromObservation(observation), startWorker, clock,
            confirmPreparedNoise, log, token, qualifyPhase, purpose);

    public static async Task<ShortRampSessionResult> RunWithAdmissionAsync(ShortRampOsdAdmission? admission,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock,
        Func<CancellationToken, Task<bool>> confirmPreparedNoise,
        Action<MenuLogEntry> log, CancellationToken token = default,
        Func<string, CancellationToken, Task>? qualifyPhase = null,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        if (!Enum.IsDefined(purpose))
            return new(MenuRunState.PreconditionFailed, "Unknown command purpose; no worker or hardware action started.", null, null, false) { Purpose = purpose };
        if (purpose is ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet)
            return new(MenuRunState.PreconditionFailed, "Persistent Quiet requires its intent controller; no diagnostic duration applies.", null, null, false) { Purpose = purpose };
        if (admission is not { IsValid: true })
            return new(MenuRunState.PreconditionFailed,
                "A valid explicit OSD admission is missing; no worker or hardware action started.", null, null, false) { Purpose = purpose };
        if (purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold)
            return await RunClosedHoldAsync(admission, startWorker, clock, confirmPreparedNoise, log, token, qualifyPhase, purpose);
        IShortRampWorker? worker = null;
        MenuCleanupResult? cleanup = null;
        var state = MenuRunState.PreconditionFailed;
        var detail = "Short session preconditions did not complete.";
        var pulseRequested = false;
        var controlClockArmed = false;
        var isControl = purpose == ShortRampPurpose.NoReductionControl;
        var activePhase = isControl ? "control-window" : "pulse";
        var cleanupArmed = false;
        var pulseWindowStarted = false;
        long? pulseArmBegan = null, revokeRequested = null, cleanupRequested = null;
        ShortRampObservedPulseBoundary? workerBoundary = null;
        ShortRampAutomaticProgress? automaticProgress = null;
        int? nativeMode2Count = null;
        int? cached = null;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(45000);
        try
        {
            session.Token.ThrowIfCancellationRequested();
            worker = await startWorker(session.Token).WaitAsync(TimeSpan.FromSeconds(5), session.Token);
            if (worker.Purpose != purpose) throw new InvalidDataException("Worker command purpose differs from the immutable session purpose.");
            var protocol = new MenuProtocol(worker.Transport, clock, log, TransferTimeoutMs);
            await protocol.ValidateAsync(session.Token);
            cached = await ReadRpm(protocol, 860);
            Baseline(cached.Value, isControl);
            await worker.ArmAsync(session.Token).WaitAsync(TimeSpan.FromSeconds(3), session.Token);
            cleanupArmed = true;
            await protocol.SetAsync(MenuCommand.Automatic, session.Token);
            await protocol.SetAsync(MenuCommand.Close, session.Token);
            log(new(clock.Milliseconds, "preparation", "automatic1/close submitted; physical state unconfirmed", "preparation"));
            if (!await confirmPreparedNoise(session.Token).WaitAsync(TimeSpan.FromSeconds(6), session.Token))
                throw new MenuPreconditionException(isControl ? "Prepared target noise is absent or uncertain; control refused." : "Prepared target noise is absent or uncertain; pulse refused.");
            await Phase("closed-before", 5000);
            await protocol.SetAsync(MenuCommand.Open, session.Token);
            await Phase("menu-only", 5000);
            cached = await ReadRpm(protocol, 860);
            Baseline(cached.Value, isControl);
            session.Token.ThrowIfCancellationRequested();
            log(new(clock.Milliseconds, isControl ? "control-precondition" : "pulse-precondition",
                "Final capture/precondition check before one-shot arm", activePhase));
            // Start before the IPC arm round trip, so it can only shorten the
            // parent's pulse. Guardian establishes its own absolute QPC deadline.
            var armBegan = clock.Milliseconds;
            pulseArmBegan = armBegan;
            await worker.ArmPulseAsync(session.Token).WaitAsync(TimeSpan.FromMilliseconds(100), session.Token);
            controlClockArmed = isControl;
            if (isControl)
            {
                if (clock.Milliseconds - armBegan > 100)
                    throw new TimeoutException("Control arm acknowledgment exceeded100ms; immutable clock cannot be renewed.");
                log(new(clock.Milliseconds, "control-clock-armed", "NoReductionControl; Mode2Requested=false", activePhase));
                log(new(clock.Milliseconds, "control-slot-start", "Passive slot; no native command substitutes for mode2", activePhase));
                var passiveRemaining = ControlPassiveOffsetMs - (clock.Milliseconds - armBegan);
                if (passiveRemaining > 0) await clock.DelayAsync((int)passiveRemaining, session.Token);
                RequireTime(armBegan, FloorGetBudgetMs);
                log(new(clock.Milliseconds, "control-slot-end", $"requested arm+384ms; actual arm+{clock.Milliseconds - armBegan}ms; complete-floor reserve560ms; remaining slack{RequestedPulseMs - (clock.Milliseconds - armBegan) - FloorGetBudgetMs}ms", activePhase));
            }
            else
            {
                RequireTime(armBegan, PulseAndFloorBudgetMs);
                pulseRequested = true;
            }
            pulseWindowStarted = true;
            if (!isControl)
            {
                log(new(clock.Milliseconds, "pulse-attempt-start", "pulse", "pulse"));
                await protocol.WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, 2]), session.Token);
            }
            log(new(clock.Milliseconds, "phase-start", activePhase, activePhase));
            RequireTime(armBegan, FloorGetBudgetMs);
            cached = await ReadRpm(protocol, FloorGetBudgetMs);
            if (cached < BoundedFloorRpm)
                throw new InvalidDataException(isControl ? "Cached RPM fell below the stricter bounded floor950; control revoked." : "Cached RPM fell below the stricter bounded floor950; pulse revoked.");
            RequireTime(armBegan, 0);
            var remaining = RequestedPulseMs - (clock.Milliseconds - armBegan);
            if (remaining > 0) await clock.DelayAsync((int)remaining, session.Token);
            revokeRequested = clock.Milliseconds;
            worker.Revoke();
            pulseWindowStarted = false;
            log(new(clock.Milliseconds, "phase-end", activePhase, activePhase));
            cleanupRequested = clock.Milliseconds;
            cleanup = await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));
            if (!cleanup.CommandsSubmitted || cleanup.WorkerFailed)
                throw new IOException("Independent cleanup did not report both submissions and a successful worker exit.");
            await Phase("closed-after", 5000);
            state = MenuRunState.Completed;
            detail = isControl ? "Matched no-reduction software sequence completed; cleanup submissions, conditional timing and physical outcome are recorded separately." :
                "Short software sequence completed; cleanup submissions, conditional timing and physical outcome are recorded separately.";

            async Task Phase(string name, int milliseconds)
            {
                try
                {
                    log(new(clock.Milliseconds, "phase-start", name, name));
                    await clock.DelayAsync(milliseconds, session.Token);
                }
                catch
                {
                    log(new(clock.Milliseconds, "phase-aborted", name, name));
                    throw;
                }
                log(new(clock.Milliseconds, "phase-end", name, name));
                if (qualifyPhase is not null) await qualifyPhase(name, session.Token);
            }
            void RequireTime(long began, int reserve)
            {
                if (clock.Milliseconds - began + reserve > RequestedPulseMs)
                    throw new TimeoutException(isControl ? "Control window/floor timing budget was exhausted; cleanup required." : "Short pulse/floor timing budget was exhausted; cleanup required.");
            }
            async Task<int> ReadRpm(MenuProtocol codec, int budget)
            {
                var began = clock.Milliseconds;
                var rpm = await codec.CachedRpmAsync(session.Token);
                if (clock.Milliseconds - began > budget)
                    throw new TimeoutException("Cached RPM reply exceeded its bounded budget.");
                return rpm;
            }
        }
        catch (Exception ex)
        {
            state = ex is OperationCanceledException && token.IsCancellationRequested ? MenuRunState.Interrupted :
                pulseRequested || controlClockArmed ? MenuRunState.Error : MenuRunState.PreconditionFailed;
            detail = ex is InvalidDataException or MenuPreconditionException ? ex.Message :
                $"Short sequence stopped ({ex.GetType().Name}); cleanup status and physical state are separate.";
        }
        finally
        {
            session.Cancel();
            if (worker is not null)
            {
                // Revoke happens synchronously, before finish can admit cleanup.
                if (pulseRequested || controlClockArmed) revokeRequested ??= clock.Milliseconds;
                worker.Revoke();
                if (pulseWindowStarted)
                {
                    pulseWindowStarted = false;
                    try { log(new(clock.Milliseconds, "phase-aborted", activePhase, activePhase)); }
                    catch { state = MenuRunState.Error; }
                }
                if (pulseRequested || controlClockArmed) cleanupRequested ??= clock.Milliseconds;
                try { cleanup ??= await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)); }
                catch (Exception ex) { cleanup = new(false, "Short cleanup unconfirmed: " + ex.GetType().Name); state = MenuRunState.Error; }
                try { workerBoundary = worker.PulseBoundaryObservation; }
                catch (Exception ex) { detail = "Short active-window boundary observation failed: " + ex.GetType().Name; state = MenuRunState.Error; }
                automaticProgress = worker.AutomaticProgress;
                nativeMode2Count = worker.NativeMode2Count;
                try { await worker.DisposeAsync(); }
                catch (Exception ex) { detail = "Short worker disposal unconfirmed: " + ex.GetType().Name; state = MenuRunState.Error; }
                if (cleanupArmed && cleanup?.CommandsSubmitted != true || cleanup?.WorkerFailed == true) state = MenuRunState.Error;
            }
        }
        return new(state, detail, cached, cleanup, pulseRequested)
        {
            Purpose = purpose, OsdAdmission = admission.Metadata, ControlClockArmed = controlClockArmed, NativeMode2Count = nativeMode2Count,
            PulseTiming = pulseRequested && pulseArmBegan is { } began
                ? new(began, checked(began + RequestedPulseMs), revokeRequested, cleanupRequested, workerBoundary) : null,
            ControlTiming = controlClockArmed && pulseArmBegan is { } controlBegan
                ? new(controlBegan, checked(controlBegan + RequestedPulseMs), revokeRequested, cleanupRequested, workerBoundary) : null,
            AutomaticProgress = automaticProgress
        };
    }

    private static async Task<ShortRampSessionResult> RunClosedHoldAsync(ShortRampOsdAdmission admission,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock,
        Func<CancellationToken, Task<bool>> confirmPreparedNoise, Action<MenuLogEntry> log,
        CancellationToken token, Func<string, CancellationToken, Task>? qualifyPhase, ShortRampPurpose purpose)
    {
        var contextual = purpose == ShortRampPurpose.ContextualClosedHold;
        var recoveryLead = contextual ? ShortRampPermit.ContextualNormalRecoveryLeadMs : ClosedHoldNormalRecoveryLeadMs;
        var qpc = clock as IShortRampQpcClock ?? new ShortRampSystemQpcClock();
        IShortRampWorker? worker = null;
        MenuCleanupResult? cleanup = null;
        var state = MenuRunState.PreconditionFailed;
        var detail = "Bounded closed-hold preparation did not complete.";
        var requested = false;
        var cleanupArmed = false;
        var afterObserved = false;
        int? cached = null, mode2Count = null, mode0Count = null, closeCount = null;
        long? revokeQpc = null, cleanupQpc = null;
        string? activePhase = null;
        ShortRampNativePhaseEvidence? evidence = null;
        ShortRampObservedPulseBoundary? boundary = null;
        ShortRampAutomaticProgress? automatic = null;
        ShortRampContextualProgress? contextualProgress = null;
        ContextualRecoveryEvidence? contextualEvidence = null;
        CancellationTokenRegistration callerCancellation = default;
        long armBeforeQpc = 0, armAfterQpc = 0;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(contextual ? 90000 : 45000);
        try
        {
            session.Token.ThrowIfCancellationRequested();
            worker = await startWorker(session.Token).WaitAsync(TimeSpan.FromSeconds(5), session.Token);
            if (worker.Purpose != purpose)
                throw new InvalidDataException("Worker command purpose differs from the immutable closed-hold purpose.");
            // A caller cancel must reach the independent guardian even when
            // the parent is blocked inside a PCM or logging callback.
            callerCancellation = token.UnsafeRegister(static value => ((IShortRampWorker)value!).Revoke(), worker);
            var protocol = new MenuProtocol(worker.Transport, clock, log, TransferTimeoutMs);
            StartPhase("preparation");
            await protocol.ValidateAsync(session.Token);
            cached = await ReadRpm();
            Baseline(cached.Value);
            await worker.ArmAsync(session.Token).WaitAsync(TimeSpan.FromSeconds(3), session.Token);
            cleanupArmed = true;
            await protocol.SetAsync(MenuCommand.Automatic, session.Token);
            await protocol.SetAsync(MenuCommand.Close, session.Token);
            if (!await confirmPreparedNoise(session.Token).WaitAsync(TimeSpan.FromSeconds(6), session.Token))
                throw new MenuPreconditionException("Prepared PCM is absent or unhealthy; bounded closed-hold refused.");
            EndPhase("preparation");
            // The fixed initial background was acquired before this worker. A
            // second five-second closed-before delay would consume G's original
            // ready+22s cap. This purpose has only the fixed700ms Open dwell.
            await protocol.SetAsync(MenuCommand.Open, session.Token);
            StartPhase("menu-only");
            await clock.DelayAsync(700, session.Token);
            EndPhase("menu-only");
            if (qualifyPhase is not null) await qualifyPhase("menu-only", session.Token);
            cached = await ReadRpm();
            Baseline(cached.Value);
            session.Token.ThrowIfCancellationRequested();
            log(new(clock.Milliseconds, "closed-hold-precondition", "Final capture check before one-shot native ramp arm", "ramp"));
            var armBegan = clock.Milliseconds;
            armBeforeQpc = qpc.Timestamp;
            await worker.ArmPulseAsync(session.Token).WaitAsync(TimeSpan.FromMilliseconds(100), session.Token);
            armAfterQpc = qpc.Timestamp;
            if (clock.Milliseconds - armBegan > 100)
                throw new TimeoutException("Closed-hold arm acknowledgment exceeded100ms; original clock cannot be renewed.");
            evidence = RequireEvidence();
            if (evidence.RampDeadlineQpc - evidence.ArmQpc != qpc.Frequency * ClosedHoldRampMs / 1000)
                throw new InvalidDataException("Native ramp evidence has a foreign fixed deadline.");
            RequireRampTime(1580);
            requested = true;
            StartPhase("ramp");
            await protocol.WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, 2]), session.Token);
            RequireRampTime(FloorGetBudgetMs + 460);
            cached = await ReadRpm();
            HoldEntry(cached.Value);
            // Poll early, then make one final complete getter with its entire
            // reply and mode0/native+recorded quiet reserved inside original2.4s.
            var remainingToFinal = ClosedHoldFinalGetOffsetMs - (clock.Milliseconds - armBegan);
            if (remainingToFinal > 0) await clock.DelayAsync((int)remainingToFinal, session.Token);
            RequireRampTime(FloorGetBudgetMs + 460);
            cached = await ReadRpm();
            HoldEntry(cached.Value);
            RequireRampTime(460);
            EndPhase("ramp");
            StartPhase("mode0");
            await protocol.WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, 0]), session.Token);
            evidence = RequireEvidence();
            if (worker.NativeMode2Count != 1 || worker.NativeMode0Count != 1 ||
                evidence.Mode0AttemptQpc < evidence.ArmQpc || evidence.Mode0SettledQpc - evidence.Mode0AttemptQpc < qpc.Frequency * 180 / 1000 ||
                evidence.Mode0SettledQpc > evidence.RampDeadlineQpc || evidence.HoldDeadlineQpc <= evidence.Mode0SettledQpc ||
                evidence.HoldDeadlineQpc > evidence.Mode0AttemptQpc + qpc.Frequency * ClosedHoldMaximumMs / 1000 ||
                evidence.HoldDeadlineQpc > evidence.ArmQpc + qpc.Frequency * 12400 / 1000 ||
                contextual && evidence.HoldDeadlineQpc > evidence.GuardianReadyQpc + qpc.Frequency * 16000 / 1000 ||
                evidence.HoldDeadlineQpc > evidence.OuterDeadlineQpc - qpc.Frequency * (contextual ? ShortRampPermit.ContextualFullRecoveryReserveMs : ShortRampPermit.ClosedHoldFullRecoveryReserveMs) / 1000)
                throw new InvalidDataException("A verified one-shot native mode0 transition with its original deadline is missing.");
            RequireHoldTime(460);
            EndPhase("mode0");
            StartPhase("close");
            await protocol.SetAsync(MenuCommand.Close, session.Token);
            evidence = RequireEvidence();
            if (worker.NativeNormalCloseCount != 1 || evidence.NormalCloseAttemptQpc < evidence.Mode0SettledQpc ||
                evidence.NormalCloseSettledQpc - evidence.NormalCloseAttemptQpc < qpc.Frequency * 180 / 1000 || evidence.NormalCloseSettledQpc > evidence.HoldDeadlineQpc)
                throw new InvalidDataException("Normal close did not establish its verified native phase; hold aborted.");
            RequireHoldTime(recoveryLead);
            EndPhase("close");
            StartPhase("closed-hold");
            var stopQpc = evidence.HoldDeadlineQpc - qpc.Frequency * recoveryLead / 1000;
            var remainingHoldMs = (stopQpc - qpc.Timestamp) * 1000 / qpc.Frequency;
            if (remainingHoldMs <= 0) throw new TimeoutException("The original native closed-hold observation window is exhausted.");
            await clock.DelayAsync(checked((int)remainingHoldMs), session.Token);
            // A delayed continuation never starts another timer. Independent G
            // continues to observe the same native deadline throughout.
            revokeQpc = qpc.Timestamp;
            worker.Revoke();
            EndPhase("closed-hold");
            StartPhase("recovery");
            cleanupQpc = qpc.Timestamp;
            cleanup = await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(worker.RecoveryWaitMilliseconds));
            if (!cleanup.CommandsSubmitted || cleanup.WorkerFailed)
                throw new IOException("Independent closed-hold cleanup did not confirm submissions and exact worker completion.");
            if (contextual && (worker.ContextualProgress is null || worker.ContextualEvidence is not { Complete: true,
                CachedRecoveryRangeObserved: true, Failures: 0, NativeFailures: 0 } recovered ||
                recovered.Authorization.Purpose != purpose || recovered.QpcFrequency != qpc.Frequency ||
                recovered.OpenSettledQpc <= 0 || recovered.ContextualAutoSettledQpc <= recovered.OpenSettledQpc ||
                recovered.LastCachedRpm is < 1050 or > 1150 ||
                recovered.LastCacheSampleQpc - recovered.ObservationStartedQpc < qpc.Frequency * 2 ||
                recovered.LastCacheSampleQpc > recovered.ObservationDeadlineQpc ||
                recovered.ObservationDeadlineQpc - recovered.ObservationStartedQpc != qpc.Frequency * 20 ||
                recovered.CompletedQpc > evidence.OuterDeadlineQpc))
                throw new IOException("Complete contextual native route and original bounded cached observation proof are missing.");
            EndPhase("recovery");
            StartPhase("closed-after");
            await clock.DelayAsync(5000, CancellationToken.None);
            afterObserved = true;
            EndPhase("closed-after");
            session.Token.ThrowIfCancellationRequested();
            state = MenuRunState.Completed;
            detail = "Bounded closed-hold software sequence completed; numeric retention, assumed OSD closure and physical protection remain separate.";

            async Task<int> ReadRpm()
            {
                var began = clock.Milliseconds;
                var rpm = await protocol.CachedRpmAsync(session.Token);
                if (clock.Milliseconds - began > FloorGetBudgetMs)
                    throw new TimeoutException("Complete cached getter exceeded its original bounded budget.");
                return rpm;
            }
            ShortRampNativePhaseEvidence RequireEvidence()
            {
                var observed = worker.NativePhaseEvidence;
                if (observed is null || observed.QpcFrequency != qpc.Frequency || observed.ArmQpc <= 0 ||
                    observed.ArmQpc < armBeforeQpc || observed.ArmQpc > armAfterQpc ||
                    observed.GuardianReadyQpc <= 0 || observed.OuterDeadlineQpc - observed.GuardianReadyQpc != qpc.Frequency * (contextual ? ShortRampPermit.ContextualOuterMs : 22000) / 1000 ||
                    (contextual ? observed.ArmQpc > observed.GuardianReadyQpc + qpc.Frequency * 5600 / 1000 : observed.ArmQpc + qpc.Frequency * 16400 / 1000 > observed.OuterDeadlineQpc))
                    throw new InvalidDataException("Native phase evidence is missing or has a foreign QPC clock.");
                return observed;
            }
            void RequireRampTime(int reserve)
            {
                evidence = RequireEvidence();
                if (qpc.Timestamp + qpc.Frequency * reserve / 1000 >= evidence.RampDeadlineQpc)
                    throw new TimeoutException("Original2400ms ramp cannot admit the complete remaining getter/mode0 work.");
                session.Token.ThrowIfCancellationRequested();
            }
            void RequireHoldTime(int reserve)
            {
                if (qpc.Timestamp + qpc.Frequency * reserve / 1000 >= evidence!.HoldDeadlineQpc)
                    throw new TimeoutException("Original native mode0 deadline cannot admit close/observation; immediate cleanup required.");
                session.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex)
        {
            state = ex is OperationCanceledException && token.IsCancellationRequested ? MenuRunState.Interrupted :
                requested ? MenuRunState.Error : MenuRunState.PreconditionFailed;
            detail = "Bounded closed-hold stopped: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            session.Cancel();
            if (worker is not null)
            {
                revokeQpc ??= qpc.Timestamp;
                worker.Revoke();
                if (activePhase is { } phase)
                {
                    activePhase = null;
                    try { log(new(clock.Milliseconds, "phase-aborted", phase, phase) { QpcTimestamp = qpc.Timestamp }); }
                    catch { state = MenuRunState.Error; }
                }
                cleanupQpc ??= qpc.Timestamp;
                var recoveringHere = cleanup is null;
                if (recoveringHere)
                    try { StartPhase("recovery"); } catch { state = MenuRunState.Error; }
                try { cleanup ??= await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(worker.RecoveryWaitMilliseconds)); }
                catch (Exception ex) { cleanup = new(false, "Closed-hold cleanup unconfirmed: " + ex.GetType().Name); state = MenuRunState.Error; }
                if (recoveringHere)
                {
                    try { EndPhase("recovery"); } catch { state = MenuRunState.Error; }
                }
                if (!afterObserved)
                {
                    // A returned failed-worker result still completed Finish;
                    // it must not skip the fixed post-cleanup raw observation.
                    try { StartPhase("closed-after"); } catch { state = MenuRunState.Error; }
                    await clock.DelayAsync(5000, CancellationToken.None);
                    afterObserved = true;
                    try { EndPhase("closed-after"); } catch { state = MenuRunState.Error; }
                }
                try
                {
                    evidence = worker.NativePhaseEvidence;
                    boundary = worker.PulseBoundaryObservation;
                    automatic = worker.AutomaticProgress;
                    contextualProgress = worker.ContextualProgress;
                    contextualEvidence = worker.ContextualEvidence;
                    mode2Count = worker.NativeMode2Count;
                    mode0Count = worker.NativeMode0Count;
                    closeCount = worker.NativeNormalCloseCount;
                }
                catch (Exception ex) { detail = "Closed-hold native lifecycle evidence failed: " + ex.GetType().Name; state = MenuRunState.Error; }
                callerCancellation.Dispose();
                try { await worker.DisposeAsync(); }
                catch (Exception ex) { detail = "Closed-hold worker disposal unconfirmed: " + ex.GetType().Name; state = MenuRunState.Error; }
                if (cleanupArmed && cleanup?.CommandsSubmitted != true || cleanup?.WorkerFailed == true) state = MenuRunState.Error;
            }
        }
        return new(state, detail, cached, cleanup, requested)
        {
            Purpose = purpose, OsdAdmission = admission.Metadata,
            NativeMode2Count = mode2Count, NativeMode0Count = mode0Count, NativeNormalCloseCount = closeCount,
            NativePhaseEvidence = evidence, ClosedHoldObservation = new(revokeQpc, cleanupQpc, qpc.Frequency, boundary),
            AutomaticProgress = automatic, ContextualProgress = contextualProgress, ContextualEvidence = contextualEvidence
        };

        void StartPhase(string name)
        {
            activePhase = name;
            log(new(clock.Milliseconds, "phase-start", name, name) { QpcTimestamp = qpc.Timestamp });
        }
        void EndPhase(string name)
        {
            activePhase = null;
            log(new(clock.Milliseconds, "phase-end", name, name) { QpcTimestamp = qpc.Timestamp });
        }
        static void HoldEntry(int rpm)
        {
            if (rpm is < BoundedFloorRpm or > MaximumBaselineRpm)
                throw new InvalidDataException("Final cached hold-entry reference must be950..1200RPM; no mode0 admitted.");
        }
    }

    public static async Task<MenuCleanupResult> CleanupAsync(IMenuTransport transport, IMenuClock clock,
        Action<MenuLogEntry> log, CancellationToken token, bool prioritizeAutomatic = false, bool strictFailures = false)
    {
        var protocol = new MenuProtocol(transport, clock, log, TransferTimeoutMs);
        var auto = false; var close = false; var failed = false;
        var commands = prioritizeAutomatic
            ? new[] { MenuCommand.Automatic, MenuCommand.Automatic, MenuCommand.Close, MenuCommand.Close }
            : new[] { MenuCommand.Automatic, MenuCommand.Close, MenuCommand.Automatic, MenuCommand.Close };
        for (var attempt = 0; attempt < commands.Length; attempt++)
            {
                var command = commands[attempt];
                try
                {
                    await protocol.SetAsync(command, token);
                    if (command == MenuCommand.Automatic) auto = true; else close = true;
                }
                catch (Exception ex)
                {
                    failed = true;
                    log(new(clock.Milliseconds, "short-cleanup-error", $"{attempt}/{command}: {ex.GetType().Name}", "cleanup"));
                }
            }
        return new(auto && close, (strictFailures && failed ? "At least one cleanup command failed. " : "") +
            (auto && close ? "Automatic1 and close submitted; physical state unconfirmed." :
                "One or both short cleanup commands were not submitted; physical state unconfirmed."),
            WorkerFailed: strictFailures && failed);
    }

    private static void Baseline(int rpm, bool isControl = false)
    {
        if (rpm is < MinimumBaselineRpm or > MaximumBaselineRpm)
            throw new InvalidDataException(isControl ? "Cached baseline must be1000..1200RPM; control refused." : "Cached baseline must be1000..1200RPM; pulse refused.");
    }
}
