using System.Diagnostics;
using System.IO;

namespace MonitorFanControl;

// Route authorization travels separately from every normal frame. Knowing a
// revoked permit does not confer cleanup Open/getter permissions.
public sealed record ContextualRecoveryAuthorization(string Nonce, ShortRampPurpose Purpose,
    ShortRampOwnedProcessIdentity Owner, int Attempt);
public interface IContextualRecoveryTransport
{
    Task<byte[]> TransferRecoveryAsync(byte[] report, bool reply, ContextualRecoveryAuthorization authorization, CancellationToken token);
}
public sealed record ShortRampContextualProgress(long FirstSettledQpc, long LastSettledQpc, int SuccessfulCount,
    ShortRampOwnedProcessIdentity FirstOwner, int FirstAttempt, ShortRampOwnedProcessIdentity LastOwner, int LastAttempt, long QpcFrequency);
public sealed record ContextualRecoveryEvidence(ContextualRecoveryAuthorization Authorization, int Stage,
    long OpenSettledQpc, long ContextualAutoSettledQpc, long ObservationStartedQpc, long ObservationDeadlineQpc,
    long LastCacheSampleQpc, int LastCachedRpm, bool CachedRecoveryRangeObserved, int Failures, int NativeFailures,
    bool Complete, long CompletedQpc, long QpcFrequency);

// Explicit values preserve the existing shared-memory and evidence stage ABI.
internal enum ContextualRecoveryStage
{
    DwellFailure = -1,
    NotStarted = 0,
    InitialAutomatic = 1,
    InitialClose = 2,
    Open = 3,
    ContextualAutomatic = 4,
    ObserveCache = 5,
    ObservationClose = 6,
    FinallyAutomatic = 7,
    FinallyClose = 8,
    Complete = 9
}

public sealed record ContextualHoldTimingEnvelope(int FirstContextualHungWriterMs, int FirstContextualSoleGuardianMs,
    int HungWriterCompleteRecoveryMs, int SoleGuardianCompleteRecoveryMs, int LatestConditionalFinalizationFromReadyMs,
    int SoleGuardianFaultTailMs, int ObservationMaximumMs, int ObservationWithTransactionTailMs,
    int OuterFromOriginalGuardianReadyMs, int FullRecoveryReserveMs, int MaximumCaptureCompositionMs, bool WithinOriginalOuterEnvelope);
public static class ContextualHoldTimingQualification
{
    public static ContextualHoldTimingEnvelope Evaluate(int hungDispatchMs = 200, int guardianDispatchMs = 330,
        int healthyFinalizationMs = 900, int soleFinalizationMs = 440)
    {
        if (hungDispatchMs < 0 || guardianDispatchMs < 0 || healthyFinalizationMs < 0 || soleFinalizationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(hungDispatchMs));
        const int setter = 460, observation = 20560, dwell = 300;
        var hungFirst = 280 + 300 + 180 + 3 * setter + 280 + hungDispatchMs;
        var soleFirst = 180 + 3 * setter + 280 + 450 + 180 + 3 * setter + 280 + guardianDispatchMs;
        var hungComplete = 280 + 300 + 180 + 7 * setter + dwell + observation + 300 + hungDispatchMs + healthyFinalizationMs;
        var soleComplete = 180 + 7 * setter + dwell + observation + 450 + 180 + 7 * setter + dwell + observation + 300 + guardianDispatchMs + soleFinalizationMs;
        // Capture:20s original pre-worker admission cap (including fixed15s,
        // drains and initial analysis)+5s finite worker factory+75s original
        // ready outer+5s fixed post+conservative1s final delivery drain. Parent
        // session90s and actor120s guards exceed their original-clock paths.
        const int capture = 20000 + 5000 + 75000 + 5000 + 1000;
        return new(hungFirst, soleFirst, hungComplete, soleComplete, 16000 + soleComplete,
            soleFirst - ShortRampPermit.ContextualNormalRecoveryLeadMs, 20000, observation,
            ShortRampPermit.ContextualOuterMs, ShortRampPermit.ContextualFullRecoveryReserveMs, capture,
            ShortRampPermit.ContextualOuterMs == 75000 && ShortRampPermit.ContextualFullRecoveryReserveMs == 51000 &&
            ShortRampPermit.ContextualNormalRecoveryLeadMs == 2620 && ShortRampPermit.ClosedHoldRampMs == 2400 &&
            ShortRampPermit.ClosedHoldDurationMs == 10000 && ShortRampPermit.ClosedHoldRampRecoveryMs == 2500 &&
            ShortRampOwnedWriterProcess.ActiveWriteRpcMs == 280 && ShortRampOwnedWriterProcess.ActiveReadRpcMs == 100 &&
            ShortRampOwnedWriterProcess.CooperativeDrainMs == 280 && ShortRampOwnedWriterProcess.TerminateAndReapMs == 300 &&
            ShortRampExecutionGate.NativeQuietGapMs == 180 && ShortRampExecutionGate.NativeIoTimeoutMs == 100 &&
            ShortRampOwnedWriterProcess.NativeIoTimeoutMs == 60 && hungDispatchMs <= 200 && guardianDispatchMs <= 330 &&
            healthyFinalizationMs <= 900 && soleFinalizationMs <= 440 && hungFirst <= 2620 && soleFirst <= 4460 &&
            hungComplete <= 51000 && soleComplete <= 51000 && 16000 + soleComplete <= 75000 &&
            MenuAudioSession.BufferDrainMs <= 1000 && capture < 120000);
    }
}

public sealed partial class ShortRampPermit
{
    private ContextualRecoveryStage RecoveryStage
    {
        get => (ContextualRecoveryStage)_view.ReadInt32(404);
        set => _view.Write(404, (int)value);
    }
    private static readonly long CurrentProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public const int ContextualOuterMs = 75000;
    public const int ContextualFullRecoveryReserveMs = 51000;
    public const int ContextualNormalRecoveryLeadMs = 2620;
    public bool IsClosedHold => Purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold or ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet;
    public int FullRecoveryReserveMs => UsesContextualRecovery ? ContextualFullRecoveryReserveMs : ClosedHoldFullRecoveryReserveMs;
    internal int OuterMs => UsesContextualRecovery ? ContextualOuterMs : ClosedHoldOuterMs;
    internal long OriginalPhaseCap => _view.ReadInt64(208) + Ticks(16000);
    internal int RemainingOuterMs => checked((int)Math.Clamp((long)(((Int128)(OuterDeadlineTimestamp - _clock.Timestamp) * 1000 + _clock.Frequency - 1) / _clock.Frequency), 1L, ContextualOuterMs));

    // The transaction owner alone advances native stages; the route driver may
    // advance a refused step as failed so finally Auto/Close remain reachable.
    internal ContextualRecoveryAuthorization BeginContextualRecoveryAttempt()
    {
        if (!UsesContextualRecovery || State != ShortRampPermitState.Cleanup)
            throw new InvalidOperationException("Contextual recovery requires its revoked immutable purpose.");
        var takeover = WriterTookCleanup;
        var owner = takeover ? OwnedWriter : GuardianIdentity;
        if (owner is null || takeover && GuardianAlive() || !takeover &&
            (owner.Pid != Environment.ProcessId || owner.StartUtcTicks != CurrentProcessStartUtcTicks))
            throw new InvalidOperationException("Contextual cleanup owner is not authenticated.");
        var bit = takeover ? 2 : 1;
        using var route = new Mutex(false, TransactionName);
        if (!TryEnter(route, 0)) throw new TimeoutException("Contextual attempt cannot bypass a busy transaction.");
        try
        {
            var used = _view.ReadInt32(400);
            if ((used & bit) != 0 || !takeover && used != 0 || takeover && (used & 2) != 0)
                throw new InvalidOperationException("Contextual cleanup owner already consumed its one attempt.");
            var attempt = _view.ReadInt32(388) + 1;
            if (attempt > 2) throw new InvalidOperationException("Contextual recovery attempt limit exceeded.");
            _view.Write(384, owner.Pid); _view.Write(388, attempt); _view.Write(392, owner.StartUtcTicks);
            _view.Write(400, used | bit); RecoveryStage = ContextualRecoveryStage.InitialAutomatic; _view.Write(408, 0);
            _view.Write(412, 0); _view.Write(416, 0L); _view.Write(424, 0L);
            _view.Write(432, 0L); _view.Write(440, 0L); _view.Write(448, 0L);
            _view.Write(456, 0); _view.Write(460, 0); _view.Write(464, 0L);
            _view.Write(508, 0); _view.Write(512, _clock.Timestamp); _view.Write(520, 0L);
            _view.Write(560, 0);
            return new(Nonce, Purpose, owner, attempt);
        }
        finally { route.ReleaseMutex(); }
    }
    private void CheckContextualAuthorization(ContextualRecoveryAuthorization? authorization, bool executingOwner)
    {
        if (!UsesContextualRecovery || authorization is null ||
            authorization.Nonce != Nonce || authorization.Purpose != Purpose || authorization.Owner.Nonce != Nonce ||
            authorization.Attempt != _view.ReadInt32(388) || authorization.Owner.Pid != _view.ReadInt32(384) ||
            authorization.Owner.StartUtcTicks != _view.ReadInt64(392) || State != ShortRampPermitState.Cleanup ||
            authorization.Owner != (WriterTookCleanup ? OwnedWriter : GuardianIdentity) ||
            WriterTookCleanup && GuardianAlive() || executingOwner &&
            (authorization.Owner.Pid != Environment.ProcessId || authorization.Owner.StartUtcTicks != CurrentProcessStartUtcTicks))
            throw new InvalidDataException("Foreign or stale contextual route/owner/attempt.");
    }
    internal void ValidateContextualFrame(byte[] report, bool reply, ContextualRecoveryAuthorization? authorization, bool executingOwner)
    {
        CheckContextualAuthorization(authorization, executingOwner);
        var kind = ShortRampExecutionGate.Classify(report, reply);
        var stage = RecoveryStage;
        var expected = stage switch {
            ContextualRecoveryStage.InitialAutomatic or ContextualRecoveryStage.ContextualAutomatic or ContextualRecoveryStage.FinallyAutomatic => ShortRampFrame.Automatic,
            ContextualRecoveryStage.InitialClose or ContextualRecoveryStage.ObservationClose or ContextualRecoveryStage.FinallyClose => ShortRampFrame.Close,
            ContextualRecoveryStage.Open => ShortRampFrame.Open, _ => (ShortRampFrame?)null };
        if (stage == ContextualRecoveryStage.ObserveCache)
        {
            if (_view.ReadInt64(432) <= 0 || _view.ReadInt64(424) <= 0)
                throw new InvalidOperationException("Recovery GET4 lacks a successful contextual prefix and observation clock.");
            var pending = _view.ReadInt32(408);
            if (kind == ShortRampFrame.Getter && Convert.ToHexString(report.AsSpan(9, report[5])) == "4F83031804BD" && pending == 0 &&
                _clock.Timestamp < _view.ReadInt64(440)) return;
            if (kind == ShortRampFrame.Exchange && report[4] == 4 && report[5] == 16 && pending == 4 &&
                _clock.Timestamp <= _view.ReadInt64(440) + Ticks(560)) return;
            throw new InvalidDataException("Contextual observation permits only one paired known GET4 transaction before its original deadline.");
        }
        if (kind != expected || _view.ReadInt32(408) != 0)
            throw new InvalidDataException("Contextual cleanup frame is outside its literal ordered route.");
        if (stage == ContextualRecoveryStage.FinallyAutomatic && _clock.Timestamp < _view.ReadInt64(464) + Ticks(300))
            throw new InvalidOperationException("Final automatic requires the original300ms dwell after observation close.");
    }
    internal void ContextualBeforeNative(byte[] report, bool reply, ContextualRecoveryAuthorization authorization)
    {
        ValidateContextualFrame(report, reply, authorization, true);
        RecordPersistentRecoveryProgress();
        var stage = RecoveryStage;
        if (_clock.Timestamp > OuterDeadlineTimestamp) ContextualFailure();
        if (stage == ContextualRecoveryStage.ObserveCache)
        {
            if (report[4] == 3) { _view.Write(408, 4); _view.Write(528, _clock.Timestamp); }
        }
        else RecoveryStage++;
    }
    internal void ContextualNativeReply(byte[] request, byte[] response)
    {
        if (request[4] != 4) return;
        _view.Write(408, 0); // A malformed response is consumed, never reusable.
        if (response.Length != 65 || response[0] != 0 || response[1] is < 12 or > 20 ||
            response[2] != request[2] || response[3] != 0x39 || response[4] != 0 ||
            response[5] != 0x18 || response[6] != 4 || response[7] != 4 || response[8] != 0 ||
            _clock.Timestamp - _view.ReadInt64(528) > Ticks(560))
            throw new InvalidDataException("Malformed, foreign or late contextual GET4 reply.");
        var rpm = response[9] * 256 + response[10];
        var now = _clock.Timestamp;
        _view.Write(448, now); _view.Write(456, rpm);
        if (now - _view.ReadInt64(432) >= Ticks(2000) && now <= _view.ReadInt64(440) && rpm is >= 1050 and <= 1150)
            _view.Write(460, 1);
    }
    internal void ContextualNativeFinished(byte[] report, ShortRampFrame kind, bool succeeded)
    {
        RecordPersistentRecoveryProgress();
        if (!succeeded) { _view.Write(408, 0); _view.Write(560, _view.ReadInt32(560) + 1); ContextualFailure(); return; }
        var now = _clock.Timestamp;
        var stage = RecoveryStage;
        if (kind == ShortRampFrame.Open && stage == ContextualRecoveryStage.ContextualAutomatic) _view.Write(416, now);
        if (kind == ShortRampFrame.Automatic && stage == ContextualRecoveryStage.ObserveCache && _view.ReadInt64(416) > 0)
        {
            _view.Write(424, now);
            var first = _view.ReadInt64(472) == 0;
            if (first)
            {
                _view.Write(492, _view.ReadInt32(384)); _view.Write(496, _view.ReadInt64(392)); _view.Write(504, _view.ReadInt32(388));
            }
            _view.Write(536, _view.ReadInt32(384)); _view.Write(544, _view.ReadInt64(392)); _view.Write(552, _view.ReadInt32(388));
            // At most two owners can publish one contextual point each. First
            // proof is immutable; last-QPC publishes the complete second owner.
            Thread.MemoryBarrier();
            _view.Write(480, now);
            if (first) { Thread.MemoryBarrier(); _view.Write(472, now); }
        }
        if (kind == ShortRampFrame.Close && stage == ContextualRecoveryStage.FinallyAutomatic) _view.Write(464, now);
        if (kind == ShortRampFrame.Close && stage == ContextualRecoveryStage.Complete)
        { _view.Write(520, now); Thread.MemoryBarrier(); _view.Write(508, 1); }
    }
    private void ContextualFailure() { _view.Write(412, _view.ReadInt32(412) + 1); _view.Write(556, _view.ReadInt32(556) + 1); }
    internal void FailContextualStep(ContextualRecoveryAuthorization authorization, ContextualRecoveryStage expectedStage)
    {
        CheckContextualAuthorization(authorization, false);
        ContextualFailure();
        if (expectedStage > ContextualRecoveryStage.NotStarted) _view.Write(560, _view.ReadInt32(560) + 1);
        if (RecoveryStage == expectedStage) RecoveryStage++;
        if (expectedStage == ContextualRecoveryStage.ObservationClose && _view.ReadInt64(464) == 0) _view.Write(464, _clock.Timestamp);
    }
    internal void BeginContextualObservation(ContextualRecoveryAuthorization authorization)
    {
        CheckContextualAuthorization(authorization, false);
        if (RecoveryStage != ContextualRecoveryStage.ObserveCache || _view.ReadInt64(424) <= 0 || _view.ReadInt64(432) != 0)
            throw new InvalidOperationException("Contextual observation cannot be started or renewed.");
        var began = _clock.Timestamp;
        _view.Write(432, began); _view.Write(440, checked(began + Ticks(20000)));
    }
    internal void EndContextualObservation(ContextualRecoveryAuthorization authorization, bool success)
    {
        CheckContextualAuthorization(authorization, false);
        if (RecoveryStage != ContextualRecoveryStage.ObserveCache) throw new InvalidOperationException("Contextual observation order changed.");
        if (!success || _view.ReadInt32(460) != 1 || _view.ReadInt32(408) != 0 || _clock.Timestamp > _view.ReadInt64(440) + Ticks(560))
            ContextualFailure();
        _view.Write(408, 0); RecoveryStage = ContextualRecoveryStage.ObservationClose;
    }
    public ShortRampContextualProgress? ContextualProgress
    {
        get
        {
            if (!UsesContextualRecovery) return null;
            var first = _view.ReadInt64(472);
            if (first <= 0) return null;
            Thread.MemoryBarrier();
            var firstOwner = new ShortRampOwnedProcessIdentity(_view.ReadInt32(492), _view.ReadInt64(496), Nonce);
            var firstAttempt = _view.ReadInt32(504);
            var last = _view.ReadInt64(480);
            // A second owner still publishing its fields cannot replace the
            // already complete first point, even if it dies between writes.
            if (last <= first) return new(first, first, 1, firstOwner, firstAttempt, firstOwner, firstAttempt, _clock.Frequency);
            Thread.MemoryBarrier();
            return new(first, last, 2, firstOwner, firstAttempt,
                new(_view.ReadInt32(536), _view.ReadInt64(544), Nonce), _view.ReadInt32(552), _clock.Frequency);
        }
    }
    public ContextualRecoveryEvidence? ContextualEvidence => UsesContextualRecovery && _view.ReadInt32(388) > 0
        ? new(new(Nonce, Purpose, new(_view.ReadInt32(384), _view.ReadInt64(392), Nonce), _view.ReadInt32(388)), (int)RecoveryStage,
            _view.ReadInt64(416), _view.ReadInt64(424), _view.ReadInt64(432), _view.ReadInt64(440), _view.ReadInt64(448),
            _view.ReadInt32(456), _view.ReadInt32(460) == 1, _view.ReadInt32(556), _view.ReadInt32(560), _view.ReadInt32(508) == 1, _view.ReadInt64(520), _clock.Frequency) : null;
}

internal static class ContextualRecovery
{
    internal static async Task<MenuCleanupResult> RunAsync(ShortRampPermit permit, IMenuTransport transport,
        IMenuClock clock, IShortRampQpcClock qpc, Action<MenuLogEntry> log, CancellationToken token)
    {
        var authorization = permit.BeginContextualRecoveryAttempt();
        if (transport is not IContextualRecoveryTransport contextual) throw new IOException("Transport lacks contextual route authorization.");
        var protocol = new MenuProtocol(new AuthorizedTransport(transport.Identity, contextual, authorization), clock,
            entry => log(entry with { Phase = "contextual-recovery", QpcTimestamp = qpc.Timestamp }), 450);
        var failed = false;
        await Set(MenuCommand.Automatic, ContextualRecoveryStage.InitialAutomatic);
        await Set(MenuCommand.Close, ContextualRecoveryStage.InitialClose);
        await Set(MenuCommand.Open, ContextualRecoveryStage.Open);
        await Set(MenuCommand.Automatic, ContextualRecoveryStage.ContextualAutomatic);
        var observed = false;
        try
        {
            permit.BeginContextualObservation(authorization);
            var deadline = permit.ContextualEvidence!.ObservationDeadlineQpc;
            while (qpc.Timestamp < deadline)
            {
                await protocol.CachedRpmAsync(token);
                if (permit.ContextualEvidence!.CachedRecoveryRangeObserved) { observed = true; break; }
                var remaining = checked((int)Math.Max(0, (deadline - qpc.Timestamp) * 1000 / qpc.Frequency));
                if (remaining > 0) await clock.DelayAsync(Math.Min(300, remaining), token);
            }
            if (!observed) throw new TimeoutException("Cached recovery-range observation1050..1150 was not established after2s within original20s.");
        }
        catch (Exception ex) { failed = true; log(new(clock.Milliseconds, "contextual-cache-error", ex.GetType().Name, "contextual-recovery") { QpcTimestamp = qpc.Timestamp }); }
        finally
        {
            permit.EndContextualObservation(authorization, observed);
            await Set(MenuCommand.Close, ContextualRecoveryStage.ObservationClose);
            // Outer finally is retained even when a previous close failed.
            try
            {
                var dwell = new ShortRampNativeQuietHorizon(qpc, 300, "Contextual finally dwell clock did not advance.");
                while (!dwell.Elapsed)
                {
                    try { await clock.DelayAsync(dwell.RemainingMilliseconds, CancellationToken.None); }
                    catch (Exception ex) { dwell.RecordWaitFailure(ex); }
                    dwell.ObserveProgress(qpc.Timestamp);
                }
                dwell.ThrowWaitFailure();
            }
            catch { failed = true; permit.FailContextualStep(authorization, ContextualRecoveryStage.DwellFailure); }
            await Set(MenuCommand.Automatic, ContextualRecoveryStage.FinallyAutomatic);
            await Set(MenuCommand.Close, ContextualRecoveryStage.FinallyClose);
        }
        var evidence = permit.ContextualEvidence!;
        var complete = evidence.Complete && evidence.NativeFailures == 0 && evidence.ContextualAutoSettledQpc > 0;
        return new(complete, "Complete contextual route and cached recovery-range observation are software evidence only; physical Auto and fresh tachometry remain unconfirmed.",
            failed || !complete || !evidence.CachedRecoveryRangeObserved || evidence.Failures != 0 || qpc.Timestamp > permit.OuterDeadlineTimestamp);

        async Task Set(MenuCommand command, ContextualRecoveryStage stage)
        {
            try { await protocol.SetAsync(command, token); }
            catch (Exception ex)
            {
                failed = true; permit.FailContextualStep(authorization, stage);
                log(new(clock.Milliseconds, "contextual-cleanup-error", $"{(int)stage}/{command}: {ex.GetType().Name}", "contextual-recovery") { QpcTimestamp = qpc.Timestamp });
            }
        }
    }
    private sealed class AuthorizedTransport(MenuDeviceIdentity identity, IContextualRecoveryTransport transport,
        ContextualRecoveryAuthorization authorization) : IMenuTransport
    {
        public MenuDeviceIdentity Identity => identity;
        public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token) => transport.TransferRecoveryAsync(report, reply, authorization, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
