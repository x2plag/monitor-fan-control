# Supported monitor for this release

**1.0.1-self-recovery** provides an experimental Apply and exit path tested on one physical monitor:

| Identity | Tested value |
| --- | --- |
| Model | LG UltraGear 39GX950B |
| Windows EDID | GSM7861 |
| USB controller | VID 043E, PID 9C33, revision 0111 |
| Native bridge | MI_00, UsagePage FF00, 65-byte reports |
| Scaler firmware | 3.11.0 |
| MICOM firmware | 1.11 |

Other models and firmware are not validated for this Quiet path. The legacy compatibility JSON is retained as part of the original application; its entries do not establish compatibility with this release's 39GX950B implementation. A matching USB identity does not guarantee a retail model or cooling safety. Connect only one matching monitor over USB. See [SAFETY.md](SAFETY.md).

Русский: тихий режим этой версии проверен только на одном **LG UltraGear 39GX950B** с указанной конфигурацией. Другие модели и прошивки не подтверждены. Записи старого compatibility JSON не означают поддержку этого тихого режима.
