using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace MonitorFanControl;

public interface IShortRampQpcClock
{
    long Timestamp { get; }
    long Frequency { get; }
}
public sealed class ShortRampSystemQpcClock : IShortRampQpcClock
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

// The wait adapters stay synchronous/native or asynchronous/orphan-owned;
// both measure one original horizon and defer wait errors until it elapses.
internal sealed class ShortRampNativeQuietHorizon
{
    private readonly long _frequency;
    private readonly long _deadline;
    private readonly string _clockFailureMessage;
    private long _observed;
    private Exception? _waitFailure;

    public ShortRampNativeQuietHorizon(IShortRampQpcClock clock, int milliseconds, string clockFailureMessage)
    {
        _frequency = clock.Frequency;
        _observed = clock.Timestamp;
        var quietTicks = checked((long)(((Int128)_frequency * milliseconds + 999) / 1000));
        _deadline = checked(_observed + quietTicks);
        _clockFailureMessage = clockFailureMessage;
    }

    public bool Elapsed => _observed >= _deadline;
    public int RemainingMilliseconds => checked((int)(((Int128)(_deadline - _observed) * 1000 + _frequency - 1) / _frequency));
    public void RecordWaitFailure(Exception error) => _waitFailure ??= error;
    public void ObserveProgress(long now)
    {
        if (now <= _observed) throw new TimeoutException(_clockFailureMessage, _waitFailure);
        _observed = now;
    }
    public void ThrowWaitFailure()
    {
        if (_waitFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_waitFailure).Throw();
    }
}

public enum ShortRampPurpose { Mode2, NoReductionControl, BoundedClosedHold, ContextualClosedHold, PersistentQuiet, ReferencePersistentQuiet }
public static class ShortRampPurposePolicy
{
    public static void Validate(ShortRampPurpose purpose)
    {
        if (!Enum.IsDefined(purpose))
            throw new InvalidDataException("Unknown short-ramp purpose.");
    }
    public static string Token(ShortRampPurpose purpose) { Validate(purpose); return purpose.ToString(); }
    public static ShortRampPurpose Parse(string token) => token switch
    {
        "Mode2" => ShortRampPurpose.Mode2,
        "NoReductionControl" => ShortRampPurpose.NoReductionControl,
        "BoundedClosedHold" => ShortRampPurpose.BoundedClosedHold,
        "ContextualClosedHold" => ShortRampPurpose.ContextualClosedHold,
        "PersistentQuiet" => ShortRampPurpose.PersistentQuiet,
        "ReferencePersistentQuiet" => ShortRampPurpose.ReferencePersistentQuiet,
        _ => throw new InvalidDataException("Unknown short-ramp purpose token.")
    };
}
public enum ShortRampPermitState { Unarmed, Armed, Used, Revoked, Cleanup }
public sealed record ShortRampPermitSnapshot(ShortRampPermitState State, long ArmTimestamp,
    long ExecutionExpiryTimestamp, long StopTimestamp);
public sealed record ShortRampOwnedProcessIdentity(int Pid, long StartUtcTicks, string Nonce);
public sealed record ShortRampAutomaticProgress(long FirstSettledQpc, long LastSettledQpc,
    int SuccessfulCount, long QpcFrequency);
public sealed record ShortRampNativePhaseEvidence(long ArmQpc, long RampDeadlineQpc,
    long Mode0AttemptQpc, long Mode0SettledQpc, long HoldDeadlineQpc,
    long NormalCloseAttemptQpc, long NormalCloseSettledQpc, long QpcFrequency,
    long GuardianReadyQpc, long OuterDeadlineQpc);

// Windows kernel objects are deliberately shared by the parent and independent
// guardian. Revocation is a separate event: it never waits for the state mutex,
// even if a suspended parent happens to own that mutex.
#pragma warning disable CA1416 // This production transport and its process fixtures run on Windows.
public sealed partial class ShortRampPermit : IDisposable
{
    public const int ExecutionWindowMs = 100;
    public const int StopDeadlineMs = 1500;
    public const int ClosedHoldRampMs = 2400;
    public const int ClosedHoldRampRecoveryMs = 2500;
    public const int ClosedHoldDurationMs = 10000;
    public const int ClosedHoldOuterMs = 22000;
    public const int ClosedHoldRecoveryReserveMs = 4000;
    public const int ClosedHoldFullRecoveryReserveMs = 6000;
    private const int Magic = 0x53525031;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _stateLock;
    private readonly EventWaitHandle _revoked;
    private readonly EventWaitHandle _cleanup;
    private readonly EventWaitHandle _writerExited;
    private readonly EventWaitHandle _boundaryUnknown;
    private readonly IShortRampQpcClock _clock;
    public string Nonce { get; }
    public ShortRampPurpose Purpose { get; }
    public MenuDeviceIdentity Identity { get; }
    public string KernelPrefix { get; }
    internal string TransactionName => KernelPrefix + ".Transaction." + Nonce;
    private static readonly MenuDeviceIdentity ExpectedIdentity = new(1, 0x043e, 0x9c33, 0x0111, 0xff00, 1, 65, 65);

    private ShortRampPermit(string nonce, MenuDeviceIdentity identity, string? kernelPrefix,
        IShortRampQpcClock? clock, bool create, ShortRampPurpose purpose)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid short session nonce.");
        if (identity != ExpectedIdentity) throw new InvalidDataException("Unexpected short-ramp bridge identity.");
        Nonce = nonce; Identity = identity; Purpose = purpose;
        KernelPrefix = kernelPrefix ?? @"Local\MonitorFanControl.ShortRamp";
        if (string.IsNullOrWhiteSpace(KernelPrefix) || KernelPrefix.Length > 120)
            throw new ArgumentException("Invalid short-ramp kernel object prefix.");
        _clock = clock ?? new ShortRampSystemQpcClock();
        if (_clock.Frequency <= 0) throw new ArgumentException("Invalid monotonic clock frequency.");
        var name = KernelPrefix + ".Permit." + nonce;
        _stateLock = new Mutex(false, name + ".State");
        _revoked = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Revoked");
        _cleanup = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Cleanup");
        _writerExited = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".WriterExited");
        _boundaryUnknown = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".BoundaryUnknown");
        try
        {
            _map = create ? MemoryMappedFile.CreateNew(name, purpose == ShortRampPurpose.ReferencePersistentQuiet ? ReferenceMapBytes : purpose == ShortRampPurpose.PersistentQuiet ? 768 : purpose == ShortRampPurpose.ContextualClosedHold ? 640 : 384) : MemoryMappedFile.OpenExisting(name);
            _view = _map.CreateViewAccessor();
            using var held = LockState(150);
            var fingerprint = Fingerprint(nonce, identity, purpose);
            if (create)
            {
                // Existing events are never reset. Reusing an old session nonce
                // therefore cannot resurrect a revoked permit.
                if (_revoked.WaitOne(0) || _cleanup.WaitOne(0)) throw new InvalidOperationException("Short session nonce was already revoked.");
                _view.Write(0, Magic); _view.Write(4, (int)ShortRampPermitState.Unarmed);
                _view.Write(24, _clock.Frequency);
                _view.WriteArray(64, fingerprint, 0, fingerprint.Length);
                if (IsReference) InitializeReferenceBinding();
                _view.Flush();
            }
            else
            {
                var actual = new byte[fingerprint.Length]; _view.ReadArray(64, actual, 0, actual.Length);
                if (_view.ReadInt32(0) != Magic || _view.ReadInt64(24) != _clock.Frequency || !actual.SequenceEqual(fingerprint))
                    throw new InvalidDataException("Foreign short permit identity, nonce, or monotonic clock.");
                if (IsReference) ValidateReferenceBinding(false);
            }
        }
        catch { _view?.Dispose(); _map?.Dispose(); _revoked.Dispose(); _cleanup.Dispose(); _writerExited.Dispose(); _boundaryUnknown.Dispose(); _stateLock.Dispose(); throw; }
    }

    public static ShortRampPermit Create(string nonce, MenuDeviceIdentity identity, string? kernelPrefix = null,
        IShortRampQpcClock? clock = null, ShortRampPurpose purpose = ShortRampPurpose.Mode2) => new(nonce, identity, kernelPrefix, clock, true, purpose);
    public static ShortRampPermit Open(string nonce, MenuDeviceIdentity identity, string? kernelPrefix = null,
        IShortRampQpcClock? clock = null, ShortRampPurpose purpose = ShortRampPurpose.Mode2) => new(nonce, identity, kernelPrefix, clock, false, purpose);

    public void RegisterGuardian(int guardianPid, long guardianStartUtcTicks)
    {
        if (guardianPid <= 0 || guardianStartUtcTicks <= 0) throw new ArgumentException("Missing guardian process identity.");
        using var held = LockState(0);
        if (State != ShortRampPermitState.Unarmed) throw new InvalidOperationException("Guardian registration is closed.");
        var previousPid = _view.ReadInt32(8); var previousStart = _view.ReadInt64(16);
        if (previousPid != 0 && (previousPid != guardianPid || previousStart != guardianStartUtcTicks))
            throw new InvalidOperationException("Guardian process identity changed.");
        _view.Write(8, guardianPid); _view.Write(16, guardianStartUtcTicks); _view.Flush();
        if (OwnedWriter is null) RecordGuardianReady();
    }

    // Only the launcher registers the PID obtained from Process.Start, after
    // checking the nonce/start-time handshake against its retained handle.
    internal void RegisterOwnedWriter(ShortRampOwnedProcessIdentity writer)
    {
        if (writer.Nonce != Nonce || writer.Pid <= 0 || writer.Pid == Environment.ProcessId || writer.StartUtcTicks <= 0)
            throw new InvalidDataException("Invalid owned writer identity.");
        using var held = LockState(0);
        if (State != ShortRampPermitState.Unarmed || _view.ReadInt32(96) != 0)
            throw new InvalidOperationException("Owned writer registration is immutable.");
        _view.Write(96, writer.Pid); _view.Write(104, writer.StartUtcTicks); _view.Flush();
    }
    public ShortRampOwnedProcessIdentity? OwnedWriter => _view.ReadInt32(96) is var pid && pid > 0
        ? new(pid, _view.ReadInt64(104), Nonce) : null;
    internal bool OwnedWriterExitConfirmed => _writerExited.WaitOne(0);
    internal bool CooperativeWriterRetired => _view.ReadInt32(132) == 1;
    internal bool WriterTookCleanup => _view.ReadInt32(136) == 1;
    internal ShortRampOwnedProcessIdentity? GuardianIdentity => _view.ReadInt32(8) is var pid && pid > 0
        ? new(pid, _view.ReadInt64(16), Nonce) : null;
    internal void ConfirmGuardianReady() { RecordGuardianReady(); _view.Write(140, 1); }
    private void RecordGuardianReady()
    {
        if (!IsClosedHold || _view.ReadInt64(208) != 0) return;
        var now = _clock.Timestamp;
        _view.Write(208, now); _view.Write(216, checked(now + Ticks(OuterMs)));
    }
    internal void RetireCooperativeWriter() { Revoke(); _view.Write(132, 1); }
    internal void ConfirmWriterCleanupTakeover(ShortRampOwnedProcessIdentity writer)
    {
        if (OwnedWriter != writer || writer.Pid != Environment.ProcessId || GuardianAlive())
            throw new InvalidOperationException("Writer cleanup takeover identity/death proof failed.");
        BeginCleanup(); _view.Write(136, 1);
    }
    internal void ConfirmOwnedWriterExit(ShortRampOwnedProcessIdentity writer)
    {
        if (OwnedWriter != writer) throw new InvalidDataException("Foreign writer exit confirmation.");
        _writerExited.Set();
    }
    internal void RecordGuardianCleanup(MenuCleanupResult result)
    {
        _view.Write(124, result.CommandsSubmitted ? 1 : 0);
        _view.Write(128, result.WorkerFailed ? 1 : 0);
        _view.Flush(); _view.Write(120, 1); _view.Flush();
    }
    internal MenuCleanupResult? GuardianCleanup => _view.ReadInt32(120) == 1
        ? new(_view.ReadInt32(124) == 1, "Guardian shared cleanup submission report; physical state unconfirmed.", _view.ReadInt32(128) == 1) : null;
    // Only the transaction owner writes these aligned fields, after successful
    // native IO and its mandatory quiet interval. No state lock, disk flush or
    // observer is allowed between native settling and publishing this evidence.
    internal void RecordAutomaticNativeSettled()
    {
        if (_view.ReadInt64(32) == 0) return; // Pre-arm preparation is unrelated.
        var now = _clock.Timestamp;
        if (_view.ReadInt64(144) == 0) _view.Write(144, now);
        _view.Write(152, now);
        _view.Write(160, checked(_view.ReadInt32(160) + 1));
    }
    public int NativeMode2Count => _view.ReadInt32(184);
    public int NativeMode0Count => _view.ReadInt32(228);
    public int NativeNormalCloseCount => _view.ReadInt32(232);
    public ShortRampNativePhaseEvidence? NativePhaseEvidence => IsClosedHold && _view.ReadInt64(32) > 0
        ? new(_view.ReadInt64(32), checked(_view.ReadInt64(32) + Ticks(IsReference ? ReferenceLoopDecisionMs : ClosedHoldRampMs)),
            _view.ReadInt64(240), _view.ReadInt32(224) >= 2 ? _view.ReadInt64(248) : 0,
            _view.ReadInt32(224) >= 2 ? _view.ReadInt64(256) : 0,
            _view.ReadInt64(264), _view.ReadInt32(224) >= 3 ? _view.ReadInt64(272) : 0,
            _clock.Frequency, _view.ReadInt64(208), OuterDeadlineTimestamp) : null;
    internal long IndependentStopTimestamp
    {
        get
        {
            if (IsPersistent && PersistentQuietActive) return long.MaxValue;
            var stop = _view.ReadInt64(48);
            // Phase is published last, only by the native transaction owner.
            // A partially written transition always retains the earlier stop.
            if (IsClosedHold && _view.ReadInt32(224) >= 2 &&
                _view.ReadInt64(248) is var settled && settled > 0 && settled <= _view.ReadInt64(32) + Ticks(IsReference ? ReferenceSettledMs : ClosedHoldRampMs))
                stop = _view.ReadInt64(256) - Ticks(Purpose == ShortRampPurpose.ContextualClosedHold ? ContextualNormalRecoveryLeadMs : 0);
            if (IsClosedHold) stop = Math.Min(stop, IsPersistent ? OriginalPhaseCap : _view.ReadInt64(216) - Ticks(FullRecoveryReserveMs));
            return stop;
        }
    }
    internal long OuterRecoveryTimestamp => UsesContextualRecovery ? OriginalPhaseCap : _view.ReadInt64(216) - Ticks(FullRecoveryReserveMs);
    internal long OuterDeadlineTimestamp => IsPersistent && PersistentStopQpc > 0 ? checked(PersistentStopQpc + Ticks(PersistentRecoveryMs)) : _view.ReadInt64(216);
    internal void RecordMode2NativeAttempt() => _view.Write(184, checked(NativeMode2Count + 1));
    public ShortRampAutomaticProgress? AutomaticProgress => _view.ReadInt64(144) is var first && first > 0
        ? new(first, _view.ReadInt64(152), _view.ReadInt32(160), _clock.Frequency) : null;
    public ShortRampObservedPulseBoundary? PulseBoundaryObservation
    {
        get
        {
            var first = _view.ReadInt64(176);
            return first > 0 ? new(first, null, _clock.Frequency, !_boundaryUnknown.WaitOne(0)) : null;
        }
    }

    public ShortRampPermitSnapshot Arm(int guardianPid, long guardianStartUtcTicks)
    {
        using var held = LockState(0);
        if (State != ShortRampPermitState.Unarmed || _view.ReadInt32(8) != guardianPid || _view.ReadInt64(16) != guardianStartUtcTicks)
            throw new InvalidOperationException("Short permit cannot be armed or guardian identity changed.");
        if (!GuardianAlive()) throw new InvalidOperationException("Independent guardian is unavailable.");
        var now = _clock.Timestamp;
        if (IsClosedHold)
        {
            if (_view.ReadInt64(208) <= 0 || (IsReference ? now >= OriginalPhaseCap : UsesContextualRecovery ? now > _view.ReadInt64(208) + Ticks(5600) : now + Ticks(ClosedHoldRampMs + ClosedHoldDurationMs + ClosedHoldRecoveryReserveMs) > _view.ReadInt64(216)))
                throw new TimeoutException("Original guardian-ready outer cap lacks complete ramp/hold/recovery reserve.");
            if (_view.ReadInt32(304) != (IsReference ? 63 : 127) || _view.ReadInt32(56) != 1 ||
                now - (IsReference ? ReferenceBaselineEpoch : _view.ReadInt64(312)) < Ticks(700) || _view.ReadInt32(288) is < 1000 or > 1200 ||
                _view.ReadInt64(280) < (IsReference ? ReferenceBaselineEpoch : _view.ReadInt64(312)) + Ticks(700) || _view.ReadInt32(236) != 0)
                throw new InvalidDataException("Native closed-hold preparation, consumed baseline, or700ms open dwell is incomplete.");
            if (IsReference && (now > _view.ReadInt64(696) + Ticks(2000) || now >= OriginalPhaseCap))
                throw new TimeoutException("Original reference preparation horizon expired.");
            _view.Write(224, 1);
        }
        _view.Write(32, now); _view.Write(40, checked(now + Ticks(ExecutionWindowMs)));
        _view.Write(48, checked(now + Ticks(IsReference ? ReferenceIndependentStopMs : IsClosedHold ? ClosedHoldRampRecoveryMs :
            OwnedWriter is null ? StopDeadlineMs : ShortRampOwnedWriterProcess.StopDeadlineMs)));
        _view.Write(4, (int)ShortRampPermitState.Armed); _view.Flush();
        return ReadSnapshot();
    }

    public ShortRampPermitSnapshot Snapshot
    {
        get
        {
            if (State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup) return ReadSnapshot();
            using var held = LockState(0); return ReadSnapshot();
        }
    }
    public void Revoke()
    {
        if (IsReference && ArbitrateReferenceDisposition(1) == 2) return;
        if (IsPersistent) FreezePersistentStop();
        // The first event publication preserves a conservative shared QPC mark.
        // Never wait for a possibly suspended actor. Contention sets a sticky
        // uncertainty event BEFORE revocation; later calls cannot restore known.
        if (!_revoked.WaitOne(0))
        {
            if (TryEnter(_stateLock, 0))
            {
                try { if (!_revoked.WaitOne(0)) { _view.Write(176, _clock.Timestamp); _revoked.Set(); } }
                finally { _stateLock.ReleaseMutex(); }
            }
            else { _boundaryUnknown.Set(); _revoked.Set(); }
        }
        TryRecordTerminalState(ShortRampPermitState.Revoked);
    }
    public void BeginCleanup()
    {
        Revoke();
        if (ReferenceReleased) return;
        _cleanup.Set();
        TryRecordTerminalState(ShortRampPermitState.Cleanup);
    }
    private void TryRecordTerminalState(ShortRampPermitState state)
    {
        // The events are authoritative even if the parent is hung while holding
        // the state mutex. Cleanup admission must not wait for that parent.
        if (!TryEnter(_stateLock, 0)) return;
        try { _view.Write(4, (int)state); }
        finally { _stateLock.ReleaseMutex(); }
    }
    internal ShortRampPermitState State => _cleanup.WaitOne(0) ? ShortRampPermitState.Cleanup :
        _revoked.WaitOne(0) ? ShortRampPermitState.Revoked : (ShortRampPermitState)_view.ReadInt32(4);
    private ShortRampPermitSnapshot ReadSnapshot() => new(State, _view.ReadInt64(32), _view.ReadInt64(40), _view.ReadInt64(48));
    private long Ticks(int milliseconds) => checked(_clock.Frequency * milliseconds / 1000);

    internal bool GuardianAlive()
    {
        if (OwnedWriter is not null && _view.ReadInt32(140) != 1) return false;
        var pid = _view.ReadInt32(8); var start = _view.ReadInt64(16);
        if (pid <= 0 || start <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == start;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void ValidateQueuedFrame(byte[] report, bool reply, ContextualRecoveryAuthorization? authorization = null)
    {
        ThrowIfReferenceReleased();
        ShortRampExecutionGate.ValidateForQueue(report, reply, Purpose);
        if (IsReference) ValidateReferenceFrame(report, reply, false);
        if (!IsClosedHold) return;
        var kind = ShortRampExecutionGate.Classify(report, reply);
        var state = State;
        if (UsesContextualRecovery && state is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
        { ValidateContextualFrame(report, reply, authorization, false); return; }
        if (authorization is not null) throw new InvalidDataException("Contextual authorization cannot enter normal work.");
        if (IsPersistent && _view.ReadInt32(224) >= 3)
            throw new InvalidOperationException("Persistent Quiet has closed all normal hardware admission.");
        if (state is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
        {
            if (kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close))
                throw new InvalidOperationException("Revoked closed-hold queue admits only cleanup.");
            return;
        }
        if (_view.ReadInt32(224) >= 2)
        {
            if (kind != ShortRampFrame.Automatic && (kind != ShortRampFrame.Close || NativeNormalCloseCount != 0 || _view.ReadInt32(224) != 2))
                throw new InvalidOperationException("Established native hold queue admits one normal close or automatic cleanup.");
            return;
        }
        if (kind == ShortRampFrame.Mode0 && (state != ShortRampPermitState.Used || NativeMode2Count != 1 || NativeMode0Count != 0 ||
            _view.ReadInt32(236) != 0 || _view.ReadInt64(280) <= _view.ReadInt64(320) || _view.ReadInt32(288) < (IsReference ? ReferenceEntryMinimumCachedRpm : 950) || _view.ReadInt32(288) > 1200))
            throw new InvalidDataException("Queued mode0 lacks its one-shot native final getter gate.");
        if (kind == ShortRampFrame.Mode2 && (state != ShortRampPermitState.Armed || NativeMode2Count != 0))
            throw new InvalidOperationException("Queued mode2 is unarmed or already consumed.");
        if (kind == ShortRampFrame.Open && (state != ShortRampPermitState.Unarmed || _view.ReadInt32(56) != 0))
            throw new InvalidOperationException("Queued Open is repeated or outside preparation.");
    }

    internal byte[] Admit(ShortRampFrame kind, string nonce, MenuDeviceIdentity identity,
        bool guardianAlive, int nativeReserveMs, CancellationToken token, Func<byte[]> submit,
        byte[] report, bool reply, ContextualRecoveryAuthorization? authorization = null)
    {
        ThrowIfReferenceReleased();
        if (nonce != Nonce || identity != Identity) throw new InvalidDataException("Foreign short execution identity or nonce.");
        ShortRampPurposePolicy.Validate(Purpose);
        if (IsReference) ValidateReferenceFrame(report, reply, true);
        if (UsesContextualRecovery && State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
        { ValidateContextualFrame(report, reply, authorization, true); token.ThrowIfCancellationRequested(); return submit(); }
        if (authorization is not null) throw new InvalidDataException("Recovery authorization cannot revive normal work.");
        if (IsPersistent && _view.ReadInt32(224) >= 3)
            throw new InvalidOperationException("Persistent Quiet has closed all normal native admission.");
        if (kind == ShortRampFrame.Mode0 && !IsClosedHold)
            throw new InvalidDataException("This purpose forbids native mode0.");
        if (Purpose == ShortRampPurpose.NoReductionControl && kind == ShortRampFrame.Mode2)
            throw new InvalidDataException("No-reduction control purpose forbids native mode2.");
        // Cleanup never takes the permit mutex. The shared transaction gate is
        // sufficient, and revocation must remain effective if the parent hangs.
        var normalClose = IsClosedHold && kind == ShortRampFrame.Close &&
            State is ShortRampPermitState.Armed or ShortRampPermitState.Used && _view.ReadInt32(224) >= 2;
        if (kind == ShortRampFrame.Automatic || kind == ShortRampFrame.Close && !normalClose)
        {
            if (IsClosedHold && State == ShortRampPermitState.Unarmed && _view.ReadInt32(56) != 0 &&
                !(kind == ShortRampFrame.Automatic && ReferenceCanInitializeColdBaseline(_view.ReadInt32(304), _view.ReadInt32(236))))
                BeginCleanup(); // A pre-arm auto/close after Open destroys its preparation context.
            if (State is ShortRampPermitState.Armed or ShortRampPermitState.Used) BeginCleanup();
            if (UsesContextualRecovery && State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
                throw new InvalidOperationException("Revoked normal requests cannot enter the contextual cleanup route.");
            if (State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup && OwnedWriter is { } owned && !OwnedWriterExitConfirmed)
            {
                var guardianCleanup = CooperativeWriterRetired && GuardianIdentity?.Pid == Environment.ProcessId;
                var writerTakeover = WriterTookCleanup && owned.Pid == Environment.ProcessId && !guardianAlive;
                if (!guardianCleanup && !writerTakeover)
                    throw new InvalidOperationException("Owned writer exit or cooperative cleanup ownership is unconfirmed.");
            }
            token.ThrowIfCancellationRequested();
            return submit();
        }
        using var held = LockState(0);
        token.ThrowIfCancellationRequested();
        var state = State;
        if (!guardianAlive)
        {
            Revoke(); throw new InvalidOperationException("Independent guardian was lost; short execution revoked.");
        }
        if (state is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
            throw new InvalidOperationException("Short execution was revoked or its guardian is unavailable.");
        var now = _clock.Timestamp;
        if (IsClosedHold && _view.ReadInt32(224) >= 2 && kind != ShortRampFrame.Close)
            throw new InvalidOperationException("Established mode0 hold admits only its one normal close or cleanup.");
        if (IsClosedHold && kind == ShortRampFrame.Mode0 &&
            (state != ShortRampPermitState.Used || NativeMode2Count != 1 || NativeMode0Count != 0 ||
                _view.ReadInt32(236) != 0 || _view.ReadInt64(280) <= _view.ReadInt64(320) || _view.ReadInt32(288) < (IsReference ? ReferenceEntryMinimumCachedRpm : 950) || _view.ReadInt32(288) > 1200))
            throw new InvalidDataException($"Mode0 requires one mode2 and a final validated consumed cached{(IsReference ? ReferenceEntryMinimumCachedRpm : 950)}..1200 reply in the original ramp.");
        if (normalClose && (NativeNormalCloseCount != 0 || _view.ReadInt32(224) != 2))
            throw new InvalidOperationException("Only one normal close is permitted in the established hold.");
        if (state is ShortRampPermitState.Armed or ShortRampPermitState.Used && now >= IndependentStopTimestamp)
        {
            Revoke(); throw new InvalidOperationException("The absolute pulse stop deadline expired.");
        }
        if (kind == ShortRampFrame.Mode2)
        {
            if (state != ShortRampPermitState.Armed || now < _view.ReadInt64(32) || (IsReference ? now > _view.ReadInt64(40) : now >= _view.ReadInt64(40)))
                throw new InvalidOperationException("Mode2 permit is unarmed, used, or expired.");
            _view.Write(4, (int)ShortRampPermitState.Used); _view.Flush();
        }
        else if (kind == ShortRampFrame.Open)
        {
            if (state != ShortRampPermitState.Unarmed || _view.ReadInt32(56) != 0)
                throw new InvalidOperationException("Only one pre-pulse menu open is permitted.");
            _view.Write(56, 1); _view.Flush();
        }
        // Last check after consumption, immediately before beginning native IO.
        if (_revoked.WaitOne(0) || _cleanup.WaitOne(0)) throw new InvalidOperationException("Short execution was revoked before submission.");
        token.ThrowIfCancellationRequested();
        // Parent callbacks and a newly issued RPC cannot renew active time.
        // This runs after the common transaction gate, using the original arm
        // clock and the actual native timeout plus any mandatory quiet interval.
        // Healthy normal native work must release its gate by1200, before G1300.
        var activeDeadline = IsClosedHold
            ? normalClose ? _view.ReadInt64(256) - Ticks(Purpose == ShortRampPurpose.ContextualClosedHold ? ContextualNormalRecoveryLeadMs : 0) : _view.ReadInt64(32) + Ticks(IsReference ? kind == ShortRampFrame.Mode0 ? ReferenceSettledMs : ReferenceLoopDecisionMs : ClosedHoldRampMs)
            : _view.ReadInt64(32) + Ticks(ShortRampSession.RequestedPulseMs);
        if (IsReference) activeDeadline = Math.Min(activeDeadline, OriginalPhaseCap);
        if (state is ShortRampPermitState.Armed or ShortRampPermitState.Used &&
            _clock.Timestamp + Ticks(nativeReserveMs) > activeDeadline)
        {
            Revoke(); throw new TimeoutException("The immutable active deadline lacks the complete native/quiet reserve.");
        }
        return submit();
    }

    internal void ClosedHoldBeforeNative(byte[] report, ShortRampFrame kind)
    {
        if (!IsClosedHold) return;
        if (IsReference) ValidateReferenceFrame(report, report[4] != 3, true);
        var pending = _view.ReadInt32(236);
        if (State == ShortRampPermitState.Unarmed)
        {
            var prepared = _view.ReadInt32(304);
            if (kind == ShortRampFrame.Open && (pending != 0 || (IsReference
                ? !ReferencePreparationMatches(prepared, 63) || !ReferenceInitialCacheAdmissible
                : prepared != 127 || _view.ReadInt32(288) is < 1000 or > 1200)))
                throw new InvalidDataException("One Open requires completed native validation/baseline/automatic1/close preparation.");
            if (kind == ShortRampFrame.Automatic && (prepared & 32) != 0 && !ReferenceCanInitializeColdBaseline(prepared, pending))
                throw new InvalidOperationException("Preparation automatic1 is one-shot.");
            if (kind == ShortRampFrame.Close && ((prepared & 32) == 0 || (prepared & 64) != 0))
                throw new InvalidOperationException("Preparation close must follow automatic1 exactly once before Open.");
            if (IsReference) ReferencePreparationBeforeNative(report, kind, pending, prepared);
        }
        if (kind == ShortRampFrame.Exchange && report[4] == 1 && _view.ReadInt32(304) != 0)
            throw new InvalidOperationException("Only the initial native bridge identity exchange is permitted.");
        if (kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close) && pending != 0 && !(kind == ShortRampFrame.Exchange && report[4] == 4))
            throw new InvalidDataException("A getter reply must be validated and consumed before another native command.");
        if (kind == ShortRampFrame.Getter) _view.Write(236, report[9] == 0x51 ? 16 : report[13]);
        if (kind == ShortRampFrame.Exchange && report[4] == 4 && (pending == 0 || report[5] != (pending == 16 ? 11 : 16)))
            throw new InvalidDataException("Native read has no matching unconsumed getter request.");
        if (kind == ShortRampFrame.Mode2) _view.Write(320, _clock.Timestamp);
        if (kind == ShortRampFrame.Mode0)
        {
            var attempt = _clock.Timestamp;
            if (IsReference && (attempt > _view.ReadInt64(712) + Ticks(40) || attempt > _view.ReadInt64(32) + Ticks(ReferenceMode0AttemptMs)))
                throw new TimeoutException("Reference actual mode0 attempt exceeded immutable dispatch/arm bounds.");
            _view.Write(228, checked(NativeMode0Count + 1)); _view.Write(240, attempt);
        }
        if (IsReference && kind == ShortRampFrame.Mode2)
        {
            var attempt = _clock.Timestamp;
            if (attempt > _view.ReadInt64(40)) throw new TimeoutException("Reference actual mode2 attempt exceeded arm+100ms.");
            _view.Write(744, attempt);
        }
        if (kind == ShortRampFrame.Close && _view.ReadInt32(224) == 2 && State is ShortRampPermitState.Armed or ShortRampPermitState.Used)
        { _view.Write(232, checked(NativeNormalCloseCount + 1)); _view.Write(264, _clock.Timestamp); }
    }
    internal void ClosedHoldNativeReply(byte[] request, byte[] response)
    {
        if (!IsClosedHold || request[4] == 3) return;
        if (response.Length != 65 || response[0] != 0 || response[1] is < 4 or > 64 ||
            response[2] != request[2] || response[3] != 0x39 || response[4] != 0)
            throw new InvalidDataException("Native getter has malformed or foreign bridge reply.");
        var data = response.AsSpan(5, response[1] - 4);
        var bits = _view.ReadInt32(304);
        if (request[4] == 1)
        { if (data.Length == 0) throw new InvalidDataException("Empty bridge identity."); _view.Write(304, bits | 1); return; }
        var pending = _view.ReadInt32(236);
        if (pending == 16)
        {
            byte checksum = 0x50; foreach (var item in data) checksum ^= item;
            if (data.Length != 11 || data[0] != 0x6e || data[1] != 0x88 || data[2] != 2 || data[3] != 0 || data[4] != 0x10 || checksum != 0)
                throw new InvalidDataException("Native brightness sanity reply is invalid.");
            bits |= 2;
        }
        else
        {
            var length = pending == 5 ? 2 : 4;
            if (pending is not (3 or 4 or 5) || data.Length < 4 + length || data.Length > request[5] ||
                data[0] != 0x18 || data[1] != pending || data[2] != length || data[3] != 0)
                throw new InvalidDataException("Native factory getter framing is invalid.");
            // The known bridge can return residual bytes after this meaningful
            // payload (technical handoff, getter framing). Consume the complete
            // response, while interpreting only its validated declared payload.
            if (pending == 3)
            { if (!data.Slice(4, 4).SequenceEqual(new byte[] { 9, 0xc4, 1, 0xf4 })) throw new InvalidDataException("Unexpected native range."); bits |= 4; }
            if (pending == 5)
            { if (!data.Slice(4, 2).SequenceEqual(new byte[] { 4, 0x4c })) throw new InvalidDataException("Unexpected native target."); bits |= 8; }
            if (pending == 4)
            {
                var rpm = data[4] * 256 + data[5];
                _view.Write(288, rpm); _view.Write(280, _clock.Timestamp);
                if (State == ShortRampPermitState.Unarmed && rpm is >= 1000 and <= 1200) bits |= 16;
                if (State is ShortRampPermitState.Armed or ShortRampPermitState.Used && (rpm < (IsReference ? ReferenceEntryMinimumCachedRpm : 950) || rpm > 1200))
                    throw new InvalidDataException($"Native ramp cached RPM outside{(IsReference ? ReferenceEntryMinimumCachedRpm : 950)}..1200; mode0 forbidden.");
                if (IsReference && State == ShortRampPermitState.Used) RecordReferenceSample(rpm);
            }
        }
        _view.Write(304, bits); _view.Write(236, 0);
        if (IsReference && pending == 5) _view.Write(696, _clock.Timestamp);
    }
    internal void ClosedHoldNativeSettled(ShortRampFrame kind, CancellationToken token)
    {
        if (!IsClosedHold) return;
        token.ThrowIfCancellationRequested();
        if (kind == ShortRampFrame.Mode0 || kind == ShortRampFrame.Close && NativeNormalCloseCount > 0 && _view.ReadInt64(272) == 0 &&
            State is ShortRampPermitState.Armed or ShortRampPermitState.Used)
        {
            if (State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
                throw new InvalidOperationException("Revoked native phase cannot establish hold.");
            var now = _clock.Timestamp;
            var deadline = kind == ShortRampFrame.Mode0 ? _view.ReadInt64(32) + Ticks(IsReference ? ReferenceSettledMs : ClosedHoldRampMs) :
                _view.ReadInt64(256) - Ticks(Purpose == ShortRampPurpose.ContextualClosedHold ? ContextualNormalRecoveryLeadMs : 0);
            if (IsReference) deadline = Math.Min(deadline, OriginalPhaseCap);
            if (now > deadline) throw new TimeoutException("Native phase settling exceeded the original phase deadline.");
            if (kind == ShortRampFrame.Mode0)
            {
                _view.Write(248, now);
                _view.Write(256, IsPersistent ? Math.Min(checked(_view.ReadInt64(240) + Ticks(PersistentPreCloseMs)), OriginalPhaseCap) : Math.Min(Math.Min(checked(_view.ReadInt64(240) + Ticks(ClosedHoldDurationMs)),
                    checked(_view.ReadInt64(32) + Ticks(ClosedHoldRampMs + ClosedHoldDurationMs))), OuterRecoveryTimestamp));
                Thread.MemoryBarrier();
                _view.Write(224, 2);
            }
            else
            {
                _view.Write(272, now);
                if (IsPersistent) _view.Write(584, now); // Initial response lease begins only at native Close.
                Thread.MemoryBarrier();
                _view.Write(224, 3);
            }
        }
        if (State == ShortRampPermitState.Unarmed)
        {
            if (kind == ShortRampFrame.Automatic) _view.Write(304, _view.ReadInt32(304) | 32);
            if (IsReference && kind == ShortRampFrame.Automatic && _view.ReadInt32(56) != 0) RecordReferencePreparationAutomaticSettled();
            if (kind == ShortRampFrame.Close) _view.Write(304, _view.ReadInt32(304) | 64);
            if (kind == ShortRampFrame.Open) _view.Write(312, _clock.Timestamp);
        }
    }

    private IDisposable LockState(int milliseconds)
    {
        if (!TryEnter(_stateLock, milliseconds)) throw new TimeoutException("Short permit state is busy; admission refused.");
        return new HeldMutex(_stateLock);
    }
    internal static bool TryEnter(Mutex mutex, int milliseconds)
    {
        try { return mutex.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; }
    }
    private sealed class HeldMutex(Mutex mutex) : IDisposable { public void Dispose() => mutex.ReleaseMutex(); }
    private static byte[] Fingerprint(string nonce, MenuDeviceIdentity identity, ShortRampPurpose purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(nonce + ":" + identity + ":" + ShortRampPurposePolicy.Token(purpose) +
            (purpose == ShortRampPurpose.ReferencePersistentQuiet ? ":" + ReferenceContract : "")));
    public void Dispose() { _view.Dispose(); _map.Dispose(); _revoked.Dispose(); _cleanup.Dispose(); _writerExited.Dispose(); _boundaryUnknown.Dispose(); _stateLock.Dispose(); }
}
#pragma warning restore CA1416

internal enum ShortRampFrame { Exchange, Getter, Automatic, Open, Close, Mode2, Mode0 }

// Shared by the native transport and the offline/process transport. This owns
// the transaction mutex; admission can never accidentally run before it.
public sealed class ShortRampExecutionGate : IDisposable
{
    public const int NativeIoTimeoutMs = 100;
    public const int TransactionWaitMs = 150;
    public const int NativeQuietGapMs = 180;
    private readonly ShortRampPermit _permit;
    private readonly MenuDeviceIdentity _identity;
    private readonly string _nonce;
    private readonly Mutex _transaction;
    private readonly Func<bool> _guardianAlive;
    private readonly Action<int> _nativeDelay;
    private readonly bool _cleanupOnly;
    private readonly int _nativeIoTimeoutMs;
    private readonly IShortRampQpcClock _nativeClock;
    public ShortRampExecutionGate(ShortRampPermit permit, MenuDeviceIdentity identity, string nonce,
        Func<bool>? guardianAlive = null, Action<int>? nativeDelay = null, bool cleanupOnly = false,
        int nativeIoTimeoutMs = NativeIoTimeoutMs, IShortRampQpcClock? nativeClock = null, ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (permit.Purpose != purpose) throw new InvalidDataException("Foreign short transport purpose.");
        if (permit.Identity != identity || permit.Nonce != nonce) throw new InvalidDataException("Foreign short transport identity or nonce.");
        _permit = permit; _identity = identity; _nonce = nonce;
        _transaction = new Mutex(false, permit.TransactionName);
        _guardianAlive = guardianAlive ?? permit.GuardianAlive;
        _nativeDelay = nativeDelay ?? Thread.Sleep;
        _cleanupOnly = cleanupOnly;
        if (nativeIoTimeoutMs is <= 0 or > NativeIoTimeoutMs) throw new ArgumentOutOfRangeException(nameof(nativeIoTimeoutMs));
        _nativeIoTimeoutMs = nativeIoTimeoutMs;
        _nativeClock = nativeClock ?? new ShortRampSystemQpcClock();
        if (_nativeClock.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(nativeClock));
    }
    public static void ValidateForQueue(byte[] report, bool reply, ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ShortRampPurposePolicy.Validate(purpose);
        var kind = Classify(report, reply);
        if (purpose == ShortRampPurpose.NoReductionControl && kind == ShortRampFrame.Mode2)
            throw new InvalidDataException("No-reduction control purpose forbids mode2.");
        if (purpose is not (ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold or ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet) && kind == ShortRampFrame.Mode0)
            throw new InvalidDataException("This purpose forbids mode0.");
    }
    public byte[] Execute(byte[] report, bool reply, CancellationToken token, Func<CancellationToken, byte[]> nativeIo,
        ContextualRecoveryAuthorization? authorization = null)
    {
        var admittedBytes = (byte[])report.Clone();
        ShortRampFrame kind;
        try
        {
            kind = Classify(report, reply);
            if (_cleanupOnly && authorization is null && kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close))
                throw new InvalidDataException("Guardian transport permits only automatic1 and close.");
        }
        catch { if (_permit.IsClosedHold) _permit.Revoke(); throw; }
        var active = _permit.State is ShortRampPermitState.Armed or ShortRampPermitState.Used;
        // Zero-wait cleanup is valid only after the exact original writer has
        // exited. A busy remaining gate is a refusal, never a bypass.
        var ownedCleanup = _permit.State == ShortRampPermitState.Cleanup && _permit.OwnedWriter is not null;
        var wait = ownedCleanup || active && kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close) ? 0 : TransactionWaitMs;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!ShortRampPermit.TryEnter(_transaction, wait)) throw new TimeoutException("Short transaction gate is busy; admission refused.");
        }
        catch { if (_permit.IsClosedHold) _permit.Revoke(); throw; }
        var possiblySubmitted = false;
        var nativeSucceeded = false;
        var contextualAdmitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var nativeReserve = _nativeIoTimeoutMs + (report[4] == 3 ? NativeQuietGapMs : 0);
            return _permit.Admit(kind, _nonce, _identity, _guardianAlive(), nativeReserve, token, () =>
            {
                // Inspect the bytes again after acquiring both gates, rather
                // than trusting validation of a caller-owned queued buffer.
                ValidateForQueue(report, reply, _permit.Purpose);
                if (!admittedBytes.SequenceEqual(report) || Classify(report, reply) != kind)
                    throw new InvalidDataException("Short payload changed before native execution.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_nativeIoTimeoutMs);
                possiblySubmitted = report[4] == 3;
                // Cooperative calls also get an elapsed deadline. The owned
                // process variant lets the guardian terminate an uncooperative
                // writer rather than leaving native IO in the PCM observer.
                var began = _nativeClock.Timestamp;
                if (authorization is not null)
                {
                    possiblySubmitted = false;
                    _permit.ContextualBeforeNative(report, reply, authorization);
                    contextualAdmitted = true;
                    possiblySubmitted = report[4] == 3;
                }
                else _permit.ClosedHoldBeforeNative(report, kind);
                if (_permit.IsReference) _permit.ReferenceBeforeNative(report);
                if (kind == ShortRampFrame.Mode2) _permit.RecordMode2NativeAttempt();
                _permit.ThrowIfReferenceReleased();
                var result = nativeIo(timeout.Token);
                if (_permit.IsReference && kind == ShortRampFrame.Mode0) _permit.RecordReferenceMode0WriteComplete();
                if ((_nativeClock.Timestamp - began) * 1000d / _nativeClock.Frequency > _nativeIoTimeoutMs)
                {
                    _permit.Revoke();
                    throw new TimeoutException("Native IO returned after its absolute elapsed deadline; submission is unconfirmed.");
                }
                if (_permit.IsClosedHold) timeout.Token.ThrowIfCancellationRequested();
                if (authorization is not null) _permit.ContextualNativeReply(report, result);
                else _permit.ClosedHoldNativeReply(report, result);
                nativeSucceeded = true;
                return result;
            }, report, reply, authorization);
        }
        catch
        {
            if (_permit.IsClosedHold) _permit.Revoke();
            throw;
        }
        finally
        {
            try
            {
                var quietSucceeded = !possiblySubmitted;
                try { if (possiblySubmitted) { WaitForNativeQuiet(); quietSucceeded = true; } }
                finally
                {
                    if (contextualAdmitted)
                        _permit.ContextualNativeFinished(report, kind, nativeSucceeded && quietSucceeded);
                }
                if (nativeSucceeded && kind == ShortRampFrame.Automatic) _permit.RecordAutomaticNativeSettled();
                if (nativeSucceeded && authorization is null) _permit.ClosedHoldNativeSettled(kind, token);
            }
            catch { if (_permit.IsClosedHold) _permit.Revoke(); throw; }
            finally { _transaction.ReleaseMutex(); }
        }
    }

    private void WaitForNativeQuiet()
    {
        if (!_permit.IsClosedHold)
        {
            _nativeDelay(NativeQuietGapMs);
            return;
        }
        // Sleep may return before the requested duration after OS tick rounding.
        // Keep one original native-clock horizon across all uncancelled waits.
        var quiet = new ShortRampNativeQuietHorizon(_nativeClock, NativeQuietGapMs,
            "Native quiet clock did not advance; settling is unconfirmed.");
        while (!quiet.Elapsed)
        {
            var remainingMs = quiet.RemainingMilliseconds;
            try { _nativeDelay(remainingMs); }
            catch (Exception ex) { quiet.RecordWaitFailure(ex); }
            quiet.ObserveProgress(_nativeClock.Timestamp);
        }
        quiet.ThrowWaitFailure();
    }

    internal static ShortRampFrame Classify(byte[] report, bool reply)
    {
        if (report.Length != 65 || report[0] != 0 || report[2] == 0 || report[3] != 0x39 ||
            report[6] != 0 || report[7] != 0x0b || report[8] != 0x37)
            throw new InvalidDataException("Invalid short bridge frame.");
        var length = report[1] - 8;
        if (length is < 0 or > 56 || report.AsSpan(9 + length).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid short frame length or padding.");
        if (report[4] == 1 && report[5] == 0 && length == 0 && reply) return ShortRampFrame.Exchange;
        if (report[4] == 4 && report[5] is 11 or 16 && length == 0 && reply) return ShortRampFrame.Exchange;
        if (report[4] != 3 || report[5] != length || reply) throw new InvalidDataException("Short bridge command is not allowlisted.");
        var hex = Convert.ToHexString(report.AsSpan(9, length));
        return hex switch
        {
            "4F8403180601B9" => ShortRampFrame.Automatic,
            "4F850302105B00EE" => ShortRampFrame.Close,
            "4F850302A800000D" => ShortRampFrame.Open,
            "4F8403180602BA" => ShortRampFrame.Mode2,
            "4F8403180600B8" => ShortRampFrame.Mode0,
            "51820110AC" or "4F83031803BA" or "4F83031804BD" or "4F83031805BC" => ShortRampFrame.Getter,
            _ => throw new InvalidDataException("Short DDC payload is not allowlisted.")
        };
    }
    public void Dispose() => _transaction.Dispose();
}
