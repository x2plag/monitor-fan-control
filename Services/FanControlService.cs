using System.IO;

namespace MonitorFanControl;

internal sealed record MonitorSnapshot(byte? CurrentDuty, string Message, bool IsConnected, bool IsError);

internal sealed class FanControlService : IAsyncDisposable
{
    private const int ActivePollSeconds = 1;
    private const int TrayPollSeconds = 10;
    private const int CorrectionCooldownSeconds = 10;
    private const int InitialTargetSettleSeconds = 5;
    private const int ReconnectRetrySeconds = 10;

    private readonly AppSettingsStore _settingsStore;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HidFanController? _controller;
    private Task? _pollTask;
    private TaskCompletionSource _pollingModeChanged = NewSignal();
    private volatile bool _fastPolling;
    private volatile bool _lastPollFailed;
    private DateTimeOffset _nextCorrectionTime = DateTimeOffset.MinValue;
    private byte? _initialDutyCandidate;
    private DateTimeOffset _initialDutySince;
    private FanControllerWatcher? _watcher;
    private long _controllerArrivedAt;

    public event Action<MonitorSnapshot>? StatusChanged;

    public FanControlService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Start()
    {
        if (_pollTask is not null)
            return;

        _watcher = new FanControllerWatcher();
        _watcher.Changed += OnControllerChanged;
        _pollTask = Task.Run(PollLoopAsync);
    }

    // After a KVM switch or replug, poll at once. Windows may need a moment before the new
    // interface opens, so keep retrying every second for a short while after an arrival.
    private void OnControllerChanged(bool arrived)
    {
        if (arrived)
            Volatile.Write(ref _controllerArrivedAt, Environment.TickCount64);
        WakePollLoop();
    }

    public void SetFastPolling(bool enabled)
    {
        if (_fastPolling == enabled)
            return;

        _fastPolling = enabled;
        WakePollLoop();
    }

    public void NotifySettingsChanged() => WakePollLoop();

    // Runs off the caller's thread: connecting enumerates devices synchronously.
    public Task ApplyTargetAsync(int targetDuty) => Task.Run(() => ApplyTargetCoreAsync(targetDuty));

    private async Task ApplyTargetCoreAsync(int targetDuty)
    {
        if (targetDuty is < FanDutyLimits.Minimum or > FanDutyLimits.Maximum)
            throw new ArgumentOutOfRangeException(nameof(targetDuty), "Choose a fan setting from 25 to 100.");

        await _operationLock.WaitAsync(_lifetime.Token);
        try
        {
            var (controller, current) = await ReadStatusReconnectingAsync(_lifetime.Token);
            ValidateWritableStatus(current);

            await controller.SetDutyAsync((byte)targetDuty, _lifetime.Token);
            _nextCorrectionTime = DateTimeOffset.UtcNow.AddSeconds(CorrectionCooldownSeconds);

            var settings = _settingsStore.Load();
            settings.TargetDuty = targetDuty;
            var saveError = TrySaveSettings(settings);

            var readback = await controller.ReadBackDutyAsync((byte)targetDuty, _lifetime.Token);
            _lastPollFailed = false;
            var targetConfirmed = readback.Confirms(targetDuty);
            var message = saveError is not null
                ? $"Applied {targetDuty}%, but the target could not be saved: {saveError} Automatic correction may restore the previous target."
                : targetConfirmed
                    ? $"Applied and verified target {targetDuty}%."
                    : $"Sent target {targetDuty}%; readback is {readback.CurrentDuty}% (mode {readback.Mode}).";
            Publish(readback, message, isError: saveError is not null || !targetConfirmed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lastPollFailed = true;
            await DisconnectAsync();
            PublishError(ErrorText.Describe(ex));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task PollLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            // Take the signal before polling so a wake-up requested during the poll is not lost.
            var signal = Volatile.Read(ref _pollingModeChanged);
            await PollOnceAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested)
                break;

            var arrivedAt = Volatile.Read(ref _controllerArrivedAt);
            var reconnecting = _lastPollFailed && arrivedAt != 0 &&
                               Environment.TickCount64 - arrivedAt < ReconnectRetrySeconds * 1000L;
            var interval = reconnecting || (_fastPolling && !_lastPollFailed) ? ActivePollSeconds : TrayPollSeconds;
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(interval), _lifetime.Token), signal.Task);
        }
    }

    // After a quick switch away and back, the old handle is dead although the monitor is present
    // again. Reading is safe to repeat, so retry once on a fresh connection before giving up.
    private async Task<(HidFanController Controller, FanStatus Status)> ReadStatusReconnectingAsync(
        CancellationToken cancellationToken)
    {
        if (_controller is not null)
        {
            try
            {
                return (_controller, await _controller.ReadStatusAsync(cancellationToken));
            }
            catch (FanControllerDisconnectedException)
            {
                await DisconnectAsync();
            }
        }

        var controller = _controller = HidFanController.Connect();
        return (controller, await controller.ReadStatusAsync(cancellationToken));
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var (controller, status) = await ReadStatusReconnectingAsync(cancellationToken);
            if (!status.IsInTestedRange)
            {
                _lastPollFailed = true;
                Publish(status, "Connected, but the controller state is outside the tested range.", isError: true);
                return;
            }

            _lastPollFailed = false;

            var settings = _settingsStore.Load();
            var idleMessage = string.Empty;
            if (settings.TargetDuty is null)
            {
                if (!TryAdoptInitialTarget(status, settings))
                    return;
                // Replaces the waiting message, which would otherwise stay on screen for a while.
                idleMessage = $"Saved the current {settings.TargetDuty}% as the initial target.";
            }

            var targetDuty = settings.TargetDuty;
            var targetAlreadyApplied = targetDuty is int savedDuty && status.Confirms(savedDuty);
            if (!settings.KeepTargetApplied || targetDuty is null || targetAlreadyApplied)
            {
                Publish(status, idleMessage);
                return;
            }

            if (DateTimeOffset.UtcNow < _nextCorrectionTime)
            {
                Publish(status, $"Target {targetDuty}% saved; waiting before the next correction.");
                return;
            }

            await controller.SetDutyAsync((byte)targetDuty.Value, cancellationToken);
            _nextCorrectionTime = DateTimeOffset.UtcNow.AddSeconds(CorrectionCooldownSeconds);
            var readback = await controller.ReadBackDutyAsync((byte)targetDuty.Value, cancellationToken);
            var targetConfirmed = readback.Confirms(targetDuty.Value);
            var message = targetConfirmed
                ? $"Applied and verified saved target {targetDuty}%."
                : $"Sent target {targetDuty}%; readback is {readback.CurrentDuty}% (mode {readback.Mode}).";
            Publish(readback, message, isError: !targetConfirmed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lastPollFailed = true;
            await DisconnectAsync();
            PublishError(ErrorText.Describe(ex));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // The controller steps upward one duty per control tick (it boots at 9 heading for 39), so a
    // single reading can be mid-ramp. Adopt a duty only after it has held for a few seconds.
    private bool TryAdoptInitialTarget(FanStatus status, AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        if (_initialDutyCandidate != status.CurrentDuty)
        {
            _initialDutyCandidate = status.CurrentDuty;
            _initialDutySince = now;
        }

        if (status.CurrentDuty < FanDutyLimits.Minimum ||
            now - _initialDutySince < TimeSpan.FromSeconds(InitialTargetSettleSeconds))
        {
            Publish(status, "Waiting for a steady reading before saving the initial target.");
            return false;
        }

        settings.TargetDuty = status.CurrentDuty;
        var saveError = TrySaveSettings(settings);
        if (saveError is null)
            return true;

        Publish(status, $"The initial target could not be saved: {saveError}", isError: true);
        return false;
    }

    // A settings-file problem is reported on its own; it must not disconnect the controller.
    private string? TrySaveSettings(AppSettings settings)
    {
        try
        {
            _settingsStore.Save(settings);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static void ValidateWritableStatus(FanStatus status)
    {
        if (!status.IsInTestedRange)
            throw new FanToolException("The current controller state is outside the tested range. Nothing was changed.");
    }

    private void Publish(FanStatus status, string message, bool isError = false) =>
        StatusChanged?.Invoke(new MonitorSnapshot(status.CurrentDuty, message, true, isError));

    private void PublishError(string message) =>
        StatusChanged?.Invoke(new MonitorSnapshot(null, message, false, true));

    private async Task DisconnectAsync()
    {
        if (_controller is null)
            return;
        await _controller.DisposeAsync();
        _controller = null;
    }

    private void WakePollLoop()
    {
        var replacement = NewSignal();
        var previous = Interlocked.Exchange(ref _pollingModeChanged, replacement);
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _lifetime.Cancel();
        WakePollLoop();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _operationLock.WaitAsync();
        try
        {
            await DisconnectAsync();
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
            _lifetime.Dispose();
        }
    }
}
