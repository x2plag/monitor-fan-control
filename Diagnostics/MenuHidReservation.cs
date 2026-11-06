using System.Threading;

namespace MonitorFanControl;

// The semaphore has no abandonment recovery. The parent retains both kernel
// objects before ready; ready proves the worker acquired this lease. A marker
// set before Release distinguishes an owned stranded lease from an ambiguous
// release or a later foreign owner. Call takeover only after confirmed exit.
internal sealed class MenuHidReservation(Semaphore semaphore, EventWaitHandle releaseStarted)
{
    internal bool TryAcquire(int milliseconds) => semaphore.WaitOne(milliseconds);

    internal bool TryTakeAfterWorkerExit(int milliseconds)
    {
        if (!releaseStarted.WaitOne(0))
        {
            releaseStarted.Set();
            semaphore.Release();
        }
        return semaphore.WaitOne(milliseconds);
    }

    internal void Release()
    {
        // Death between these calls is ambiguous: a successor must wait/refuse,
        // never assume it can increment the historical semaphore's count.
        releaseStarted.Set();
        semaphore.Release();
    }
}
