using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MonitorFanControl;

public sealed record ReferenceReleasePreparation(long Epoch, string ReceiptDigest);
public sealed record ReferenceReleaseResult(bool Committed, bool ChildrenExited,
    ShortRampOwnedProcessIdentity Writer, ShortRampOwnedProcessIdentity Guardian, string Detail);

// ABI5's terminal ownership boundary. Preparation has no effect on recovery.
// The parent's one CAS arbitrates against EVERY actor's first Revoke, without
// waiting for a state mutex which a suspended peer could hold indefinitely.
public sealed partial class ShortRampPermit
{
    // Existing layout ends at1855. All atomic fields are naturally aligned.
    //1856 disposition(0 active/1 recovering/2 released),1864 epoch,1872 digest32,
    //1904 W-ready epoch,1912 G-ready epoch,1920 commit QPC,1928/1932 final marks.
    private const int ReleaseDispositionOffset = 1856;
    public bool ReferenceReleased => IsReference && ReleaseDisposition == 2;
    internal bool ReferenceWriterFinalized => ReferenceReleased && _view.ReadInt32(1928) == 1;
    internal bool ReferenceGuardianFinalized => ReferenceReleased && _view.ReadInt32(1932) == 1;
    private unsafe int ReleaseDisposition
    {
        get
        {
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try { return Volatile.Read(ref *(int*)(pointer + _view.PointerOffset + ReleaseDispositionOffset)); }
            finally { _view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
    }
    private unsafe int ArbitrateReferenceDisposition(int value)
    {
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try { return Interlocked.CompareExchange(ref *(int*)(pointer + _view.PointerOffset + ReleaseDispositionOffset), value, 0); }
        finally { _view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
    private static string ReceiptDigest(ReferenceActivationReceipt receipt) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt)));

    private ReferenceActivationReceipt ValidateReleaseReceipt()
    {
        if (!IsReference) throw new InvalidOperationException("Only reference ABI5 supports explicit release.");
        ValidateReferenceBinding(false);
        var receipt = ReferenceReceipt;
        if (ReleaseDisposition != 0 || State != ShortRampPermitState.Used || !PersistentQuietActive ||
            PersistentHealthExpired || PersistentStopQpc != 0 || _view.ReadInt32(236) != 0 ||
            receipt is not { NativeMode2Count: 1, NativeMode0Count: 1, NativeNormalCloseCount: 1, Writer: not null, Guardian: not null } ||
            receipt.Samples.Length is < 1 or > ReferenceSampleCapacity || receipt.DecisionQpc <= 0 ||
            receipt.ConsumedSampleQpc <= receipt.Mode2AttemptQpc || receipt.ConsumedCachedRpm is < ReferenceEntryMinimumCachedRpm or > 1200 ||
            receipt.Samples[^1].Qpc != receipt.ConsumedSampleQpc || receipt.Samples[^1].CachedRpm != receipt.ConsumedCachedRpm ||
            receipt.Phase.NormalCloseSettledQpc <= receipt.Phase.NormalCloseAttemptQpc ||
            receipt.Phase.NormalCloseSettledQpc > receipt.Phase.HoldDeadlineQpc)
            throw new InvalidOperationException("Release requires the complete current healthy settled activation.");
        return receipt;
    }
    private void ValidateReleaseParent()
    {
        using var self = Process.GetCurrentProcess();
        ValidateReferenceParent(self);
    }
    public ReferenceReleasePreparation PrepareReferenceRelease(ReferenceActivationReceipt receipt)
    {
        if (!IsReference) throw new InvalidOperationException("Release is unavailable for this purpose.");
        ValidateReleaseParent();
        using var held = LockState(0);
        var actual = ValidateReleaseReceipt();
        if (receipt is null || ReceiptDigest(actual) != ReceiptDigest(receipt) || _view.ReadInt64(1864) != 0)
            throw new InvalidDataException("Foreign, stale or repeated release receipt.");
        var digest = ReceiptDigest(actual);
        var bytes = Convert.FromHexString(digest);
        _view.WriteArray(1872, bytes, 0, bytes.Length);
        var epoch = _clock.Timestamp;
        if (epoch <= 0) throw new InvalidDataException("Invalid release epoch.");
        Thread.MemoryBarrier(); _view.Write(1864, epoch);
        return new(epoch, digest);
    }
    private void ValidateReleasePreparation(ReferenceReleasePreparation preparation)
    {
        var actual = ValidateReleaseReceipt();
        var bytes = new byte[32]; _view.ReadArray(1872, bytes, 0, bytes.Length);
        if (preparation.Epoch <= 0 || preparation.Epoch != _view.ReadInt64(1864) ||
            preparation.ReceiptDigest != Convert.ToHexString(bytes) || preparation.ReceiptDigest != ReceiptDigest(actual))
            throw new InvalidDataException("Foreign release preparation or changed immutable receipt.");
    }
    internal void AcknowledgeReferenceRelease(ReferenceReleasePreparation preparation, bool writer)
    {
        ValidateReleasePreparation(preparation);
        var exact = writer ? OwnedWriter : GuardianIdentity;
        if (exact is null || exact.Pid != Environment.ProcessId || exact.StartUtcTicks != CurrentProcessStartUtcTicks || exact.Nonce != Nonce)
            throw new InvalidDataException("Only the exact child may acknowledge its own release readiness.");
        var offset = writer ? 1904 : 1912;
        if (_view.ReadInt64(offset) != 0) throw new InvalidDataException("Release readiness cannot be replayed.");
        Thread.MemoryBarrier(); _view.Write(offset, preparation.Epoch);
    }
    internal void CommitReferenceRelease(ReferenceReleasePreparation preparation, CancellationToken token)
    {
        ValidateReleaseParent();
        using var cancellation = token.UnsafeRegister(static state => ((ShortRampPermit)state!).Revoke(), this);
        token.ThrowIfCancellationRequested();
        ValidateReleasePreparation(preparation);
        if (_view.ReadInt64(1904) != preparation.Epoch || _view.ReadInt64(1912) != preparation.Epoch)
            throw new InvalidDataException("Both exact release acknowledgments are required.");
        _view.Write(1920, _clock.Timestamp);
        if (ArbitrateReferenceDisposition(2) != 0)
            throw new InvalidOperationException("Recovery won terminal ownership before release.");
    }
    internal void ConfirmReferenceFinalized(bool writer)
    {
        if (!ReferenceReleased) return;
        var exact = writer ? OwnedWriter : GuardianIdentity;
        if (exact?.Pid != Environment.ProcessId || exact.StartUtcTicks != CurrentProcessStartUtcTicks)
            throw new InvalidDataException("Foreign release finalization identity.");
        _view.Write(writer ? 1928 : 1932, 1);
    }
    internal void ThrowIfReferenceReleased()
    {
        if (ReferenceReleased) throw new InvalidOperationException("Released ownership permanently denies all native reports.");
    }
    internal async Task ObserveReferenceReleaseAsync(CancellationTokenSource released, CancellationToken lifetime)
    {
        if (!IsReference) return;
        try
        {
            while (!ReferenceReleased) await Task.Delay(10, lifetime).ConfigureAwait(false);
            released.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
