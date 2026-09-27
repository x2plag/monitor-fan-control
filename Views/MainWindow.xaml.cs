using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace MonitorFanControl;

public partial class MainWindow : Window
{
    private readonly FanControlService _service;
    private readonly AppSettingsStore _settingsStore;
    private int? _displayedTarget;
    private bool _allowClose;
    private bool _requestInProgress;
    private bool _applyAfterCurrentRequest;
    private DateTime _statusShownAt;

    private static readonly TimeSpan StatusHoldTime = TimeSpan.FromSeconds(8);

    internal event EventHandler? SettingsChanged;

    internal MainWindow(FanControlService service, AppSettingsStore settingsStore)
    {
        InitializeComponent();
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/MonitorFanControl.png"));
        _service = service;
        _settingsStore = settingsStore;
        DetectedModelText.Text = MonitorIdentityDetector.GetDisplayDescription();

        var settings = _settingsStore.Load();
        _displayedTarget = settings.TargetDuty;
        if (settings.TargetDuty is int target)
            DutySlider.Value = target;
        DutySlider.IsEnabled = settings.TargetDuty is not null;
        SelectedValue.Text = settings.TargetDuty?.ToString() ?? "—";
        KeepTargetCheckBox.IsChecked = settings.KeepTargetApplied;
        StartWithWindowsCheckBox.IsChecked = WindowsStartupRegistration.IsEnabled();
        UpdateApplyButton();
    }

    internal void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        CurrentValue.Text = snapshot.CurrentDuty?.ToString() ?? "—";
        // Idle polls publish an empty message; keep a recent result readable instead of clearing it.
        if (snapshot.Message.Length > 0 || DateTime.UtcNow - _statusShownAt >= StatusHoldTime)
            SetStatusText(snapshot.Message, snapshot.IsError);
        ConnectionLabel.Text = snapshot.IsConnected ? "Connected" : "Not connected";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            snapshot.IsConnected ? "Success" : "Disconnected");
        var target = _settingsStore.Load().TargetDuty;
        DutySlider.IsEnabled = target is not null;
        UpdateApplyButton();
        if (target != _displayedTarget)
        {
            _displayedTarget = target;
            if (target is int value)
                DutySlider.Value = value;
            else
                SelectedValue.Text = "—";
        }
    }

    internal void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _service.SetFastPolling(true);
    }

    internal void SetKeepTargetApplied(bool enabled)
    {
        KeepTargetCheckBox.IsChecked = enabled;
        UpdateApplyButton();
    }

    internal void SetStartWithWindows(bool enabled) => StartWithWindowsCheckBox.IsChecked = enabled;

    internal void CloseForApplicationExit()
    {
        _allowClose = true;
        Close();
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = e.NewValue is true;
        _service.SetFastPolling(visible);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyTitleBarTheme(AppTheme.IsDark);
    }

    internal void ApplyTitleBarTheme(bool isDark)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
            return;

        var useDarkMode = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(windowHandle, UseImmersiveDarkMode, ref useDarkMode,
            (uint)Marshal.SizeOf<int>());
    }

    private void DutySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SelectedValue is not null)
            SelectedValue.Text = ((int)Math.Round(DutySlider.Value)).ToString();
    }

    private async void DutySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await ApplySliderTargetAsync();
    }

    private async void DutySlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or
            Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await ApplySliderTargetAsync();
    }

    private async Task ApplySliderTargetAsync()
    {
        var target = (int)Math.Round(DutySlider.Value);
        if (target == _displayedTarget)
            return;
        if (_requestInProgress)
        {
            _applyAfterCurrentRequest = true;
            return;
        }

        await RunRequestAsync(_service.ApplyTargetAsync(target));
    }

    // Resends the target even when it is unchanged, e.g. after the monitor changed the fan itself.
    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_requestInProgress)
            return;

        await RunRequestAsync(_service.ApplyTargetAsync((int)Math.Round(DutySlider.Value)));
    }

    private void UpdateApplyButton()
    {
        ApplyButton.Visibility = KeepTargetCheckBox.IsChecked == true ? Visibility.Hidden : Visibility.Visible;
        ApplyButton.IsEnabled = DutySlider.IsEnabled && !_requestInProgress;
    }

    private async Task RunRequestAsync(Task request)
    {
        _requestInProgress = true;
        UpdateApplyButton();
        SetStatusText("Communicating with the fan controller…");
        try
        {
            await request;
        }
        catch (Exception ex)
        {
            SetStatusText(ErrorText.Describe(ex), isError: true);
        }
        finally
        {
            _requestInProgress = false;
            UpdateApplyButton();
            _displayedTarget = _settingsStore.Load().TargetDuty;
            if (_applyAfterCurrentRequest)
            {
                _applyAfterCurrentRequest = false;
                await ApplySliderTargetAsync();
            }
            else if (!DutySlider.IsMouseCaptureWithin)
            {
                if (_displayedTarget is int savedTarget)
                    DutySlider.Value = savedTarget;
                else
                    SelectedValue.Text = "—";
            }
        }
    }

    private void KeepTargetCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsStore.Load();
        settings.KeepTargetApplied = KeepTargetCheckBox.IsChecked == true;
        if (SaveSettings(settings))
        {
            _service.NotifySettingsChanged();
            SetStatusText(settings.KeepTargetApplied
                ? "Automatic target correction is on."
                : "Automatic target correction is off; use Apply to send the target again.");
        }
        else
        {
            KeepTargetCheckBox.IsChecked = !settings.KeepTargetApplied;
        }
        UpdateApplyButton();
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            WindowsStartupRegistration.SetEnabled(enabled);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            SetStatusText(enabled
                ? "The app will start hidden in the tray when you sign in."
                : "The app will no longer start automatically.");
        }
        catch (Exception ex)
        {
            StartWithWindowsCheckBox.IsChecked = WindowsStartupRegistration.IsEnabled();
            SetStatusText(ErrorText.Describe(ex), isError: true);
        }
    }

    private bool SaveSettings(AppSettings settings)
    {
        try
        {
            _settingsStore.Save(settings);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            SetStatusText(ErrorText.Describe(ex), isError: true);
            return false;
        }
    }

    private void SetStatusText(string message, bool isError = false)
    {
        StatusDetail.Text = message;
        StatusDetail.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "Error" : "Muted");
        _statusShownAt = DateTime.UtcNow;
    }

    private const uint UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint windowHandle, uint attribute,
        ref int attributeValue, uint attributeSize);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
