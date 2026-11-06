using System.IO;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

public enum PersistentQuietState { Auto, Preparing, Activating, Quiet, Restoring, Faulted }
public sealed record PersistentQuietStatus(PersistentQuietState State, string Detail,
    MenuCleanupResult? Cleanup = null, ContextualRecoveryEvidence? Recovery = null)
{
    public ShortRampOsdAdmissionMetadata? OsdAdmission { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReferenceActivationReceipt? ReferenceActivation { get; init; }
}

// Intent and lifetime form one deep boundary. WPF only dispatches requests;
// there is no microphone dependency, saved resume, or normal Quiet duration.
public sealed class PersistentQuietController : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IShortRampWorker>> _start;
    private readonly Func<CancellationToken, Task<ShortRampOsdAdmission?>> _admission;
    private readonly bool _qualified;
    private readonly ShortRampSoftwareProfile _expectedProfile;
    private readonly IMenuClock _clock;
    private readonly object _sync = new();
    private readonly Action<MenuLogEntry> _log;
    private Task _operation = Task.CompletedTask;
    private Task<bool>? _closeOperation;
    private CancellationTokenSource? _stop;
    private IShortRampWorker? _worker;
    private bool _closing;
    private bool _restoreRequested;
    private bool _canStartQuiet = true;
    private ShortRampOsdAdmissionMetadata? _acceptedAdmission;
    private ReferenceActivationReceipt? _referenceActivation;
    public PersistentQuietStatus Status { get; private set; } = new(PersistentQuietState.Auto,
        "Quiet has not been requested; no hardware action started.");
    public event Action<PersistentQuietStatus>? StatusChanged;
    public bool QuietAvailable { get { lock (_sync) return !_closing && _canStartQuiet; } }

    public PersistentQuietController(Func<CancellationToken, Task<IShortRampWorker>> startWorker,
        Func<CancellationToken, Task<ShortRampOsdAdmission?>> readAdmission, bool softwareQualified,
        IMenuClock? clock = null, Action<MenuLogEntry>? log = null, ShortRampSoftwareProfile? expectedProfile = null)
    { _start = startWorker; _admission = readAdmission; _qualified = softwareQualified;
      _expectedProfile = expectedProfile ?? ShortRampSoftwareProfile.PersistentQuiet;
      _clock = clock ?? new MenuSystemClock(); _log = log ?? (_ => { }); }

    public static PersistentQuietController FromAdmissionFile(string path,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock? clock = null, Action<MenuLogEntry>? log = null,
        ShortRampSoftwareProfile? expectedProfile = null)
        => new(startWorker, token => ShortRampOsdAdmissionFile.ReadAsync(path, token),
            (expectedProfile ?? ShortRampSoftwareProfile.PersistentQuiet).SoftwareQualified, clock, log, expectedProfile);

    // Completes when this activation has been restored/finalized, allowing an
    // independent caller to await its entire lifetime without tying up the UI.
    public Task RequestQuietAsync()
    {
        lock (_sync)
        {
            if (_closing || !_canStartQuiet || !_operation.IsCompleted) return _operation;
            _restoreRequested = false;
            _referenceActivation = null;
            _stop?.Dispose();
            _stop = new();
            return _operation = Task.Run(() => RunAsync(quiet: true, _stop.Token));
        }
    }
    public Task RequestAutoAsync()
    {
        lock (_sync)
        {
            if (_closing) return _operation;
            return StartAuto();
        }
    }

    // Closing is a recoverable intent until restoration/finalization succeeds.
    // A failed attempt releases the close gate so explicit Auto can try again.
    public Task<bool> RequestCloseAsync()
    {
        lock (_sync)
        {
            if (_closing) return _closeOperation!;
            _closing = true;
            // A completed successful Auto already finalized its owners. Final
            // disposal must not start another zero-lowering restoration route.
            var pending = _operation.IsCompleted && Status is { State: PersistentQuietState.Auto, Cleanup: not null }
                ? _operation : StartAuto();
            return _closeOperation = CompleteCloseAsync(pending);
        }
    }
    private Task StartAuto()
    {
        if (_restoreRequested && !_operation.IsCompleted) return _operation;
        _restoreRequested = true;
        // Revoke before any serialized callback/await. The native permit
        // freezes the very first stop epoch across all three actors.
        _worker?.Revoke();
        _stop?.Cancel();
        if (!_operation.IsCompleted) return _operation;
        _stop?.Dispose(); _stop = new();
        return _operation = Task.Run(() => RunAsync(quiet: false, _stop.Token));
    }
    private async Task<bool> CompleteCloseAsync(Task pending)
    {
        await pending.ConfigureAwait(false);
        lock (_sync)
        {
            var ready = Status.State == PersistentQuietState.Auto;
            if (!ready) _closing = false;
            return ready;
        }
    }

    private async Task RunAsync(bool quiet, CancellationToken stop)
    {
        IShortRampWorker? worker = null;
        MenuCleanupResult? cleanup = null;
        ContextualRecoveryEvidence? recovery = null;
        Exception? failure = null;
        var started = false;
        try
        {
            if (quiet)
            {
                if (!_qualified) throw new MenuPreconditionException("Persistent Quiet software qualification is pending.");
                var admission = await _admission(stop).ConfigureAwait(false);
                if (admission is not { IsValid: true }) throw new MenuPreconditionException("The original OSD admission file is missing or invalid.");
                _acceptedAdmission = admission.Metadata;
                stop.ThrowIfCancellationRequested();
                Set(PersistentQuietState.Preparing, "Preparing explicit Quiet request; menu may appear briefly.");
            }
            else Set(PersistentQuietState.Restoring, "Requesting contextual automatic control without lowering.");
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                startup.CancelAfter(5000);
                worker = await _start(startup.Token).WaitAsync(TimeSpan.FromSeconds(5), startup.Token).ConfigureAwait(false);
            }
            started = true;
            if (worker.Purpose != _expectedProfile.Purpose || worker.SoftwareProfileId != _expectedProfile.Id)
                throw new InvalidDataException("Controller worker has a foreign immutable purpose/profile.");
            lock (_sync)
            {
                _worker = worker;
                if (stop.IsCancellationRequested) worker.Revoke();
            }
            if (quiet)
            {
                if (_expectedProfile.Purpose == ShortRampPurpose.ReferencePersistentQuiet)
                {
                    Set(PersistentQuietState.Activating, "Activating reference Quiet; service menu closes automatically.");
                    _referenceActivation = await ReferencePersistentActivation.ActivateAsync(worker, _clock, stop).ConfigureAwait(false);
                }
                else await PersistentQuietActivation.ActivateAsync(worker, _clock, () => Set(PersistentQuietState.Activating,
                    "Activating Quiet; service menu closes automatically."), _log, stop).ConfigureAwait(false);
                Set(PersistentQuietState.Quiet, "Quiet is active until Auto or normal exit. Fresh RPM and temperature are unavailable.");
                while (true)
                {
                    stop.ThrowIfCancellationRequested(); // Service Auto/Closing before renewing health.
                    await worker.CheckHealthAsync(stop).ConfigureAwait(false);
                    await Task.Delay(250, stop).ConfigureAwait(false);
                }
            }
            else await worker.ArmAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (quiet && stop.IsCancellationRequested && _restoreRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (worker is not null)
            {
                worker.Revoke();
                Set(PersistentQuietState.Restoring, "Restoring automatic control and finalizing independent owners.");
                try
                {
                    cleanup = await worker.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(worker.RecoveryWaitMilliseconds)).ConfigureAwait(false);
                    recovery = worker.ContextualEvidence;
                    if (cleanup is not { CommandsSubmitted: true, WorkerFailed: false } || recovery is not
                        { Complete: true, CachedRecoveryRangeObserved: true, Failures: 0, NativeFailures: 0 })
                        failure ??= new IOException("Contextual restoration or exact finalization is unconfirmed.");
                }
                catch (Exception ex) { failure ??= ex; }
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; }
                lock (_sync) { if (ReferenceEquals(_worker, worker)) _worker = null; }
            }
            lock (_sync) _canStartQuiet = failure is null || (!started && _canStartQuiet);
            Set(failure is null ? PersistentQuietState.Auto : PersistentQuietState.Faulted,
                failure is null ? "Automatic commands and cached recovery range confirmed in software; physical state is unverified." :
                (started ? "Restoration requires attention: " : "Quiet was not started: ") + failure.Message, cleanup, recovery);
        }
    }

    private void Set(PersistentQuietState state, string detail, MenuCleanupResult? cleanup = null, ContextualRecoveryEvidence? recovery = null)
    {
        var status = new PersistentQuietStatus(state, detail, cleanup, recovery) { OsdAdmission = _acceptedAdmission, ReferenceActivation = _referenceActivation };
        Status = status;
        try { _log(new(_clock.Milliseconds, "persistent-state", state + ": " + detail)); } catch { }
        // Observers are descriptive. A blocked UI/log callback cannot suspend
        // control dispatch or keep a dead actor's health lease alive.
        var observer = StatusChanged;
        if (observer is not null) ThreadPool.QueueUserWorkItem(_ => { try { observer(status); } catch { } });
    }
    public async ValueTask DisposeAsync()
    {
        if (!await RequestCloseAsync().ConfigureAwait(false)) return;
        lock (_sync) { _stop?.Dispose(); _stop = null; }
    }
}

public static class PersistentQuietActivation
{
    public static async Task ActivateAsync(IShortRampWorker worker, IMenuClock clock, Action activating,
        Action<MenuLogEntry> log, CancellationToken token)
    {
        if (worker.Purpose != ShortRampPurpose.PersistentQuiet) throw new InvalidDataException("Persistent activation purpose required.");
        using var activation = CancellationTokenSource.CreateLinkedTokenSource(token); activation.CancelAfter(16000);
        var ct = activation.Token;
        var qpc = clock as IShortRampQpcClock ?? new ShortRampSystemQpcClock();
        var protocol = new MenuProtocol(worker.Transport, clock, log, 450);
        await protocol.ValidateAsync(ct).ConfigureAwait(false);
        Baseline(await protocol.CachedRpmAsync(ct).ConfigureAwait(false));
        await worker.ArmAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        await protocol.SetAsync(MenuCommand.Automatic, ct).ConfigureAwait(false);
        await protocol.SetAsync(MenuCommand.Close, ct).ConfigureAwait(false);
        await protocol.SetAsync(MenuCommand.Open, ct).ConfigureAwait(false);
        await clock.DelayAsync(700, ct).ConfigureAwait(false);
        Baseline(await protocol.CachedRpmAsync(ct).ConfigureAwait(false));
        activating();
        var before = qpc.Timestamp;
        await worker.ArmPulseAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        var after = qpc.Timestamp;
        var phase = Evidence();
        if (phase.ArmQpc < before || phase.ArmQpc > after ||
            phase.RampDeadlineQpc - phase.ArmQpc != qpc.Frequency * 2400 / 1000)
            throw new InvalidDataException("Activation arm/deadline does not match native evidence.");
        RequireRamp(1580);
        await protocol.WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, 2]), ct).ConfigureAwait(false);
        RequireRamp(1020);
        Entry(await protocol.CachedRpmAsync(ct).ConfigureAwait(false));
        var toFinal = (phase.ArmQpc + qpc.Frequency * 1200 / 1000 - qpc.Timestamp) * 1000 / qpc.Frequency;
        if (toFinal > 0) await clock.DelayAsync(checked((int)toFinal), ct).ConfigureAwait(false);
        RequireRamp(1020);
        Entry(await protocol.CachedRpmAsync(ct).ConfigureAwait(false));
        RequireRamp(460);
        await protocol.WriteAsync(MenuProtocol.Ddc([3, 0x18, 6, 0]), ct).ConfigureAwait(false);
        phase = Evidence();
        if (worker.NativeMode2Count != 1 || worker.NativeMode0Count != 1 || phase.Mode0AttemptQpc <= phase.ArmQpc ||
            phase.Mode0SettledQpc - phase.Mode0AttemptQpc < qpc.Frequency * 180 / 1000 || phase.Mode0SettledQpc > phase.RampDeadlineQpc ||
            phase.HoldDeadlineQpc > phase.Mode0AttemptQpc + qpc.Frequency || qpc.Timestamp + qpc.Frequency * 460 / 1000 >= phase.HoldDeadlineQpc)
            throw new InvalidDataException("Mode0 native success or finite pre-Close reserve is missing.");
        await protocol.SetAsync(MenuCommand.Close, ct).ConfigureAwait(false);
        phase = Evidence();
        if (worker.NativeNormalCloseCount != 1 || phase.NormalCloseAttemptQpc < phase.Mode0SettledQpc ||
            phase.NormalCloseSettledQpc - phase.NormalCloseAttemptQpc < qpc.Frequency * 180 / 1000 || phase.NormalCloseSettledQpc > phase.HoldDeadlineQpc)
            throw new InvalidDataException("A settled one-shot native Close is required before Quiet.");
        token.ThrowIfCancellationRequested();

        ShortRampNativePhaseEvidence Evidence() => worker.NativePhaseEvidence is { } evidence && evidence.QpcFrequency == qpc.Frequency && evidence.ArmQpc > 0
            ? evidence : throw new InvalidDataException("Authenticated native phase evidence is missing.");
        void RequireRamp(int reserve)
        {
            if (qpc.Timestamp + qpc.Frequency * reserve / 1000 >= phase.RampDeadlineQpc)
                throw new TimeoutException("Original2.4s activation cannot admit complete native work.");
            ct.ThrowIfCancellationRequested();
        }
        static void Baseline(int rpm) { if (rpm is < 1000 or > 1200) throw new InvalidDataException("Cached baseline outside1000..1200."); }
        static void Entry(int rpm) { if (rpm is < 950 or > 1200) throw new InvalidDataException("Final consumed cached entry outside950..1200."); }
    }
}
