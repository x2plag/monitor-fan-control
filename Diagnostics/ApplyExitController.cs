using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitorFanControl;

public enum ApplyExitState { Ready, Applying, Releasing, Released, Failed, WaitingForUsb, Repairing }
public sealed record ApplyExitStatus(ApplyExitState State, string Detail, ReferenceReleaseResult? Release = null);

// An explicit one-shot intent. Construction and idle close never acquire native
// ownership. Precommit failure retains ordinary bounded recovery; a terminal
// shared release can only be finalized and can never become an Auto request.
public sealed class ApplyExitController : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IShortRampWorker>> _start;
    private readonly Func<CancellationToken, Task<ShortRampOsdAdmission?>> _admission;
    private readonly IMenuClock _clock;
    private readonly Action<ApplyExitDiagnostic>? _diagnostic;
    private readonly IApplyExitRecovery? _recovery;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _operation = Task.CompletedTask;
    private IShortRampWorker? _worker;
    private bool _requested, _closing, _disposed;
    private int _attempt = 1;
    public ApplyExitStatus Status { get; private set; } = new(ApplyExitState.Ready,
        "Press Apply and exit to begin. Opening this window performs no monitor operation.");
    public event Action<ApplyExitStatus>? StatusChanged;
    public ApplyExitController(Func<CancellationToken, Task<IShortRampWorker>> start,
        Func<CancellationToken, Task<ShortRampOsdAdmission?>> admission, IMenuClock? clock = null,
        Action<ApplyExitDiagnostic>? diagnostic = null, IApplyExitRecovery? recovery = null)
    { _start = start; _admission = admission; _clock = clock ?? new MenuSystemClock(); _diagnostic = diagnostic; _recovery = recovery; }
    public static ApplyExitController FromAdmissionFile(string path, Func<CancellationToken, Task<IShortRampWorker>> start,
        Action<ApplyExitDiagnostic>? diagnostic = null, IApplyExitRecovery? recovery = null) =>
        new(start, token => ShortRampOsdAdmissionFile.ReadAsync(path, token), diagnostic: diagnostic, recovery: recovery);
    public bool CanApply => !_closing && _operation.IsCompleted &&
        (Status.State == ApplyExitState.Ready || Status.State == ApplyExitState.Failed && Status.Release?.Committed != true);
    public Task RequestApplyAsync()
    {
        lock (_sync)
        {
            if (_closing || _requested && (!CanApply || Status.State != ApplyExitState.Failed)) return _operation;
            _requested = true;
            return _operation = Task.Run(RunAsync);
        }
    }
    public Task RequestCloseAsync()
    {
        lock (_sync)
        {
            _closing = true;
            if (!_operation.IsCompleted) { _worker?.Revoke(); _stop.Cancel(); }
            return _operation;
        }
    }
    private async Task RunAsync()
    {
        _attempt = 1;
        try
        {
            if (_recovery is not null)
                await _recovery.WaitForConnectionAsync(() => Set(ApplyExitState.WaitingForUsb,
                    "USB control is unavailable. Connect the monitor USB data cable; application resumes automatically."), _stop.Token).ConfigureAwait(false);
            await RunAttemptAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record("recovery-failed", null, ApplyExitJournal.Describe(ex));
            Set(ApplyExitState.Failed, "Automatic preparation did not complete: " + ApplyExitJournal.Describe(ex));
        }
    }
    private async Task RunAttemptAsync()
    {
        IShortRampWorker? worker = null;
        ReferenceReleaseResult? released = null;
        Exception? failure = null;
        var committed = false;
        try
        {
            Record("admission", worker);
            if (!ShortRampSoftwareProfile.ReferencePersistentQuiet.SoftwareQualified)
                throw new MenuPreconditionException("Reference software qualification is pending.");
            var admission = await _admission(_stop.Token).ConfigureAwait(false);
            if (admission is not { IsValid: true }) throw new MenuPreconditionException("The original OSD admission is missing or invalid.");
            _stop.Token.ThrowIfCancellationRequested();
            Set(ApplyExitState.Applying, "Applying Quiet; service menu will close automatically.");
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                Record("starting-owners", worker);
                startup.CancelAfter(5000);
                worker = await _start(startup.Token).WaitAsync(TimeSpan.FromSeconds(5), startup.Token).ConfigureAwait(false);
            }
            lock (_sync) { _worker = worker; if (_stop.IsCancellationRequested) worker.Revoke(); }
            if (worker.Purpose != ShortRampPurpose.ReferencePersistentQuiet || worker.SoftwareProfileId != ShortRampSoftwareProfile.ReferencePersistentQuiet.Id)
                throw new InvalidDataException("Apply-and-exit requires the exact immutable reference purpose/profile.");
            var receipt = await ReferencePersistentActivation.ActivateAsync(worker, _clock, _stop.Token,
                initializeBaseline: _recovery is not null, preparation: (stage, rpm) =>
                {
                    Record(stage, worker, "CachedRpm=" + rpm);
                    if (stage == "baseline-wait") Set(ApplyExitState.Repairing, "Initializing automatic control before the strict baseline gate.");
                    if (stage == "baseline-ready") Set(ApplyExitState.Applying, "Baseline confirmed; applying Quiet.");
                }).ConfigureAwait(false);
            Record("activation-complete", worker, JsonSerializer.Serialize(new {
                receipt.ConsumedCachedRpm,
                DecisionMilliseconds = (receipt.DecisionQpc - receipt.Phase.ArmQpc) * 1000d / receipt.Phase.QpcFrequency,
                CloseMilliseconds = (receipt.Phase.NormalCloseSettledQpc - receipt.Phase.ArmQpc) * 1000d / receipt.Phase.QpcFrequency }));
            Set(ApplyExitState.Releasing, "Finalizing the settled application and exiting both independent owners.");
            released = await worker.ReleaseReferenceAsync(receipt, _stop.Token).ConfigureAwait(false);
            Record("release-result", worker, "Committed=" + released.Committed + "; childrenExited=" + released.ChildrenExited + "; " + released.Detail);
            committed = worker.ReferenceReleased;
            if (!committed || released is not { Committed: true, ChildrenExited: true })
                throw new IOException(released.Detail);
        }
        catch (Exception ex) { failure = ex; Record("primary-failure", worker, ApplyExitJournal.Describe(ex)); }
        finally
        {
            if (worker is not null)
            {
                committed |= worker.ReferenceReleased;
                if (!committed)
                {
                    worker.Revoke();
                    try
                    {
                        var cleanup = await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(worker.RecoveryWaitMilliseconds)).ConfigureAwait(false);
                        Record("cleanup-result", worker, cleanup.Detail);
                        if (cleanup is not { CommandsSubmitted: true, WorkerFailed: false })
                            failure = new IOException("Application failed; bounded restoration is unconfirmed. " + cleanup.Detail, failure);
                    }
                    catch (Exception ex) { failure = new IOException("Application failed; bounded restoration is unconfirmed.", new AggregateException(failure ?? ex, ex)); }
                }
                Record("disposing-owners", worker);
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; }
                lock (_sync) _worker = null;
            }
            Set(failure is null ? ApplyExitState.Released : ApplyExitState.Failed,
                failure is null ? "Applied and released. Both exact child processes exited." :
                (committed ? "Applied/released, but finalization requires attention: " : "Application was not completed: ") + ApplyExitJournal.Describe(failure),
                released);
            Record(failure is null ? "success" : "failed", null, failure is null ? null : ApplyExitJournal.Describe(failure));
        }
    }
    private void Record(string stage, IShortRampWorker? worker, string? detail = null)
    {
        try { _diagnostic?.Invoke(new(DateTimeOffset.UtcNow, stage, detail,
            worker?.NativeMode2Count, worker?.NativeMode0Count, worker?.NativeNormalCloseCount, _attempt)); }
        catch { } // Observation must never change native admission or recovery.
    }
    private void Set(ApplyExitState state, string detail, ReferenceReleaseResult? release = null)
    {
        var status = new ApplyExitStatus(state, detail, release); Status = status;
        var observer = StatusChanged;
        if (observer is not null) ThreadPool.QueueUserWorkItem(_ => { try { observer(status); } catch { } });
    }
    public async ValueTask DisposeAsync()
    {
        await RequestCloseAsync().ConfigureAwait(false);
        lock (_sync) { if (_disposed) return; _disposed = true; _stop.Dispose(); }
    }
}

public sealed record ApplyExitDiagnostic(DateTimeOffset AtUtc, string Stage, string? Detail,
    int? Mode2Count, int? Mode0Count, int? CloseCount, int Attempt = 1);

// Local, bounded support evidence. No raw HID reports, serials or stack paths.
public sealed class ApplyExitJournal
{
    private readonly object _sync = new();
    private int _rows;
    private readonly Queue<ApplyExitDiagnostic> _pending = new();
    private Task _flush = Task.CompletedTask;
    private bool _writing;
    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorFanControl.Quiet", "logs", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
    public void TryWrite(ApplyExitDiagnostic entry)
    {
        try
        {
            lock (_sync)
            {
                if (_rows++ >= 128) return;
                _pending.Enqueue(entry);
                if (!_writing) { _writing = true; _flush = Task.Run(Drain); }
            }
        }
        catch { }
    }
    public Task FlushAsync() { lock (_sync) return _flush; }
    private void Drain()
    {
        while (true)
        {
            ApplyExitDiagnostic entry;
            lock (_sync)
            {
                if (!_pending.TryDequeue(out entry!)) { _writing = false; return; }
            }
            try
            {
                var json = JsonSerializer.Serialize(entry with { Detail = entry.Detail is null ? null : Clean(entry.Detail) }) + "\n";
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length + Encoding.UTF8.GetByteCount(json) > 65536) continue;
                File.AppendAllText(Path, json, Encoding.UTF8);
            }
            catch { }
        }
    }
    internal static string Describe(Exception exception)
    {
        var pending = new Queue<Exception>(); pending.Enqueue(exception);
        var messages = new List<string>();
        while (pending.TryDequeue(out var item) && messages.Count < 4)
        {
            messages.Add(item.GetType().Name + ": " + Clean(item.Message));
            if (item.Data["ReferenceBaselineCachedRpm"] is int rpm) messages.Add("InitialCachedRpm=" + rpm);
            if (item is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) pending.Enqueue(inner);
            else if (item.InnerException is { } inner) pending.Enqueue(inner);
        }
        return string.Join(" | ", messages);
    }
    private static string Clean(string text)
    {
        text = text.Split(['\r', '\n'])[0];
        text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\|\bhid#)[^;]*", "[path]");
        text = string.Concat(text.Take(2048).Select(c => char.IsControl(c) ? ' ' : c));
        return text;
    }
}
