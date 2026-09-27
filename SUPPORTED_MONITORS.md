# Supported monitors

## Tested

| Monitor | Firmware | App testing |
| --- | --- | --- |
| 32GS95UV-B | `0202` | Hardware-tested |

## Compatibility candidates

These are possible matches for firmware profiles accepted by the app. **None has been tested with the app.** A match does not confirm that a particular monitor has that firmware, contains a fan, or can be cooled safely at a chosen duty.

| Monitor | Possible firmware profile |
| --- | --- |
| 27GS95QE | `0202` |
| 32GS95UE | `0202` |
| 34GS95QE | `0202` |
| 39GS95QE | `0202` |
| 45GS96QB | `0202` |
| 27GR93U | `0117` |
| 27GR95QE | `0117` |
| 27GR95UM | `0202` |
| 45GR95QE | `0113` |
| 32GQ950 | `0113` |
| 32GQ850 | `0113` |
| 48GQ900 | `0113` or `0118` |
| 27GX700A | `0204` |
| 27GX790A | `0204` |
| 45GX950A | `0204` |
| 27GP950 | `0607` |
| 27GN950 | `0607` |
| 34GP950G | `0607` |
| 38GL950G | `0607` |
| 38GN950 | `0607` |

Fan presence is uncertain for the 34GS95QE, 27GX700A, and 38GN950. The 27GN950 association is also uncertain. The app identifies a controller by its USB/HID profile and firmware version rather than a guaranteed retail model name. It may recognize a monitor outside this list.

Connect only one of these monitors to a PC over USB. With several matching controllers, the app cannot tell them apart and sends no commands.
