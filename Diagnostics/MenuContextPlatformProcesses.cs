using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MonitorFanControl;

internal sealed record MonitorProcessEntry(int Pid, string Name, string? CommandLine);

internal static class MenuContextPlatformProcesses
{
    private const string Controllers = "(?i)(monitorfancontrol|lg.*switch|switch.*lg|firmware|fwupdate|onscreencontrol|on.screen.control)";
    private const string Diagnostics = "(?i)(lg-firmware-analysis|nvapi_ddc|usb_native_fan|usb_i2c|--(?:test39|restore39|manual39|probe39)|MonitorFanControl)";

    // WMI can stall independently of the monitor. Inspect local processes and
    // relevant command lines directly. Existing conflict checks/locks remain.
    internal static Task EnsureCompatibleAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= 3000)
                    throw new MenuPreconditionException("Local monitor process inspection exceeded 3 seconds; no diagnostic started.");
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var name = process.ProcessName;
                    var controller = Regex.IsMatch(name, Controllers);
                    if (!controller && !Regex.IsMatch(name, "(?i)^(python|pwsh|powershell)")) continue;
                    Validate(new(process.Id, name, controller ? null : ReadCommandLine(process)), Environment.ProcessId);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                catch (Win32Exception)
                {
                    throw new MenuPreconditionException($"A diagnostic-capable process could not be inspected (PID {process.Id}); no diagnostic started.");
                }
            }
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static void Validate(MonitorProcessEntry row, int selfPid)
    {
        if (row.Pid == selfPid) return;
        if (Regex.IsMatch(row.Name, Controllers))
            throw new MenuPreconditionException($"An incompatible monitor control or updater process is active (PID {row.Pid}); it was left running.");
        if (!Regex.IsMatch(row.Name, "(?i)^(python|pwsh|powershell)")) return;
        if (row.CommandLine is null)
            throw new MenuPreconditionException($"A diagnostic-capable process could not be inspected (PID {row.Pid}); no diagnostic started.");
        if (Regex.IsMatch(row.CommandLine, Diagnostics))
            throw new MenuPreconditionException($"An existing monitor diagnostic or delayed watchdog is active (PID {row.Pid}); it was left running.");
    }

    internal static string ReadCommandLine(Process process)
    {
        // Windows 8.1+ ProcessCommandLineInformation returns a bounded local
        // UNICODE_STRING. Query-only handle; no VM writes/debug privileges.
        const int capacity = 65552;
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var status = NtQueryInformationProcess(handle, 60, buffer, capacity, out var returned);
            if (status < 0 || returned < (IntPtr.Size == 8 ? 16 : 8) || returned > capacity)
                throw new Win32Exception("Local process command line could not be read.");
            var length = (ushort)Marshal.ReadInt16(buffer);
            var maximum = (ushort)Marshal.ReadInt16(buffer, 2);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            var offset = text.ToInt64() - buffer.ToInt64();
            if (length > maximum || (length & 1) != 0 || offset < (IntPtr.Size == 8 ? 16 : 8) || offset > returned || length > returned - offset)
                throw new Win32Exception("Local process command line has an invalid length.");
            return Marshal.PtrToStringUni(text, length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
