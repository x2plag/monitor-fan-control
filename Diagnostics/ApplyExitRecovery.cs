namespace MonitorFanControl;

public interface IApplyExitRecovery
{
    Task WaitForConnectionAsync(Action waiting, CancellationToken token);
}

public sealed class ReferenceApplyExitRecovery(
    Func<CancellationToken, Task<bool>> connected,
    IMenuClock? clock = null, Action<ApplyExitDiagnostic>? diagnostic = null) : IApplyExitRecovery
{
    private readonly IMenuClock _clock = clock ?? new MenuSystemClock();
    public async Task WaitForConnectionAsync(Action waiting, CancellationToken token)
    {
        var deadline = _clock.Milliseconds + 120000;
        var reported = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await connected(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) return;
            if (!reported) { waiting(); Report("waiting-usb", null); reported = true; }
            var remaining = deadline - _clock.Milliseconds;
            if (remaining <= 0) throw new MenuPreconditionException("USB control did not reconnect within two minutes. Connect the monitor USB data cable and press Apply again.");
            await _clock.DelayAsync((int)Math.Min(1000, remaining), token).ConfigureAwait(false);
        }
    }
    private void Report(string stage, IShortRampWorker? worker, string? detail = null)
    {
        try { diagnostic?.Invoke(new(DateTimeOffset.UtcNow, stage, detail, worker?.NativeMode2Count, worker?.NativeMode0Count, worker?.NativeNormalCloseCount)); }
        catch { }
    }
}
