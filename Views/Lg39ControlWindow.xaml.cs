using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace MonitorFanControl;

public partial class Lg39ControlWindow : Window
{
    private readonly PersistentQuietController _controller;
    private readonly PersistentQuietFaultCapture _faultCapture = new();
    private bool _closing;
    private bool _exitReady;

    public Lg39ControlWindow()
    {
        InitializeComponent();
        _controller = PersistentQuietController.FromAdmissionFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "osd-admission.json"),
            token => ShortRampCli.StartNativeAsync(token, ShortRampPurpose.ReferencePersistentQuiet),
            expectedProfile: ShortRampSoftwareProfile.ReferencePersistentQuiet);
        _controller.StatusChanged += status =>
        {
            Dispatcher.BeginInvoke(UpdateStatus);
            _faultCapture.TryCapture(status);
        };
    }
    private async void QuietButton_Click(object sender, RoutedEventArgs e) => await _controller.RequestQuietAsync();
    private async void RestoreButton_Click(object sender, RoutedEventArgs e) => await _controller.RequestAutoAsync();

    private void UpdateStatus()
    {
        if (_closing) return;
        var status = _controller.Status;
        var (title, detail) = status.State switch
        {
            PersistentQuietState.Preparing => ("Подготавливаю тихий режим…", "Сервисное меню может ненадолго появиться и закроется автоматически."),
            PersistentQuietState.Activating => ("Включаю тихий режим…", "Сервисное меню может ненадолго появиться и закроется автоматически."),
            PersistentQuietState.Quiet => ("Тихо", "Работает до нажатия «Авто». Закрытие окна также возвращает автоматику."),
            PersistentQuietState.Restoring => ("Возвращаю автоматику…", "Восстанавливаю автоматику и завершаю управление. Это может занять до минуты."),
            PersistentQuietState.Faulted => ("Режим не подтверждён", ShortRampSoftwareProfile.ReferencePersistentQuiet.SoftwareQualified
                ? "Включение или восстановление не подтверждено. Нажми «Авто» для повторного запроса автоматики."
                : "Постоянный тихий режим ещё проходит проверку. Кнопка «Авто» запрашивает штатную автоматику."),
            _ => ("Авто", status.Cleanup is null ? "Тихий режим включается только по кнопке «Тихо»."
                : "Команды автоматики и возврат кэшированного значения подтверждены. Физическое состояние не измеряется.")
        };
        StatusText.Text = title;
        DetailText.Text = detail;
        QuietButton.IsEnabled = _controller.QuietAvailable && status.State is PersistentQuietState.Auto or PersistentQuietState.Faulted;
        RestoreButton.IsEnabled = status.State != PersistentQuietState.Restoring;
        BusyProgress.Visibility = status.State is PersistentQuietState.Preparing or PersistentQuietState.Activating or PersistentQuietState.Restoring
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        QuietButton.IsEnabled = RestoreButton.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Возвращаю автоматику перед выходом…";
        DetailText.Text = "Ожидаю завершения независимого восстановления. Это может занять до минуты.";
        if (!await _controller.RequestCloseAsync())
        {
            _closing = false;
            UpdateStatus();
            return;
        }
        await _controller.DisposeAsync();
        _exitReady = true;
        Close();
    }

    internal static void ShowExisting()
    {
        var handle = FindWindowW(null, "LG 39GX950B — вентилятор");
        if (handle == IntPtr.Zero) return;
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
}
