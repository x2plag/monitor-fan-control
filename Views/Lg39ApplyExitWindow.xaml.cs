using System.ComponentModel;
using System.Windows;

namespace MonitorFanControl;

public partial class Lg39ApplyExitWindow : Window
{
    private readonly ApplyExitController _controller;
    private readonly ApplyExitJournal _journal = new();
    private bool _closing, _exitReady;
    public Lg39ApplyExitWindow()
    {
        InitializeComponent();
        _controller = ApplyExitController.FromAdmissionFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "osd-admission.json"),
            token => ShortRampCli.StartNativeAsync(token, ShortRampPurpose.ReferencePersistentQuiet), _journal.TryWrite,
            ApplyExitPlatformRecovery.Create(_journal.TryWrite));
        _controller.StatusChanged += _ => Dispatcher.BeginInvoke(UpdateStatus);
    }
    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        await _controller.RequestApplyAsync();
        UpdateStatus();
        if (_controller.Status is { State: ApplyExitState.Released, Release.ChildrenExited: true })
        {
            await _controller.DisposeAsync();
            await FlushJournalAsync();
            _exitReady = true;
            Close();
        }
    }
    private void UpdateStatus()
    {
        var status = _controller.Status;
        (StatusText.Text, DetailText.Text) = status.State switch
        {
            ApplyExitState.Ready => ("Готов к применению",
                "Нажми «Применить и выйти». После успеха все процессы завершатся без возврата автоматики."),
            ApplyExitState.Applying => ("Применяю тихий режим…",
                "Сервисное меню может ненадолго появиться и закроется автоматически."),
            ApplyExitState.Releasing => ("Завершаю управление…",
                "Ожидаю подтверждения выхода обоих независимых процессов."),
            ApplyExitState.WaitingForUsb => ("Ожидаю USB-соединение с монитором…",
                "Подключи USB-кабель передачи данных от LG к этому ПК. После появления устройства программа продолжит сама; ожидание до двух минут."),
            ApplyExitState.Repairing => ("Восстанавливаю начальное состояние…",
                "Временно включаю штатное управление и проверяю исходное значение. Тихий режим включится только после успешной проверки."),
            ApplyExitState.Released => ("Применено. Управление завершено.",
                "Окно закроется после подтверждения завершения управления."),
            ApplyExitState.Failed => ("Применение или выход не подтверждены", status.Release?.Committed == true
                ? "Настройка применена, но завершение процессов не подтверждено. Возврат автоматики не запрашивается. Причина: " + status.Detail
                : "Применение не подтверждено. Причина: " + status.Detail),
            _ => ("Готов к применению", "Окно закроется после подтверждения завершения управления.")
        };
        ApplyButton.IsEnabled = !_closing && _controller.CanApply;
        BusyProgress.Visibility = status.State is ApplyExitState.Applying or ApplyExitState.Releasing or ApplyExitState.WaitingForUsb or ApplyExitState.Repairing ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        var active = _controller.Status.State is ApplyExitState.Applying or ApplyExitState.Releasing or ApplyExitState.Repairing;
        ApplyButton.IsEnabled = false;
        await _controller.RequestCloseAsync();
        await _controller.DisposeAsync();
        await FlushJournalAsync();
        if (active && _controller.Status.State == ApplyExitState.Failed)
        {
            _closing = false;
            UpdateStatus();
            return;
        }
        _exitReady = true;
        // The awaited operation can already be complete on idle/failure close.
        // WPF forbids recursively closing inside the original Closing event.
        await Dispatcher.InvokeAsync(Close);
    }
    private async Task FlushJournalAsync()
    {
        try { await _journal.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { } // Failed/slow support storage cannot keep native owners alive.
    }
}
