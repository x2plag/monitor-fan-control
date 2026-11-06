# LG 39GX950B Quiet — 1.0.1-self-recovery

Experimental Windows x64 utility for **LG UltraGear 39GX950B**. Not affiliated with LG. Tested on one monitor: USB `043E:9C33`, revision `0111`, scaler `3.11.0`, MICOM `1.11`. Read [SAFETY.md](SAFETY.md).

## English

Download **LG-Quiet-1.0.1-self-recovery-win-x64.zip** from the [latest release](https://github.com/x2plag/monitor-fan-control/releases/tag/v1.0.1-self-recovery). The portable ZIP includes the .NET runtime.

1. Extract every file into a writable folder. Connect the monitor's USB data cable and close other monitor-control applications.
2. Run **Start.cmd**. Check that the service menu is closed and type **YES** to record your own confirmation for this session.
3. Click **«Применить и выйти» (Apply and exit)**.

The app prepares the initial state automatically. If USB is absent, connecting it within two minutes continues the request. The service menu may appear briefly and closes automatically. After success the window and helper processes exit **without restoring Auto**. There is no background keeper or automatic repeated lowering. Start.cmd is required; launching the EXE alone opens the legacy tray interface.

Logs: `%LOCALAPPDATA%\MonitorFanControl.Quiet\logs`. Cached RPM is not live tachometry. Temperature, cooling protection, complete fault/Auto recovery and persistence after power cycling are unverified.

## Русский

Скачайте **LG-Quiet-1.0.1-self-recovery-win-x64.zip** из [последнего релиза](https://github.com/x2plag/monitor-fan-control/releases/tag/v1.0.1-self-recovery). .NET уже включён.

1. Распакуйте все файлы в доступную для записи папку. Подключите USB-кабель передачи данных от монитора и закройте другие программы управления монитором.
2. Запустите **Start.cmd**. Проверьте, что сервисное меню закрыто, и введите **YES** — это ваше подтверждение для данного сеанса.
3. Нажмите **«Применить и выйти»**.

Программа сама подготовит исходное состояние. Если USB отсутствует, подключите его в течение двух минут. Сервисное меню может кратко появиться и закроется автоматически. После успеха окно и вспомогательные процессы завершатся **без возврата в Авто**. Фонового процесса и автоматических повторных снижений нет. EXE без параметров открывает старый tray-интерфейс; запускайте Start.cmd.

Экспериментальная версия, проверенная на одном мониторе. Свежие обороты, температура, тепловая защита, полный возврат в Авто при сбоях и сохранение после отключения питания не подтверждены. Журналы: `%LOCALAPPDATA%\MonitorFanControl.Quiet\logs`.

## Source and build

Production source is copied unchanged from `63ab5d436731d63e0e6e2229afad1e3605dfa4b6`. The released executable has SHA-256 `28ec4da70a2424279f15376f0c059220cac11fc15c001a0ee6e02fd165ea4ca7`. Packaging, launchers and these public instructions are separate from that source snapshot. A local rebuild is not claimed to reproduce the released EXE byte for byte.

On Windows with the .NET 10 SDK:

```powershell
dotnet publish MonitorFanControl.csproj -c Release -o publish -p:Version=1.0.1-self-recovery
Copy-Item Start.cmd, Start.ps1 publish
```

Run `publish\Start.cmd`. This source snapshot contains the application and resources; research captures and private operator admission are excluded. MIT License: [LICENSE.txt](LICENSE.txt).
