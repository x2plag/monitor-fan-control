# Safety and limitations

**Independent utility; not affiliated with, endorsed by, or supported by LG Electronics. Use this software at your own risk.** The app sends undocumented commands to a monitor's fan controller. Incorrect settings or unexpected device behavior may reduce cooling or damage equipment.

## Testing and compatibility

The app has been hardware-tested only with the LG UltraGear 32GS95UV-B, firmware `0202`. Other models listed in [Supported monitors](SUPPORTED_MONITORS.md) are untested compatibility candidates. The app checks a USB/HID controller profile and firmware version; it does not reliably identify the connected retail monitor. A matching controller may not correspond to the listed model and may not operate a physical fan. Connect only one monitor with this fan controller over USB; with several, the app sends no commands because it cannot tell them apart.

## Fan control

- The app sets a fan duty value from 25 to 100. This is an app input range, not a safety rating.
- The displayed value is controller duty, not measured fan RPM. Temperature data from the tested monitor is not valid, so the app provides no thermal feedback or protection.
- Automatic correction is enabled by default. On first run, once the reported duty has held steady for a few seconds, the app saves it as its initial target and may switch the controller to manual mode at that duty. Later, it restores the saved target at startup and after it detects a change.
- While the USB connection is interrupted, for example by a KVM switch, the app cannot read or correct the fan. The monitor may change its fan setting in the meantime; the tested 32GS95UV-B did after a KVM switch. With automatic correction on, the app reapplies the saved target when it reconnects.
- Disabling automatic correction or exiting stops this app from sending further corrections. The app does not send a command to restore automatic fan mode; the last manual setting may remain until the monitor changes or resets it.

Pay attention to the monitor's heat and fan behavior during use. If cooling seems abnormal, stop using the utility and consult the monitor's documentation or LG support. The app cannot confirm adequate cooling.

## What the app does not do

The app does not flash firmware, write EEPROM settings, update monitor firmware, or communicate over the internet. The service menu does not need to be activated.
