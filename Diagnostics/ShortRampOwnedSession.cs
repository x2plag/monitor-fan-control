using System.Diagnostics;

namespace MonitorFanControl;

// One composition shared by the native entry and no-HID joint fixtures. W's
// authenticated boot identity is retained before G acquires its reservations;
// W cannot construct its native adapter until G has acknowledged readiness.
public static class ShortRampOwnedSession
{
    public static async Task<IShortRampWorker> StartAsync(string nonce, MenuDeviceIdentity identity,
        ProcessStartInfo writerChild, ProcessStartInfo guardianChild, Action<MenuLogEntry> log,
        CancellationToken token, string? kernelPrefix = null, ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        var permit = ShortRampPermit.Create(nonce, identity, kernelPrefix, purpose: purpose);
        IShortRampWorker? guardian = null;
        try
        {
            _ = await ShortRampOwnedWriterProcess.StartAsync(writerChild, nonce, permit, log, token,
                async (registered, readyToken) => guardian = await ShortRampGuardianProcess.StartAsync(
                    guardianChild, nonce, permit, registered, log, readyToken, purpose), purpose);
            return new Lifetime(guardian ?? throw new InvalidOperationException("Guardian readiness was lost."), permit);
        }
        catch
        {
            try { if (guardian is not null) await guardian.DisposeAsync(); }
            finally { permit.Dispose(); }
            throw;
        }
    }
    private sealed class Lifetime(IShortRampWorker worker, ShortRampPermit permit) : IShortRampWorker
    {
        public ShortRampPurpose Purpose => permit.Purpose;
        public bool ReferenceReleased => permit.ReferenceReleased;
        public Task<ReferenceReleaseResult> ReleaseReferenceAsync(ReferenceActivationReceipt receipt, CancellationToken token) => worker.ReleaseReferenceAsync(receipt, token);
        public int? NativeMode2Count => permit.NativeMode2Count;
        public int? NativeMode0Count => permit.NativeMode0Count;
        public int? NativeNormalCloseCount => permit.NativeNormalCloseCount;
        public ShortRampNativePhaseEvidence? NativePhaseEvidence => permit.NativePhaseEvidence;
        public IMenuTransport Transport => worker.Transport;
        public ShortRampAutomaticProgress? AutomaticProgress => worker.AutomaticProgress;
        public ShortRampContextualProgress? ContextualProgress => worker.ContextualProgress;
        public ContextualRecoveryEvidence? ContextualEvidence => worker.ContextualEvidence;
        public int RecoveryWaitMilliseconds => worker.RecoveryWaitMilliseconds;
        public ShortRampObservedPulseBoundary? PulseBoundaryObservation => worker.PulseBoundaryObservation;
        public Task ArmAsync(CancellationToken token) => worker.ArmAsync(token);
        public Task ArmPulseAsync(CancellationToken token) => worker.ArmPulseAsync(token);
        public Task<ReferenceActivationReceipt> ActivateReferenceAsync(CancellationToken token) => worker.ActivateReferenceAsync(token);
        public Task CheckHealthAsync(CancellationToken token) => worker.CheckHealthAsync(token);
        public void Revoke() => worker.Revoke();
        public Task<MenuCleanupResult> FinishAsync(CancellationToken token) => worker.FinishAsync(token);
        public async ValueTask DisposeAsync() { try { await worker.DisposeAsync(); } finally { permit.Dispose(); } }
    }
}
