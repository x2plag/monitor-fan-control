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
        AttachToParentConsole();
        var args = allArgs.Where(argument => argument != "--background").ToArray();

        if (args.Length == 1 && args[0] == "--help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
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

    private static void PrintUsage()
    {
        Console.WriteLine("Monitor Fan Control command line");
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
