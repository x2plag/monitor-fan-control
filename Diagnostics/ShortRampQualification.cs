using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

// This qualifies one immutable software/fault model. It cannot supply a human
// observation or claim delivery when the cleanup driver itself is unresponsive.
public sealed class ShortRampSoftwareProfile
{
    public string Id { get; }
    public bool SoftwareQualified { get; }
    public ShortRampPurpose Purpose { get; }
    private ShortRampSoftwareProfile(string id, bool qualified, ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    { Id = id; SoftwareQualified = qualified; Purpose = purpose; }
    public static ShortRampSoftwareProfile Current { get; } = new("owned1300-active1200-native60-rpc280-drain280-v2", true);
    public static ShortRampSoftwareProfile NoReductionControl { get; } = new(
        "owned1300-active1200-control384-native60-rpc280-drain280-v1", true, ShortRampPurpose.NoReductionControl);
    public static ShortRampSoftwareProfile BoundedClosedHold { get; } = new(
        "owned-phase2400-holdmax10000-ready16000-outer22000-native60-v1", true, ShortRampPurpose.BoundedClosedHold);
    public static ShortRampSoftwareProfile ContextualClosedHold { get; } = new(
        "owned-context7-cache20000-phase2400-hold10000-lead2620-ready16000-outer75000-reserve51000-v1", true, ShortRampPurpose.ContextualClosedHold);
    public static ShortRampSoftwareProfile Unqualified { get; } = new("unqualified", false);
    public static ShortRampSoftwareProfile PersistentQuiet { get; } = new(
        "owned-persistent-phase2400-preclose1000-health2000-stop55000-context7-cache20000-v1", true, ShortRampPurpose.PersistentQuiet);
    // Purpose5 admission is qualified only for the explicitly requested healthy
    // listening path. It does not qualify successful restoration after faults.
    public static ShortRampSoftwareProfile ReferencePersistentQuiet { get; } = new(
        "reference-loop10000-freeze10280-preclose1000-entry810-health2000-stop55000-context7-cache20000-release-coldcache0-prep3-contextauto1-v6", true, ShortRampPurpose.ReferencePersistentQuiet);
    public static ShortRampSoftwareProfile ForPurpose(ShortRampPurpose purpose) => purpose switch
    {
        ShortRampPurpose.Mode2 => Current,
        ShortRampPurpose.NoReductionControl => NoReductionControl,
        ShortRampPurpose.BoundedClosedHold => BoundedClosedHold,
        ShortRampPurpose.ContextualClosedHold => ContextualClosedHold,
        ShortRampPurpose.PersistentQuiet => PersistentQuiet,
        ShortRampPurpose.ReferencePersistentQuiet => ReferencePersistentQuiet,
        _ => throw new InvalidDataException("Unknown short-ramp software purpose.")
    };
    public string FaultModel => Purpose == ShortRampPurpose.ReferencePersistentQuiet
        ? "Limited healthy-listening software admission: one bounded original-loop activation, settled mode0/Close, then healthy until-off hold. No acoustic, fresh RPM, temperature or thermal-protection qualification. Restoration machinery remains enabled with the original immutable first-stop+55s bound and visible Faulted result. The current no-HID w-hang-mode0 fault fails contextual GET4/cache restoration acceptance; full Auto and fault restoration are not qualified. No scheduled Auto is part of the listening request."
        : Purpose == ShortRampPurpose.PersistentQuiet
        ? "Experimental until-explicit-Auto/exit Quiet after one native mode0/Close. No fresh RPM/temperature or thermal protection. Authenticated250ms health RPCs,2s lease, independent25ms observer; bounded original activation and immutable first-stop+55s full contextual recovery/finalization. Single P/W fault requires healthy G, single G death/stall requires healthy prepared W/P; native progress stall detection2s plus25ms observer and300ms reap leaves a conditional52365ms full replay within55s. USB/native-driver/compound faults, cache failures or unconfirmed exact reap stay Faulted. Conditional software qualification is separate from physical acoustic effect and Auto acceptance."
        : Purpose == ShortRampPurpose.ContextualClosedHold
        ? "Conditional single hung-W/healthy-G or sole-G-death/full-prepared-W replay. Original ready+75s outer,51s recovery reserve,20s cached observation per attempt; native ramp2.4s/hold<=10s/ready+16s phase cap unchanged. Contextual native success2620ms(hungW) or4460ms(soleG);2620ms normal lead leaves an explicit1840ms soleG fault tail. Dispatch<=200/330ms, responsive native IO and finalization<=900/440ms required. Compound faults, failed lease/reap/native/cache progress and expired original deadlines remain failures. Native success and cached1050..1150 are not physical Auto, fresh tachometry or thermal protection."
        : Purpose == ShortRampPurpose.BoundedClosedHold
        ? "Single hung normal writer with healthy guardian, or sole guardian death with healthy prepared writer and independent parent recovery observer. Original guardian-ready22s outer envelope requires responsive cleanup IO, aggregate dispatch<=200/330ms and finalization<=900/440ms respectively. Normal ramp2400ms/hold10000ms are native admission limits, not unconditional physical guarantees. Compound faults, failed native-success progress, cleanup-native failure, failed reap, expired outer envelope and ambiguous leases are explicit failures."
        : "Single hung normal writer with healthy guardian, or sole guardian death with healthy prepared writer and parent recovery observer; responsive cleanup IO and aggregate dispatch allowance required. Compound faults, cleanup-native failure, failed reap and ambiguous leases are explicit failures.";
}

public sealed record ShortRampTimingEnvelope(int ActivePulseAndFloorMs, int HungWriterMs,
    int SoleGuardianMs, int HungDispatchAllowanceMs, int GuardianDispatchAllowanceMs, bool WithinThreeSeconds);
public sealed record ShortRampControlTimingEnvelope(int PassiveOffsetMs, int MaximumArmAcknowledgmentMs,
    int FloorReadBudgetMs, int SlotDispatchAllowanceMs, int ActiveThroughFloorMs,
    int RemainingBeforeRequestedEndMs, bool WithinRequestedWindow);

public sealed record ClosedHoldTimingEnvelope(int NormalRampNativeMs, int HoldFromNativeAttemptMs,
    int EarlyRampRecoveryMs, int OuterFromGuardianReadyMs, int MaximumPreArmMs,
    int FullRecoveryReserveMs,
    int HungWriterFirstAutomaticTailMs, int SoleGuardianFirstAutomaticTailMs,
    int HungWriterCompleteRecoveryMs, int SoleGuardianCompleteRecoveryMs,
    int HungDispatchAllowanceMs, int GuardianDispatchAllowanceMs,
    int HealthyGuardianFinalizationAllowanceMs, int SoleGuardianFinalizationAllowanceMs,
    bool WithinOriginalOuterEnvelope);

public static class ClosedHoldTimingQualification
{
    // These are conditional recovery envelopes, separate from normal native
    // admission. The finalization allowances include responsive disposal,
    // lifecycle output and exact process-exit notification after AACC/reap.
    public static ClosedHoldTimingEnvelope Evaluate(int hungDispatchMs = 200, int guardianDispatchMs = 330,
        int healthyGuardianFinalizationMs = 900, int soleGuardianFinalizationMs = 440)
    {
        if (hungDispatchMs < 0 || guardianDispatchMs < 0 || healthyGuardianFinalizationMs < 0 || soleGuardianFinalizationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(hungDispatchMs));
        var hungFirst = 280 + 300 + 180 + 460 + 280 + hungDispatchMs;
        var soleFirst = 180 + 280 + 450 + 180 + 280 + guardianDispatchMs;
        var hungComplete = 280 + 300 + 180 + 4 * 460 + 300 + hungDispatchMs + healthyGuardianFinalizationMs;
        // G may die during the last close, after spending its orphan180+AACC.
        // Earlier successful auto progress bounds physical recovery but does
        // not justify dropping any of the prepared peer's cleanup actions.
        var soleComplete = 180 + 4 * 460 + 450 + 180 + 4 * 460 + 300 + guardianDispatchMs + soleGuardianFinalizationMs;
        var preArm = ShortRampPermit.ClosedHoldOuterMs - ShortRampPermit.ClosedHoldRampMs -
            ShortRampPermit.ClosedHoldDurationMs - ShortRampPermit.ClosedHoldRecoveryReserveMs;
        return new(ShortRampPermit.ClosedHoldRampMs, ShortRampPermit.ClosedHoldDurationMs,
            ShortRampPermit.ClosedHoldRampRecoveryMs, ShortRampPermit.ClosedHoldOuterMs, preArm,
            ShortRampPermit.ClosedHoldFullRecoveryReserveMs,
            hungFirst, soleFirst, hungComplete, soleComplete, hungDispatchMs, guardianDispatchMs,
            healthyGuardianFinalizationMs, soleGuardianFinalizationMs,
            ShortRampPermit.ClosedHoldRampMs == 2400 && ShortRampPermit.ClosedHoldDurationMs == 10000 &&
            ShortRampPermit.ClosedHoldRampRecoveryMs == 2500 && ShortRampPermit.ClosedHoldOuterMs == 22000 &&
            ShortRampPermit.ClosedHoldRecoveryReserveMs == 4000 && ShortRampPermit.ClosedHoldFullRecoveryReserveMs == 6000 && preArm == 5600 &&
            ShortRampOwnedWriterProcess.NativeIoTimeoutMs == 60 && ShortRampOwnedWriterProcess.ActiveWriteRpcMs == 280 &&
            ShortRampOwnedWriterProcess.ActiveReadRpcMs == 100 && ShortRampOwnedWriterProcess.CooperativeDrainMs == 280 &&
            ShortRampOwnedWriterProcess.TerminateAndReapMs == 300 && ShortRampExecutionGate.NativeIoTimeoutMs == 100 &&
            ShortRampExecutionGate.NativeQuietGapMs == 180 && ShortRampSession.BoundedFloorRpm == 950 &&
            ShortRampSession.ClosedHoldRampMs == 2400 && ShortRampSession.ClosedHoldMaximumMs == 10000 &&
            hungDispatchMs <= 200 && guardianDispatchMs <= 330 &&
            healthyGuardianFinalizationMs <= 900 && soleGuardianFinalizationMs <= 440 &&
            hungFirst <= 1700 && soleFirst <= 1700 && hungComplete <= 6000 && soleComplete <= 6000);
    }
}

public static class ShortRampTimingQualification
{
    public static ShortRampControlTimingEnvelope EvaluateControl(int armAcknowledgmentMs = 100, int slotDispatchMs = 0)
    {
        if (armAcknowledgmentMs < 0 || slotDispatchMs < 0) throw new ArgumentOutOfRangeException(nameof(armAcknowledgmentMs));
        // The arm acknowledgment is inside the absolute384ms slot, never
        // added to it. Floor GET includes its native quiet and recorded gap.
        var floor = ShortRampOwnedWriterProcess.ActiveWriteRpcMs + 180 + ShortRampOwnedWriterProcess.ActiveReadRpcMs;
        var active = checked(ShortRampSession.ControlPassiveOffsetMs + slotDispatchMs + floor);
        var slack = ShortRampSession.RequestedPulseMs - active;
        return new(ShortRampSession.ControlPassiveOffsetMs, armAcknowledgmentMs, floor, slotDispatchMs,
            active, slack, armAcknowledgmentMs <= 100 && floor == 560 &&
            ShortRampSession.ControlPassiveOffsetMs == 384 && ShortRampSession.ControlSlotAndFloorBudgetMs == 944 && slack >= 0);
    }

    // Dispatch includes timer rounding/wake, gate release/recheck, exact exit
    // notification and continuations. Existing RPC allowances are counted once.
    public static ShortRampTimingEnvelope Evaluate(int hungDispatchMs = 200, int guardianDispatchMs = 330)
    {
        if (hungDispatchMs < 0 || guardianDispatchMs < 0) throw new ArgumentOutOfRangeException(nameof(hungDispatchMs));
        var active = 100 + ShortRampOwnedWriterProcess.ActiveWriteRpcMs + 180 +
            ShortRampOwnedWriterProcess.ActiveWriteRpcMs + 180 + ShortRampOwnedWriterProcess.ActiveReadRpcMs;
        var hung = ShortRampOwnedWriterProcess.StopDeadlineMs + ShortRampOwnedWriterProcess.CooperativeDrainMs + ShortRampOwnedWriterProcess.TerminateAndReapMs +
            180 + (100 + 180 + 180) + (100 + 180) + hungDispatchMs;
        // Sole G failure does not also imply an independently failed W auto.
        // Native admission independently reserves all normal native/quiet work
        // before immutable arm+1200, including after blocked parent callbacks.
        // Thus W is idle at G1300 even when the nominal1120 path was delayed;
        // before the first successful mark G's latest unsettled phase is1760.
        var guardian = ShortRampOwnedWriterProcess.StopDeadlineMs + 180 + 100 + 180 +
            450 + 180 + ShortRampOwnedWriterProcess.ActiveWriteRpcMs + guardianDispatchMs;
        return new(active, hung, guardian, hungDispatchMs, guardianDispatchMs,
            ShortRampOwnedWriterProcess.NativeIoTimeoutMs == 60 && ShortRampOwnedWriterProcess.StopDeadlineMs == 1300 &&
            ShortRampOwnedWriterProcess.CooperativeDrainMs == 280 && ShortRampSession.RequestedPulseMs == 1200 &&
            ShortRampSession.BoundedFloorRpm == 950 && active <= ShortRampSession.PulseAndFloorBudgetMs && hung <= 3000 && guardian <= 3000);
    }
}

public sealed record ShortRampQualifiedResult(string SoftwareProfile, bool SoftwareQualified,
    bool PhysicalAdmissionAccepted, ShortRampAudioResult? Audio, string Detail)
{
    // Compatibility name: this records admission acceptance, including a
    // standing assumption. It never certifies a current physical state.
    public bool OsdAdmissionAccepted => PhysicalAdmissionAccepted;
    public ShortRampOsdAdmissionMetadata? OsdAdmission { get; init; }
    public ShortRampPurpose Purpose { get; init; } = ShortRampPurpose.Mode2;
    public bool ControlClockArmed => Audio?.Session.ControlClockArmed ?? false;
    public bool Mode2Requested => Audio?.Session.Mode2Requested ?? false;
    public int? NativeMode2Count => Audio?.Session.NativeMode2Count;
    public int? NativeMode0Count => Audio?.Session.NativeMode0Count;
    public int? NativeNormalCloseCount => Audio?.Session.NativeNormalCloseCount;
    public ShortRampNativePhaseEvidence? NativePhaseEvidence => Audio?.Session.NativePhaseEvidence;
    public bool PhysicalClosureAfterCleanupConfirmed => false;
    public bool PermanentQuietEstablished => false;
}

public static class ShortRampQualifiedSession
{
    public static async Task<ShortRampQualifiedResult> RunFromObservationFileAsync(ShortRampSoftwareProfile profile,
        string observationPath, Func<CancellationToken, Task<IShortRampAudioCapture>> startCapture,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock, string evidenceDirectory,
        Action<MenuLogEntry> log, CancellationToken token = default)
    {
        var admission = profile.SoftwareQualified ? await ShortRampOsdAdmissionFile.ReadAsync(observationPath, token) : null;
        return await RunWithAdmissionAsync(profile, admission, startCapture, startWorker, clock, evidenceDirectory, log, token);
    }

    public static Task<ShortRampQualifiedResult> RunAsync(ShortRampSoftwareProfile profile,
        HumanOsdClosureObservation? observation, Func<CancellationToken, Task<IShortRampAudioCapture>> startCapture,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock, string evidenceDirectory,
        Action<MenuLogEntry> log, CancellationToken token = default)
        => RunWithAdmissionAsync(profile, ShortRampOsdAdmission.FromObservation(observation), startCapture,
            startWorker, clock, evidenceDirectory, log, token);

    public static async Task<ShortRampQualifiedResult> RunWithAdmissionAsync(ShortRampSoftwareProfile profile,
        ShortRampOsdAdmission? admission, Func<CancellationToken, Task<IShortRampAudioCapture>> startCapture,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock, string evidenceDirectory,
        Action<MenuLogEntry> log, CancellationToken token = default)
    {
        if (profile.Purpose is ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet || !profile.SoftwareQualified ||
            (profile.Purpose == ShortRampPurpose.ContextualClosedHold
                ? !ContextualHoldTimingQualification.Evaluate().WithinOriginalOuterEnvelope
                : profile.Purpose == ShortRampPurpose.BoundedClosedHold ? !ClosedHoldTimingQualification.Evaluate().WithinOriginalOuterEnvelope
                : !ShortRampTimingQualification.Evaluate().WithinThreeSeconds) ||
            profile.Purpose == ShortRampPurpose.NoReductionControl && !ShortRampTimingQualification.EvaluateControl().WithinRequestedWindow)
            return new(profile.Id, false, false, null, "The exact software profile is not qualified; no capture/worker/native factory started.") { Purpose = profile.Purpose };
        if (admission is not { IsValid: true })
            return new(profile.Id, true, false, null, "A valid explicit OSD admission is missing; no capture/worker/native factory started.") { Purpose = profile.Purpose };
        var audio = await ShortRampAudioSession.RunWithAdmissionAsync(admission, startCapture, startWorker, clock,
            evidenceDirectory, log, token,
            profile.Purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold
                ? MenuAudioReferencePolicy.StrictBroadbandAndTones : MenuAudioReferencePolicy.FrozenStrongTonalReference,
            preCaptureSettleMs: 10000, purpose: profile.Purpose,
            audioPolicy: profile.Purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold
                ? ShortRampAudioPolicy.DescriptiveClosedHold : ShortRampAudioPolicy.ExistingReference);
        return new(profile.Id, true, true, audio, "Software qualification, OSD admission basis, cleanup delivery, subsequent physical closure and permanent quiet are separate.")
            { Purpose = profile.Purpose, OsdAdmission = admission.Metadata };
    }
}
