using System.IO;

namespace MonitorFanControl;

// Admission shared by the native transport's bounded queue and no-HID queue
// fixtures. Native execution still revalidates after transaction acquisition.
public static class ShortRampNativeQueueAdmission
{
    public static void Enqueue(ShortRampPermit permit, ShortRampPurpose purpose, byte[] report,
        bool reply, CancellationToken token, Func<byte[], CancellationToken, bool> tryEnqueue,
        ContextualRecoveryAuthorization? authorization = null)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (permit.Purpose != purpose)
            throw new InvalidDataException("Native queue purpose differs from its immutable permit.");
        token.ThrowIfCancellationRequested();
        var snapshot = (byte[])report.Clone();
        permit.ValidateQueuedFrame(snapshot, reply, authorization);
        token.ThrowIfCancellationRequested();
        if (!tryEnqueue(snapshot, token))
            throw new TimeoutException("Short native queue is busy; admission refused.");
    }
}
