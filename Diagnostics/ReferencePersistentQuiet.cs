using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MonitorFanControl;

public sealed record ReferenceLoopSample(int Sequence, int CachedRpm, long Qpc);
public sealed record ReferenceActivationReceipt(string Contract, string Profile, string Nonce,
    ShortRampOwnedProcessIdentity Parent, ShortRampOwnedProcessIdentity? Writer, ShortRampOwnedProcessIdentity? Guardian,
    ReferenceLoopSample[] Samples, string Reason, long DecisionQpc, long ConsumedSampleQpc, int ConsumedCachedRpm,
    long Mode2AttemptQpc, long Mode0WriteCompletedQpc, ShortRampNativePhaseEvidence Phase,
    int NativeMode2Count, int NativeMode0Count, int NativeNormalCloseCount)
{
    public double ConsumedSampleAgeMilliseconds => (DecisionQpc - ConsumedSampleQpc) * 1000d / Phase.QpcFrequency;
}

public sealed record ReferenceTimingEnvelope(int IndependentActivationStopMs, int HealthyGuardianFirstAutoSettledTailMs,
    int HealthyGuardianContextualAutoTailMs, int SoleGuardianContextualAutoTailMs, int StalledGuardianContextualAutoTailMs,
    int CompleteRecoveryFromFirstStopMs, int MaximumActivationToCompleteMs, int FirstStopBudgetMs);
public static class ReferenceTimingQualification
{
    public static ReferenceTimingEnvelope Evaluate()
    {
        var contextual = ContextualHoldTimingQualification.Evaluate();
        // First Auto counts native settling, before MenuProtocol's additional
        // recorded180ms. Contextual Auto and full replay keep double gaps.
        var firstAuto = 280 + 300 + 180 + 280 + 200;
        var stall = 2000 + 25 + 300;
        var complete = contextual.SoleGuardianCompleteRecoveryMs + stall;
        return new(ShortRampPermit.ReferenceIndependentStopMs, firstAuto, contextual.FirstContextualHungWriterMs, contextual.FirstContextualSoleGuardianMs,
            contextual.FirstContextualSoleGuardianMs + stall, complete, ShortRampPermit.ReferenceIndependentStopMs + complete, 55000);
    }
}

public sealed partial class ShortRampPermit
{
    public const int ReferenceLoopDecisionMs = 10000, ReferenceMode0AttemptMs = 10040,
        ReferenceWriteCompleteMs = 10100, ReferenceSettledMs = 10280, ReferenceIndependentStopMs = 10380;
    internal const int ReferenceMapBytes = 2048, ReferenceSampleCapacity = 64;
    private const int ReferenceReplyBitmapOffset = 1792, ReferenceParentHashOffset = 1824;
    private const int ReferenceBaselineReadsOffset = 1936;
    private const int ReferencePreparationAutomaticOffset = 1940, ReferencePreparationAutomaticSettledOffset = 1944;
    // Observed cached input admission for experimental healthy listening only;
    // this is neither a fresh-speed measurement nor a thermal protection bound.
    public const int ReferenceEntryMinimumCachedRpm = 810;
    public const string ReferenceContract = "reference-abi6-coldcache0-prep3-contextauto1-release-loop10000-get380-age200-dispatch40-attempt10040-write10100-settle10280-stop10380-entry810-profile6";
    public bool IsReference => Purpose == ShortRampPurpose.ReferencePersistentQuiet;
    private static byte[] ReferenceProfileHash => SHA256.HashData(Encoding.UTF8.GetBytes(ShortRampSoftwareProfile.ReferencePersistentQuiet.Id));
    private static byte[] ReferenceContractHash => SHA256.HashData(Encoding.UTF8.GetBytes(ReferenceContract));
    internal void InitializeReferenceBinding()
    {
        _view.Write(608, 0x52465136); _view.Write(612, 6); _view.Write(616, Environment.ProcessId);
        _view.Write(624, CurrentProcessStartUtcTicks);
        _view.WriteArray(632, ReferenceProfileHash, 0, 32); _view.WriteArray(664, ReferenceContractHash, 0, 32);
        var parent = ReferenceParentHash(); _view.WriteArray(ReferenceParentHashOffset, parent, 0, 32);
    }
    private byte[] ReferenceParentHash() => SHA256.HashData(Encoding.UTF8.GetBytes(Nonce + ":" + Identity + ":" +
        ReferenceContract + ":" + _view.ReadInt32(616) + ":" + _view.ReadInt64(624)));
    internal void ValidateReferenceBinding(bool native)
    {
        var profile = new byte[32]; var contract = new byte[32];
        _view.ReadArray(632, profile, 0, 32); _view.ReadArray(664, contract, 0, 32);
        var parent = new byte[32]; _view.ReadArray(ReferenceParentHashOffset, parent, 0, 32);
        if (_view.ReadInt32(608) != 0x52465136 || _view.ReadInt32(612) != 6 ||
            !profile.SequenceEqual(ReferenceProfileHash) || !contract.SequenceEqual(ReferenceContractHash) ||
            _view.ReadInt32(616) <= 0 || _view.ReadInt64(624) <= 0 || !parent.SequenceEqual(ReferenceParentHash()))
            throw new InvalidDataException("Reference ABI/profile/contract/parent binding differs.");
        if (native && OwnedWriter is { } owner && State is not (ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup) &&
            (owner.Pid != Environment.ProcessId || owner.StartUtcTicks != CurrentProcessStartUtcTicks))
            throw new InvalidDataException("Only the exact immutable writer may submit reference activation.");
    }
    internal void ValidateReferenceParent(Process parent)
    {
        ValidateReferenceBinding(false);
        if (parent.Id != _view.ReadInt32(616) || parent.StartTime.ToUniversalTime().Ticks != _view.ReadInt64(624) || parent.HasExited)
            throw new InvalidDataException("Reference guardian retained a foreign parent PID/start identity.");
    }
    internal void ValidateReferenceFrame(byte[] report, bool reply, bool native)
    {
        ValidateReferenceBinding(native);
        if (State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup) return;
        var kind = ShortRampExecutionGate.Classify(report, reply);
        var now = _clock.Timestamp;
        if (State is ShortRampPermitState.Armed or ShortRampPermitState.Used)
        {
            if (kind == ShortRampFrame.Mode2 && _view.ReadInt32(704) != 1)
                throw new InvalidDataException("Only the authenticated worker reference operation may activate mode2.");
            if (kind == ShortRampFrame.Getter && (report[13] != 4 || _view.ReadInt64(712) != 0 ||
                _view.ReadInt32(760) > 0 && _view.ReadInt32(288) < 950 ||
                now + Ticks(380) > Math.Min(_view.ReadInt64(32) + Ticks(ReferenceLoopDecisionMs), OriginalPhaseCap)))
                throw new InvalidDataException("Reference GET lacks original complete380ms reserve or follows a terminal input.");
            if (kind == ShortRampFrame.Mode0)
            {
                var decision = _view.ReadInt64(712); var sample = _view.ReadInt64(720);
                if (decision <= 0 || decision > _view.ReadInt64(32) + Ticks(ReferenceLoopDecisionMs) || now < decision || now > decision + Ticks(40) ||
                    now > _view.ReadInt64(32) + Ticks(ReferenceMode0AttemptMs) || sample <= _view.ReadInt64(320) || decision - sample > Ticks(200) ||
                    sample != _view.ReadInt64(280) || _view.ReadInt32(728) != _view.ReadInt32(288) ||
                    _view.ReadInt32(736) != _view.ReadInt32(760) || _view.ReadInt32(236) != 0)
                    throw new InvalidDataException("Reference mode0 lacks its immutable worker terminal decision/sample/dispatch bound.");
            }
        }
        if (report[4] is 1 or 4 && (_view.ReadByte(ReferenceReplyBitmapOffset + report[2] / 8) & (1 << (report[2] % 8))) != 0)
            throw new InvalidDataException("Reference native reply token has already been consumed.");
    }
    internal void ReferencePreparationBeforeNative(byte[] report, ShortRampFrame kind, int pending, int prepared)
    {
        if (kind == ShortRampFrame.Close) throw new InvalidDataException("Reference preparation has no initial Close.");
        if (kind == ShortRampFrame.Automatic && !ReferencePreparationMatches(prepared, 31) && !ReferenceCanInitializeColdBaseline(prepared, pending))
            throw new InvalidDataException("Reference validation order is incomplete.");
        if (kind == ShortRampFrame.Getter)
        {
            var expected = report[9] == 0x51 ? 1 : report[13] switch { 3 => 3, 4 => _view.ReadInt32(56) == 0 ? 7 : 63, 5 => 23, _ => -1 };
            if (!ReferencePreparationMatches(prepared, expected) || pending != 0 || report[13] == 4 && _view.ReadInt32(56) != 0 &&
                _clock.Timestamp < ReferenceBaselineEpoch + Ticks(700))
                throw new InvalidDataException("Reference requires identity/brightness/GET3/GET4/GET5 then Auto/Open/700ms/baseline.");
            if (report[13] == 4 && _view.ReadInt32(56) != 0 &&
                (_view.ReadInt32(ReferenceBaselineReadsOffset) >= 3 || _clock.Timestamp + Ticks(380) > _view.ReadInt64(696) + Ticks(2000)))
                throw new TimeoutException("Reference baseline observation exhausted its three-read/original target+2s preparation budget.");
        }
    }
    internal bool ReferencePreparationMatches(int prepared, int expected) => prepared == expected ||
        IsReference && (expected & 16) != 0 && prepared == (expected & ~16) && ReferenceInitialCacheAdmissible;
    internal bool ReferenceInitialCacheAdmissible => _view.ReadInt32(288) is 0 or (>= 500 and <= 2500);
    internal long ReferenceBaselineEpoch => Math.Max(_view.ReadInt64(312), _view.ReadInt64(ReferencePreparationAutomaticSettledOffset));
    internal bool ReferenceCanInitializeColdBaseline(int prepared, int pending) => IsReference && State == ShortRampPermitState.Unarmed &&
        ReferencePreparationMatches(prepared, 63) && pending == 0 && _view.ReadInt32(56) == 1 &&
        ReferenceInitialCacheAdmissible && _view.ReadInt32(288) is not (>= 1000 and <= 1200) &&
        _view.ReadInt32(ReferenceBaselineReadsOffset) == 0 && _view.ReadInt32(ReferencePreparationAutomaticOffset) == 0;
    internal void RecordReferencePreparationAutomaticSettled() =>
        _view.Write(ReferencePreparationAutomaticSettledOffset, _clock.Timestamp);
    internal void ReferenceBeforeNative(byte[] report)
    {
        if (State == ShortRampPermitState.Unarmed && report[4] == 3 && report[13] == 4 && _view.ReadInt32(56) != 0)
            _view.Write(ReferenceBaselineReadsOffset, _view.ReadInt32(ReferenceBaselineReadsOffset) + 1);
        if (State == ShortRampPermitState.Unarmed && report[4] == 3 && report[13] == 6 && report[14] == 1 && _view.ReadInt32(56) != 0)
            _view.Write(ReferencePreparationAutomaticOffset, 1);
        if (report[4] is 1 or 4)
        {
            var offset = ReferenceReplyBitmapOffset + report[2] / 8;
            _view.Write(offset, (byte)(_view.ReadByte(offset) | (1 << (report[2] % 8))));
        }
    }
    internal void RecordReferenceSample(int rpm)
    {
        var count = _view.ReadInt32(760);
        if (count >= ReferenceSampleCapacity) throw new InvalidDataException("Reference original loop sample capacity exceeded.");
        // This is the same native consume instant already published at280,
        // not a second clock read that could create a different sample token.
        _view.Write(768 + count * 16, _view.ReadInt64(280)); _view.Write(776 + count * 16, rpm);
        _view.Write(780 + count * 16, count + 1); _view.Write(760, count + 1);
    }
    internal void ClaimReferenceLoop()
    {
        ValidateReferenceBinding(true);
        using var held = LockState(0);
        if (!IsReference || State != ShortRampPermitState.Armed || _view.ReadInt32(704) != 0)
            throw new InvalidDataException("Reference activation is authenticated and one-shot.");
        _view.Write(704, 1);
    }
    internal void DecideReferenceLoop()
    {
        ValidateReferenceBinding(true);
        using var held = LockState(0);
        var now = _clock.Timestamp; var sample = _view.ReadInt64(280); var rpm = _view.ReadInt32(288);
        if (!IsReference || State != ShortRampPermitState.Used || _view.ReadInt32(704) != 1 || _view.ReadInt64(712) != 0 ||
            _view.ReadInt32(760) == 0 || _view.ReadInt32(236) != 0 || sample <= _view.ReadInt64(320) ||
            now < sample || now - sample > Ticks(200) || now > _view.ReadInt64(32) + Ticks(ReferenceLoopDecisionMs) || rpm is < ReferenceEntryMinimumCachedRpm or > 1200)
            throw new InvalidDataException("Reference terminal decision requires the exact latest complete post-mode2 sample within200ms.");
        if (rpm >= 950 && now + Ticks(580) <= Math.Min(_view.ReadInt64(32) + Ticks(ReferenceLoopDecisionMs), OriginalPhaseCap))
            throw new InvalidDataException("Reference terminal decision is premature while the original200ms dwell and complete GET380 still fit.");
        _view.Write(720, sample); _view.Write(728, rpm); _view.Write(732, rpm < 950 ? 1 : 2);
        _view.Write(736, _view.ReadInt32(760)); Thread.MemoryBarrier(); _view.Write(712, now);
    }
    internal void RecordReferenceMode0WriteComplete()
    {
        var now = _clock.Timestamp;
        if (now > _view.ReadInt64(32) + Ticks(ReferenceWriteCompleteMs)) throw new TimeoutException("Reference mode0 native write exceeded A+10100.");
        _view.Write(752, now);
    }
    public ReferenceActivationReceipt? ReferenceReceipt
    {
        get
        {
            if (!IsReference || _view.ReadInt32(224) < 3) return null;
            var samples = Enumerable.Range(0, _view.ReadInt32(760)).Select(i => new ReferenceLoopSample(
                _view.ReadInt32(780 + i * 16), _view.ReadInt32(776 + i * 16), _view.ReadInt64(768 + i * 16))).ToArray();
            return new(ReferenceContract, ShortRampSoftwareProfile.ReferencePersistentQuiet.Id, Nonce,
                new(_view.ReadInt32(616), _view.ReadInt64(624), Nonce), OwnedWriter, GuardianIdentity, samples,
                _view.ReadInt32(732) == 1 ? "first-cached-below950" : "next-get380-after200ms-dwell-unavailable",
                _view.ReadInt64(712), _view.ReadInt64(720), _view.ReadInt32(728), _view.ReadInt64(744), _view.ReadInt64(752), NativePhaseEvidence!,
                NativeMode2Count, NativeMode0Count, NativeNormalCloseCount);
        }
    }
}

// A fixed codec for purpose5 activation only. Each command3 already waits for
// the native gate's measured uncancelled180ms. Recovery keeps MenuProtocol.
public sealed class ReferenceActivationCodec(IMenuTransport transport)
{
    private byte _tag = 1;
    internal void SetLoopTag() => _tag = 32;
    private byte[] Frame(byte command, byte count, byte[]? packet = null)
    {
        packet ??= []; var frame = new byte[65]; frame[1] = (byte)(8 + packet.Length);
        frame[2] = ++_tag; frame[3] = 0x39; frame[4] = command; frame[5] = count;
        frame[7] = 11; frame[8] = 0x37; packet.CopyTo(frame, 9); return frame;
    }
    private async Task<byte[]> Exchange(byte command, byte count, CancellationToken token)
    {
        var frame = Frame(command, count); var response = await transport.TransferAsync(frame, true, token).ConfigureAwait(false);
        if (response.Length != 65 || response[1] is < 4 or > 64 || response[0] != 0 || response[2] != frame[2] || response[3] != 0x39 || response[4] != 0)
            throw new InvalidDataException("Reference codec received a malformed or foreign reply.");
        return response.AsSpan(5, response[1] - 4).ToArray();
    }
    public Task SetAsync(byte mode, CancellationToken token) => WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, mode]), token);
    public Task OpenAsync(CancellationToken token) => WriteAsync(MenuProtocol.Ddc([3, 2, 0xa8, 0, 0]), token);
    public Task CloseAsync(CancellationToken token) => WriteAsync(MenuProtocol.Ddc([3, 2, 0x10, 0x5b, 0]), token);
    private async Task WriteAsync(byte[] packet, CancellationToken token) =>
        _ = await transport.TransferAsync(Frame(3, (byte)packet.Length, packet), false, token).ConfigureAwait(false);
    private async Task<byte[]> Field(byte field, CancellationToken token)
    {
        await WriteAsync(MenuProtocol.Ddc([3, 0x18, field]), token).ConfigureAwait(false);
        var data = await Exchange(4, 16, token).ConfigureAwait(false); var length = field == 5 ? 2 : 4;
        if (data.Length < 4 + length || data[0] != 0x18 || data[1] != field || data[2] != length || data[3] != 0)
            throw new InvalidDataException("Reference getter framing differs.");
        return data.AsSpan(4, length).ToArray();
    }
    public async Task<int> CachedAsync(CancellationToken token)
    { var data = await Field(4, token).ConfigureAwait(false); return data[0] * 256 + data[1]; }
    public async Task<int> ValidateAsync(CancellationToken token, bool requireInitialBaseline = true)
    {
        if (transport.Identity != new MenuDeviceIdentity(1,0x043e,0x9c33,0x0111,0xff00,1,65,65))
            throw new InvalidDataException("Reference bridge identity/descriptor differs; no report admitted.");
        if ((await Exchange(1, 0, token).ConfigureAwait(false)).Length == 0) throw new InvalidDataException("Empty reference identity.");
        await WriteAsync([0x51, 0x82, 1, 0x10, 0xac], token).ConfigureAwait(false);
        var brightness = await Exchange(4, 11, token).ConfigureAwait(false); byte checksum = 0x50;
        foreach (var item in brightness) checksum ^= item;
        if (brightness.Length != 11 || brightness[0] != 0x6e || brightness[1] != 0x88 || brightness[2] != 2 || brightness[3] != 0 || brightness[4] != 0x10 || checksum != 0)
            throw new InvalidDataException("Reference brightness sanity differs.");
        var range = await Field(3, token).ConfigureAwait(false);
        var initial = await CachedAsync(token).ConfigureAwait(false);
        if (requireInitialBaseline) Baseline(initial);
        else if (initial is not (0 or (>= 500 and <= 2500)))
            throw new InvalidDataException("Initial cached value is outside the inspected range or empty-cache sentinel: " + initial);
        var target = await Field(5, token).ConfigureAwait(false);
        if (!range.SequenceEqual(new byte[] { 9, 0xc4, 1, 0xf4 }) || !target.SequenceEqual(new byte[] { 4, 0x4c }))
            throw new InvalidDataException("Reference range/target differs.");
        return initial;
    }
    public static void Baseline(int rpm)
    {
        if (rpm is >= 1000 and <= 1200) return;
        var failure = new InvalidDataException("Reference baseline outside1000..1200.");
        failure.Data["ReferenceBaselineCachedRpm"] = rpm;
        throw failure;
    }
}

public static class ReferencePersistentActivation
{
    public static async Task<ReferenceActivationReceipt> ActivateAsync(IShortRampWorker worker, IMenuClock clock, CancellationToken token,
        bool initializeBaseline = false, Action<string, int>? preparation = null)
    {
        if (worker.Purpose != ShortRampPurpose.ReferencePersistentQuiet || worker.SoftwareProfileId != ShortRampSoftwareProfile.ReferencePersistentQuiet.Id)
            throw new InvalidDataException("Reference controller requires its exact immutable purpose/profile.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(16000);
        var codec = new ReferenceActivationCodec(worker.Transport);
        var initial = await codec.ValidateAsync(limit.Token, requireInitialBaseline: !initializeBaseline).ConfigureAwait(false);
        preparation?.Invoke("initial-cache", initial);
        if (initializeBaseline && (initial is < 1000 or > 1200)) preparation?.Invoke("baseline-wait", initial);
        await worker.ArmAsync(limit.Token).ConfigureAwait(false);
        await codec.SetAsync(1, limit.Token).ConfigureAwait(false); await codec.OpenAsync(limit.Token).ConfigureAwait(false);
        if (initializeBaseline && (initial is < 1000 or > 1200))
            await codec.SetAsync(1, limit.Token).ConfigureAwait(false);
        await clock.DelayAsync(700, limit.Token).ConfigureAwait(false);
        var baseline = await codec.CachedAsync(limit.Token).ConfigureAwait(false);
        // At most three reads inside the unchanged target+2s native preparation
        // horizon. Arm still requires a consumed1000..1200 post-Open sample.
        for (var read = 1; initializeBaseline && (baseline is < 1000 or > 1200) && read < 3; read++)
        {
            preparation?.Invoke("baseline-wait", baseline);
            baseline = await codec.CachedAsync(limit.Token).ConfigureAwait(false);
        }
        ReferenceActivationCodec.Baseline(baseline);
        preparation?.Invoke("baseline-ready", baseline);
        await worker.ArmPulseAsync(limit.Token).ConfigureAwait(false);
        var receipt = await worker.ActivateReferenceAsync(limit.Token).ConfigureAwait(false);
        var phase = receipt.Phase; var frequency = (clock as IShortRampQpcClock ?? new ShortRampSystemQpcClock()).Frequency;
        long Ticks(int ms) => frequency * ms / 1000;
        if (receipt.Contract != ShortRampPermit.ReferenceContract || receipt.Profile != worker.SoftwareProfileId || phase.QpcFrequency != frequency ||
            receipt.NativeMode2Count != 1 || receipt.NativeMode0Count != 1 || receipt.NativeNormalCloseCount != 1 ||
            receipt.Samples is not { Length: > 0 and <= ShortRampPermit.ReferenceSampleCapacity } ||
            receipt.Samples[^1].CachedRpm != receipt.ConsumedCachedRpm || receipt.Samples[^1].Qpc != receipt.ConsumedSampleQpc ||
            receipt.ConsumedCachedRpm is < ShortRampPermit.ReferenceEntryMinimumCachedRpm or > 1200 || receipt.ConsumedSampleAgeMilliseconds is < 0 or > 200 ||
            receipt.DecisionQpc > phase.ArmQpc + Ticks(ShortRampPermit.ReferenceLoopDecisionMs) || receipt.Mode2AttemptQpc < phase.ArmQpc || receipt.Mode2AttemptQpc > phase.ArmQpc + Ticks(100) ||
            phase.Mode0AttemptQpc < receipt.DecisionQpc || phase.Mode0AttemptQpc > Math.Min(receipt.DecisionQpc + Ticks(40), phase.ArmQpc + Ticks(ShortRampPermit.ReferenceMode0AttemptMs)) ||
            receipt.Mode0WriteCompletedQpc < phase.Mode0AttemptQpc || receipt.Mode0WriteCompletedQpc > phase.ArmQpc + Ticks(ShortRampPermit.ReferenceWriteCompleteMs) ||
            phase.Mode0SettledQpc - receipt.Mode0WriteCompletedQpc < Ticks(180) || phase.Mode0SettledQpc > phase.ArmQpc + Ticks(ShortRampPermit.ReferenceSettledMs) ||
            phase.NormalCloseAttemptQpc < phase.Mode0SettledQpc || phase.NormalCloseSettledQpc - phase.NormalCloseAttemptQpc < Ticks(180) ||
            phase.NormalCloseSettledQpc > phase.HoldDeadlineQpc || phase.HoldDeadlineQpc > Math.Min(phase.Mode0AttemptQpc + Ticks(1000), phase.GuardianReadyQpc + Ticks(16000)))
            throw new InvalidDataException("Reference native one-shot receipt is missing or foreign.");
        token.ThrowIfCancellationRequested();
        return receipt;
    }
    public static async Task<ReferenceActivationReceipt> RunWorkerLoopAsync(ShortRampPermit permit, IMenuTransport transport,
        IMenuClock clock, CancellationToken token)
    {
        permit.ClaimReferenceLoop(); var codec = new ReferenceActivationCodec(transport);
        // A distinct fixed tag range prevents preparation/reply-token reuse.
        codec.SetLoopTag();
        await codec.SetAsync(2, token).ConfigureAwait(false);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var rpm = await codec.CachedAsync(token).ConfigureAwait(false);
            var qpc = clock as IShortRampQpcClock ?? new ShortRampSystemQpcClock();
            var phase = permit.NativePhaseEvidence!;
            if (rpm < 950 || qpc.Timestamp + qpc.Frequency * 580 / 1000 > Math.Min(
                phase.ArmQpc + qpc.Frequency * ShortRampPermit.ReferenceLoopDecisionMs / 1000, phase.GuardianReadyQpc + qpc.Frequency * 16000 / 1000))
            { permit.DecideReferenceLoop(); break; }
            await clock.DelayAsync(200, token).ConfigureAwait(false);
        }
        await codec.SetAsync(0, token).ConfigureAwait(false);
        await codec.CloseAsync(token).ConfigureAwait(false);
        return permit.ReferenceReceipt ?? throw new InvalidDataException("Reference native Close did not publish Quiet.");
    }
}
