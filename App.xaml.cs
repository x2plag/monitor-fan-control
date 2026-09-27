using Microsoft.Win32;
using System.Threading;
using System.Windows;

namespace MonitorFanControl;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\MonitorFanControl.Instance";
    private const string ShowWindowEventName = @"Local\MonitorFanControl.ShowWindow";

    private AppSettingsStore? _settingsStore;
    private FanControlService? _service;
    private TrayController? _tray;
    private MainWindow? _settingsWindow;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _listenerLifetime;
    private bool _ownsMutex;
    private bool _exiting;
    private bool? _darkTheme;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (CommandLine.IsRequested(e.Args))
        {
            Environment.ExitCode = await CommandLine.RunAsync(e.Args);
            Shutdown();
            return;
        }

        var startInBackground = e.Args.Contains("--background", StringComparer.Ordinal);
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            SignalExistingInstance(startInBackground);
            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _listenerLifetime = new CancellationTokenSource();
        _ = ListenForShowRequestsAsync(_listenerLifetime.Token);

        RefreshTheme();
        _settingsStore = new AppSettingsStore();
        _service = new FanControlService(_settingsStore);
        _tray = new TrayController(_settingsStore.Load());
        _tray.ApplyTheme();
        _tray.OpenRequested += ShowSettingsWindow;
        _tray.KeepTargetChanged += SetKeepTargetApplied;
        _tray.StartWithWindowsChanged += SetStartWithWindows;
        _tray.ExitRequested += () => _ = ExitAsync();
        _service.StatusChanged += OnStatusChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _service.Start();
        if (!startInBackground)
            ShowSettingsWindow();
    }

    private void OnStatusChanged(MonitorSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _tray?.UpdateStatus(snapshot);
            _settingsWindow?.UpdateSnapshot(snapshot);
        });
    }

    private void ShowSettingsWindow()
    {
        if (_service is null || _settingsStore is null)
            return;

        RefreshTheme();
        if (_settingsWindow is null)
        {
            _settingsWindow = new MainWindow(_service, _settingsStore);
            _settingsWindow.SettingsChanged += (_, _) => RefreshSettingsControls();
            _settingsWindow.ShowAndActivate();
        }
        else
        {
            _settingsWindow.ShowAndActivate();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_exiting)
            _ = Dispatcher.BeginInvoke(new Action(RefreshTheme));
    }

    private void RefreshTheme()
    {
        var isDark = AppTheme.IsDark;
        if (_darkTheme == isDark)
            return;

        AppTheme.Apply(isDark);
        _darkTheme = isDark;
        _tray?.ApplyTheme();
        _settingsWindow?.ApplyTitleBarTheme(isDark);
    }

    private void SetKeepTargetApplied(bool enabled)
    {
        if (_settingsStore is null)
            return;

        try
        {
            var settings = _settingsStore.Load();
            settings.KeepTargetApplied = enabled;
            _settingsStore.Save(settings);
            _service?.NotifySettingsChanged();
            _settingsWindow?.SetKeepTargetApplied(enabled);
            RefreshSettingsControls();
        }
        catch (Exception ex)
        {
            _tray?.ShowMessage("Fan Control", ErrorText.Describe(ex));
            RefreshSettingsControls();
        }
    }

    private void SetStartWithWindows(bool enabled)
    {
        if (_settingsStore is null)
            return;

        try
        {
            WindowsStartupRegistration.SetEnabled(enabled);
            _settingsWindow?.SetStartWithWindows(enabled);
            RefreshSettingsControls();
        }
        catch (Exception ex)
        {
            _tray?.ShowMessage("Fan Control", ErrorText.Describe(ex));
            RefreshSettingsControls();
        }
    }

    private void RefreshSettingsControls()
    {
        if (_settingsStore is null)
            return;

        var settings = _settingsStore.Load();
        _tray?.UpdateSettings(settings);
        _settingsWindow?.SetKeepTargetApplied(settings.KeepTargetApplied);
        _settingsWindow?.SetStartWithWindows(WindowsStartupRegistration.IsEnabled());
    }

    private async Task ListenForShowRequestsAsync(CancellationToken cancellationToken)
    {
        var showEvent = _showWindowEvent;
        if (showEvent is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var signaled = await Task.Run(() => showEvent.WaitOne(), cancellationToken);
                if (signaled && !cancellationToken.IsCancellationRequested)
                    _ = Dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void SignalExistingInstance(bool startInBackground)
    {
        if (startInBackground)
            return;

        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;

        _listenerLifetime?.Cancel();
        _showWindowEvent?.Set();
        if (_service is not null)
            await _service.DisposeAsync();

        _settingsWindow?.CloseForApplicationExit();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listenerLifetime?.Cancel();
        _listenerLifetime?.Dispose();
        _showWindowEvent?.Dispose();
        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
