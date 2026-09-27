# Contributing

Monitor Fan Control is an independent utility for selected LG monitors. It is not affiliated with, endorsed by, or supported by LG Electronics.

## Issues and pull requests

- State the monitor model, firmware version, requested duty, and observed readback when reporting a hardware issue. Do not include serial numbers, account data, or unredacted USB captures.
- Keep changes focused on the app and its documented read/set behavior.
- Do not add firmware flashing, EEPROM writes, automatic updates, or network communication.
- Do not commit LG firmware packages, firmware binaries, copied vendor source code, or decompiled/disassembled vendor code.
- Follow [SAFETY.md](SAFETY.md) when testing hardware.

## Build and verification

On Windows with the .NET 10 SDK, run:

```powershell
dotnet build MonitorFanControl.csproj --configuration Release
```

GitHub Actions builds pushes and pull requests. The separate manual release workflow creates draft releases with built packages; publishing still requires review. HID behavior requires a monitor and must be tested manually; CI has no monitor attached and does not send controller commands.
