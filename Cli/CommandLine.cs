using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace MonitorFanControl;

internal static class CommandLine
{
    private const uint AttachParentConsole = 0xFFFFFFFF;

    public static bool IsRequested(string[] args) =>
        args.Any(argument => argument != "--background");

    public static async Task<int> RunAsync(string[] allArgs)
    {
        if (ShortRampCommand.TryParseChild(allArgs, out var shortChild))
        {
            try { return await ShortRampCli.RunChildAsync(shortChild!.Role, shortChild.Nonce, shortChild.ParentPid, shortChild.Purpose); }
            catch (Exception) { return 1; }
        }
        // The independent worker must not join the listening parent's console:
        // Ctrl+C must reach the parent without killing its cleanup process.
        if (allArgs.Length == 3 && allArgs[0] == "--menu39-worker" &&
            Guid.TryParseExact(allArgs[1], "N", out _) && int.TryParse(allArgs[2], out var menuParent) && menuParent > 0)
        {
            try { return await MenuContextCli.RunWorkerAsync(allArgs[1], menuParent); }
            catch (Exception) { return 1; }
        }
        AttachToParentConsole();
        var args = allArgs.Where(argument => argument != "--background").ToArray();

        if (args.Length == 1 && args[0] == "--help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            if (ShortRampCommand.TryParseContextualHold(allArgs, out var contextualHold))
                return await ShortRampCli.RunAsync(contextualHold!.ObservationPath, contextualHold.Purpose);
            if (ShortRampCommand.TryParseClosedHold(allArgs, out var closedHold))
                return await ShortRampCli.RunAsync(closedHold!.ObservationPath, closedHold.Purpose);
            if (ShortRampCommand.TryParseControl(allArgs, out var control))
                return await ShortRampCli.RunAsync(control!.ObservationPath, control.Purpose);
            if (args.Length == 2 && args[0] == "--test39-short-ramp-audio")
                return await ShortRampCli.RunAsync(args[1]);
            if (args.Length == 1 && args[0] == "--test39-menu-context-audio")
                return await MenuAudioCli.RunAsync();
            if (args.Length == 1 && args[0] == "--test39-menu-context")
                return await MenuContextCli.RunAsync();
            if (args.Length == 1 && args[0] == "--manual39-auto")
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(HidFanController.Lg39BridgeAction.ManualAutomatic);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--manual39-hold" &&
                ValidSessionEvent(args[1], @"Local\MonitorFanControl.Lg39Stop.") &&
                ValidSessionEvent(args[2], @"Local\MonitorFanControl.Lg39Ready.") &&
                int.TryParse(args[3], out var ownerId) && ownerId > 0)
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(HidFanController.Lg39BridgeAction.ManualHold,
                    stopEventName: args[1], manualReadyEventName: args[2], ownerProcessId: ownerId);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--test39-native-controlled" &&
                args[1].StartsWith(@"Local\MonitorFanControl.Lg39Stop.", StringComparison.Ordinal) &&
                Guid.TryParseExact(args[1][@"Local\MonitorFanControl.Lg39Stop.".Length..], "N", out _))
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(HidFanController.Lg39BridgeAction.BoundedFanTest,
                    stopEventName: args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] is "--probe39-i2c-bridge" or "--probe39-i2c-factory")
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(args[0] == "--probe39-i2c-factory"
                    ? HidFanController.Lg39BridgeAction.FanReadback : HidFanController.Lg39BridgeAction.Probe);
                return 0;
            }

            if (args.Length == 1 && args[0] is "--test39-native-fan" or "--test39-native-hold" or "--restore39-native-auto")
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(args[0] switch
                {
                    "--test39-native-fan" => HidFanController.Lg39BridgeAction.BoundedFanTest,
                    "--test39-native-hold" => HidFanController.Lg39BridgeAction.BoundedHoldTest,
                    _ => HidFanController.Lg39BridgeAction.RestoreAutomatic
                });
                return 0;
            }

            if (args.Length == 2 && args[0] is "--restore39-native-watchdog" or "--restore39-hold-watchdog" &&
                args[1].StartsWith(@"Local\MonitorFanControl.Lg39Restore.", StringComparison.Ordinal) &&
                Guid.TryParseExact(args[1][@"Local\MonitorFanControl.Lg39Restore.".Length..], "N", out _))
            {
                await HidFanController.ProbeLg39I2cBridgeAsync(args[0] == "--restore39-hold-watchdog"
                    ? HidFanController.Lg39BridgeAction.HoldRestoreWatchdog : HidFanController.Lg39BridgeAction.RestoreWatchdog, args[1]);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--probe39-identity")
            {
                Console.WriteLine("39GX950B: one read-only MCU identity query (D1), with no payload or fan-setting command.");
                await using var candidate = HidFanController.ConnectLg39StatusProbe();
                await candidate.ProbeLg39ReadCommandAsync(0xD1, CancellationToken.None);
                return 0;
            }

            if (args.Length == 2 && args[0] == "--probe39-read" &&
                byte.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var readOpcode))
            {
                Console.WriteLine($"39GX950B: one allowlisted read-only opcode {readOpcode:X2}; no payload or setting command.");
                await using var candidate = HidFanController.ConnectLg39StatusProbe();
                await candidate.ProbeLg39ReadCommandAsync(readOpcode, CancellationToken.None);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--probe39-feature")
            {
                Console.WriteLine("39GX950B: one status query (0xDE) through the advertised 64-byte HID feature channel; no fan-setting command.");
                await using var candidate = HidFanController.ConnectLg39StatusProbe();
                var payload = candidate.ProbeLg39FeatureStatus();
                Console.WriteLine($"Feature-channel status payload: {Convert.ToHexString(payload)}");
                return 0;
            }

            if (args.Length == 1 && args[0] == "--probe39")
            {
                Console.WriteLine("39GX950B candidate: one status request (0xDE), 32-byte firmware packet. Fan-setting commands are disabled.");
                await using var candidate = HidFanController.ConnectLg39StatusProbe();
                var status = await candidate.ReadStatusAsync(CancellationToken.None);
                Console.WriteLine($"Status: duty={status.CurrentDuty}; mode={status.Mode}; sensorRaw={status.SensorValue}; derivedRaw={status.DerivedValue}; gpioRaw={status.GpioValue}; constant={status.FirmwareConstant}; inTestedRange={status.IsInTestedRange}");
                return 0;
            }

            if (args.Length == 1 && args[0] == "--observe39")
            {
                await HidFanController.ObserveLg39Async();
                return 0;
            }

            if (args.Length == 1 && args[0] == "--observe39-i2c")
            {
                Console.WriteLine("39GX950B HID-I2C interface: one standard GET_FEATURE request; no output or feature report is sent.");
                HidFanController.ObserveLg39I2cFeatureReport();
                return 0;
            }

            if (args.Length == 1 && args[0] == "--diagnose")
            {
                Console.WriteLine("LG USB descriptor diagnosis (no monitor commands sent)");
                var interfaces = HidFanController.DiagnoseLgInterfaces();
                if (interfaces.Count == 0)
                {
                    Console.WriteLine("No LG HID interfaces are connected.");
                    return 1;
                }
                foreach (var entry in interfaces)
                {
                    Console.WriteLine($"Interface: {entry.Interface}; product: {entry.Product ?? "unknown"}");
                    if (entry.Identity is { } id)
                        Console.WriteLine($"VID={id.VendorId:X4} PID={id.ProductId:X4} Revision={id.Version:X4} Usage={id.UsagePage:X4}/{id.Usage:X4} Reports(in/out/feature)={id.InputReportLength}/{id.OutputReportLength}/{id.FeatureReportLength}");
                    if (entry.Error is not null)
                        Console.WriteLine(entry.Error);
                }
                return 0;
            }

            if (args.Length == 1 && args[0] == "--read")
            {
                await using var controller = HidFanController.Connect();
                var status = await controller.ReadStatusAsync(CancellationToken.None);
                Console.WriteLine(status.CurrentDuty.ToString(CultureInfo.InvariantCulture));
                return 0;
            }

            if (args.Length == 2 && args[0] == "--set" &&
                int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var duty) &&
                duty is >= FanDutyLimits.Minimum and <= FanDutyLimits.Maximum)
            {
                await using var controller = HidFanController.Connect();
                var current = await controller.ReadStatusAsync(CancellationToken.None);
                if (!current.IsInTestedRange)
                    throw new FanToolException("The current controller state is outside the tested range. Nothing was changed.");

                // Save first: a running tray app with automatic correction would otherwise restore
                // its previous target while this command waits for the readback.
                var store = new AppSettingsStore();
                var settings = store.Load();
                settings.TargetDuty = duty;
                store.Save(settings);

                await controller.SetDutyAsync((byte)duty, CancellationToken.None);
                var readback = await controller.ReadBackDutyAsync((byte)duty, CancellationToken.None);
                if (!readback.Confirms(duty))
                {
                    Console.Error.WriteLine(
                        $"Sent target {duty}%; readback is {readback.CurrentDuty}% (mode {readback.Mode}).");
                    return 1;
                }

                Console.WriteLine(readback.CurrentDuty.ToString(CultureInfo.InvariantCulture));
                return 0;
            }

            Console.Error.WriteLine("Invalid command or fan setting. Use --help for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ErrorText.Describe(ex));
            return 1;
        }
    }

    private static bool ValidSessionEvent(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(value[prefix.Length..], "N", out _);

    private static void PrintUsage()
    {
        Console.WriteLine("Monitor Fan Control command line");
        Console.WriteLine("  MonitorFanControl.exe --test39-menu-context-audio  One Yeti X PCM-recorded menu-only A/B/C cycle; automatic1/close cleanup, frozen numeric baseline gate, no human observations or physical OSD confirmation.");
        Console.WriteLine("  MonitorFanControl.exe --test39-short-ramp-audio <osd-admission.json>  One qualified short pulse; requires an explicit OSD admission file (current human observation or standing user instruction) before capture/worker/HID. Physical outcome and permanent quiet remain unconfirmed.");
        Console.WriteLine("  MonitorFanControl.exe --test39-short-control-audio <osd-admission.json>  One matched NoReductionControl acquisition with a passive arm+384ms slot; native mode2 is forbidden. Exact argument pair and explicit OSD admission file (current human observation or standing user instruction) required before capture/worker/HID.");
        Console.WriteLine("  MonitorFanControl.exe --test39-closed-hold-audio <osd-admission.json>  One separately qualified mode2/mode0/close acoustic retention probe; ramp native admission2400ms, hold10000ms from native mode0 start, original guardian-ready outer22000ms. Exact argument pair and explicit OSD admission required before capture/worker/HID; lasting quiet and fresh protection remain unverified.");
        Console.WriteLine("  MonitorFanControl.exe --test39-contextual-hold-audio <osd-admission.json>  Separate bounded hold candidate with complete contextual Auto recovery; original ramp2400ms/hold<=10000ms,20s cached recovery observation, original guardian-ready outer75000ms. Explicit OSD admission and exact qualified profile required; native submissions/cache are not physical Auto or protection.");
        Console.WriteLine("  MonitorFanControl.exe --test39-menu-context  Explicit menu-only A/B/C5/5/5s listening comparison, automatic1 only; independent bounded cleanup. Requires a ready listener and audible target noise. No LG Switch actions.");
        Console.WriteLine("  MonitorFanControl.exe --diagnose   Inspect LG USB descriptors without sending monitor commands.");
        Console.WriteLine("  MonitorFanControl.exe --observe39  Read a standard HID feature report, then listen for incoming reports for 15 seconds (39GX950B candidate; no output reports).");
        Console.WriteLine("  MonitorFanControl.exe --observe39-i2c  Read one standard HID-I2C feature report only; no output reports.");
        Console.WriteLine("  MonitorFanControl.exe --probe39-i2c-bridge  Verify the Renesas1.11 USB-I2C bridge with a brightness GET. Close LG Switch first.");
        Console.WriteLine("  MonitorFanControl.exe --probe39-i2c-factory  Also read native fan RPM/range/target and fixed factory diagnostic getters; no setting commands.");
        Console.WriteLine("  MonitorFanControl.exe --test39-native-fan   At most3 seconds of native fan ramp-down, then auto restore; independent8-second restore worker. Close LG Switch first.");
        Console.WriteLine("  MonitorFanControl.exe --lg39-control       Experimental manual On/Off window; no service OSD and no LGSwitch launch. No live RPM/temperature verification.");
        Console.WriteLine("  MonitorFanControl.exe --test39-native-hold  At most2.4 seconds ramp-down,10 seconds mode0,3 seconds post-resume sampling; independent22-second auto restore. Experimental, no permanent setting.");
        Console.WriteLine("  MonitorFanControl.exe --restore39-native-auto  Send native automatic fan mode1 and close the service window. Close LG Switch first.");
        Console.WriteLine("  MonitorFanControl.exe --probe39    Try one legacy status request on the 32-byte 39GX950B candidate interface. Fan-setting commands stay disabled.");
        Console.WriteLine("  MonitorFanControl.exe --probe39-feature  Send the same status query through the 64-byte HID feature channel.");
        Console.WriteLine("  MonitorFanControl.exe --probe39-identity  Try the reference MCU identity read commands C3 and D1.");
        Console.WriteLine("  MonitorFanControl.exe --probe39-read C4  Send one reference read opcode; only C3/C4/C6/CB/CC/CE/D1/DE are accepted.");
        Console.WriteLine("  MonitorFanControl.exe --read       Print the current fan duty (25–100).\n  MonitorFanControl.exe --set 45     Set the target and print the verified readback.");
        Console.WriteLine("  MonitorFanControl.exe --help       Show this help.");
        Console.WriteLine("--set saves the target used by the tray app. CLI commands exit without opening the window or tray icon.");
    }

    private static void AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentConsole))
            return;

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
