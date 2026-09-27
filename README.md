# Monitor Fan Control

An independent Windows tray app and command-line tool for reading and setting fan duty on selected LG monitors. **This project is not affiliated with, endorsed by, or supported by LG Electronics.**

> **Use at your own risk.** This app sends undocumented commands to a monitor fan controller and can override automatic fan behavior. The selected duty range is not an LG safety rating. The app does not provide reliable fan-speed or temperature feedback. Read [Safety and limitations](SAFETY.md) before use.

## Why this app exists

This app began as a way to adjust fan duty without changing monitor firmware or enabling the service menu. It communicates with the fan controller over USB, so **the service menu does not need to be activated**.

## Features

- View live controller duty, connection status, and the monitor information reported by Windows.
- Set and save a target with the slider or one-shot command-line options.
- Reapply the saved target when the controller changes it, if enabled.
- Run in the notification area and optionally start hidden with Windows.
- Verify the controller readback after each write.

## Compatibility

The app has been hardware-tested only with the **LG UltraGear 32GS95UV-B**, firmware `0202`. Other models in [Supported monitors](SUPPORTED_MONITORS.md) are untested compatibility candidates. A listed firmware association does not confirm that a particular retail monitor has that firmware or contains a physical fan.

The app checks the controller's USB identity, firmware version, and HID report sizes before sending a command. The monitor name shown in the app comes from Windows and may not uniquely identify the physical monitor connected to the fan controller.

Connect only one monitor with this fan controller to a PC over USB. With two or more, the app cannot tell them apart or link them to a display, so it controls none of them and says so. Other monitors, including LG monitors connected only by a video cable, do not affect it.

The human-editable [`monitor-compatibility.json`](monitor-compatibility.json) sits beside the executable and lists accepted firmware versions and model information. If the file is missing, the app restores its built-in default. Existing files are used as-is, so edits are preserved. Adding a firmware version allows commands to be sent to any matching controller that reports that version. Only change this file if you understand and have independently verified the compatibility; restart the app after editing it.

## Download and Windows SmartScreen

Download the release ZIP only from the [Releases page](https://github.com/ykorsten/monitor-fan-control/releases). The executable is not code-signed yet, so Windows SmartScreen and some browsers may warn that it is unrecognized or not commonly downloaded. This warning is based on download reputation; it does not mean a problem was found.

1. Optionally, compare the ZIP's checksum with `SHA256SUMS.txt` from the same release. Letter case does not matter.

   ```powershell
   Get-FileHash .\MonitorFanControl-1.0-portable-win-x64.zip -Algorithm SHA256
   ```

2. Before extracting, right-click the ZIP, select **Properties**, check **Unblock**, and select **OK**. The extracted files are then not marked as downloaded from the internet. In PowerShell, `Unblock-File .\MonitorFanControl-1.0-portable-win-x64.zip` does the same.
3. If SmartScreen still appears when you start the app, select **More info**, then **Run anyway**.

## Desktop app and tray

Connect the monitor with a USB data cable, extract a release ZIP, and start `MonitorFanControl.exe`. Move the slider and release it to apply and save a target. On first launch, the app saves the current duty as its initial target once the reading has held steady for a few seconds. Automatic target correction is enabled by default and may place the controller in manual mode at that duty. See [Safety and limitations](SAFETY.md) before use.

The window reads the controller once per second. When the window is hidden in the tray, it reads every 10 seconds. Repeated automatic corrections are limited to once every 10 seconds. Turn off **Keep this target applied automatically** to continue reading status without correcting the fan setting; select **Apply** to send the saved target again without moving the slider.

If the USB connection drops, for example when a KVM switch selects another PC, the app shows **Not connected**. The monitor may change its fan setting in the meantime; the tested 32GS95UV-B did after a KVM switch. The app reconnects as soon as Windows reports the controller again and, with automatic correction on, reapplies the saved target if the controller changed. With automatic correction off, select **Apply** to restore it. If another PC also runs this app, that PC applies its own saved target while it is connected.

Closing the window hides it in the tray. Double-click the tray icon or select **Open fan control** to show it again. The tray menu lets you toggle automatic correction, enable **Start with Windows**, and **Exit**. Windows startup applies to the current user and launches the app hidden in the tray. After moving the app to another folder, turn **Start with Windows** on again from the new location. Exiting stops monitoring and further corrections; the last manual setting may remain active.

The current duty and connection state appear in the tray tooltip. Settings are stored at `%LocalAppData%\MonitorFanControl\settings.json`.

## Command-line interface

Run these commands from PowerShell in the folder containing the executable:

```powershell
.\MonitorFanControl.exe --read
.\MonitorFanControl.exe --set 45
.\MonitorFanControl.exe --help
```

| Command | Result |
| --- | --- |
| `--read` | Reads once and prints the controller duty, such as `45`. |
| `--set 45` | Saves `45` as the target, sets the duty, and reads it back for up to 5 seconds while the controller settles. |
| `--help` | Shows command usage without accessing the monitor. |

CLI commands are one-shot: they exit without opening the app window or tray icon. `--set` saves the target used by the tray app but does not change the automatic-correction preference. Values outside **25–100** are rejected. The CLI can be used while the tray app is running; device requests are serialized. A successful command exits with code `0`; controller errors or an unconfirmed write exit with code `1`; invalid arguments exit with code `2`.

PowerShell may return to the prompt before this Windows GUI executable finishes. For scripts that need its output and exit code, use `Start-Process -Wait -PassThru` with standard-output and standard-error redirection.

## Build and downloads

On Windows with the .NET 10 SDK installed, build the app with:

```powershell
dotnet build MonitorFanControl.csproj --configuration Release
```

Run `build-release.ps1` to create local ZIP packages and checksums. The small package requires the **.NET 10 Desktop Runtime for Windows x64**; the portable package includes it and is about 70 MB. Neither package includes monitor firmware. See Microsoft's [single-file deployment documentation](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview) and [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

GitHub Actions builds the app on Windows for pushes and pull requests. To prepare a GitHub release, push a tag matching the project version (for example `v1.0`), then run **Actions → Prepare release** on `main` with that tag. The workflow builds the packages and creates a **draft** release; running it again for the same tag refreshes that draft. GitHub provides the source archives automatically. Review the files and notes, then select **Publish release** when ready. Normal pushes do not publish releases.

## Safety and limitations

The duty display does not measure fan RPM or temperature. The **25–100** range is an app input range, not a safety rating. Automatic correction can override the monitor's own fan behavior, and exiting the app does not explicitly restore automatic mode. Read [SAFETY.md](SAFETY.md) for the full limitations. The executable is unsigned, so Windows may show an unknown publisher warning; see [Download and Windows SmartScreen](#download-and-windows-smartscreen).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build and contribution guidance. The app uses .NET/WPF, Windows Forms for the tray icon, and Windows HID/SetupAPI.

## License

Released under the MIT License. See [LICENSE.txt](LICENSE.txt).
