using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorFanControl;

internal static class MonitorIdentityDetector
{
    private static readonly Guid MonitorClassGuid = new("4D36E96E-E325-11CE-BFC1-08002BE10318");
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const int ErrorNoMoreItems = 259;

    public static string GetDisplayDescription()
    {
        try
        {
            return ReadDisplayDescription();
        }
        catch (Exception)
        {
            return "Detected display: unavailable";
        }
    }

    private static string ReadDisplayDescription()
    {
        var classGuid = MonitorClassGuid;
        var deviceSet = Native.SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (deviceSet == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var lgDisplays = new List<(string Text, bool IsKnownModel)>();
            for (uint index = 0; ; index++)
            {
                var device = new Native.SpDevinfoData
                {
                    Size = (uint)Marshal.SizeOf<Native.SpDevinfoData>()
                };

                if (!Native.SetupDiEnumDeviceInfo(deviceSet, index, ref device))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    throw new Win32Exception(error);
                }

                var instanceId = GetInstanceId(deviceSet, ref device);
                if (!instanceId.StartsWith("DISPLAY\\GSM", StringComparison.OrdinalIgnoreCase))
                    continue;

                var modelId = instanceId.Split('\\', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                var friendlyName = ReadDeviceProperty(deviceSet, ref device, SpdrpFriendlyName)
                    ?? ReadDeviceProperty(deviceSet, ref device, SpdrpDeviceDesc)
                    ?? "LG monitor";
                if (string.IsNullOrWhiteSpace(friendlyName))
                    friendlyName = "LG monitor";
                friendlyName = CleanFriendlyName(friendlyName);
                string? knownModel = null;
                var isKnownModel = modelId is not null &&
                                   MonitorCompatibilityFile.TryGetKnownModelName(modelId, out knownModel);
                var displayName = isKnownModel ? knownModel! : friendlyName;
                lgDisplays.Add((modelId is null ? displayName : $"{displayName} ({modelId})", isKnownModel));
            }

            return lgDisplays.Count switch
            {
                0 => "Detected display: no LG model reported by Windows",
                1 => $"Detected display: {lgDisplays[0].Text}",
                _ => DescribeSeveral(lgDisplays)
            };
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    // Windows does not link a display to the USB fan controller. Several LG displays are normal
    // (only one needs USB), so name the one known fan model, or list them all.
    private static string DescribeSeveral(List<(string Text, bool IsKnownModel)> displays)
    {
        var known = displays.Where(display => display.IsKnownModel).ToList();
        if (known.Count != 1)
            return $"Detected displays: {string.Join(", ", displays.Select(display => display.Text))}";

        var others = displays.Count - 1;
        return $"Detected display: {known[0].Text}, plus {others} other LG monitor{(others == 1 ? "" : "s")}";
    }

    private static string GetInstanceId(IntPtr deviceSet, ref Native.SpDevinfoData device)
    {
        var instanceId = new StringBuilder(512);
        if (!Native.SetupDiGetDeviceInstanceIdW(deviceSet, ref device, instanceId,
                instanceId.Capacity, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return instanceId.ToString();
    }

    private static string? ReadDeviceProperty(IntPtr deviceSet, ref Native.SpDevinfoData device, uint property)
    {
        var buffer = new byte[1024];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(deviceSet, ref device, property,
                out _, buffer, (uint)buffer.Length, out var requiredSize))
            return null;

        var byteCount = Math.Min(requiredSize, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, (int)byteCount).TrimEnd('\0').Trim();
    }

    private static string CleanFriendlyName(string name)
    {
        const string genericPrefix = "Generic Monitor (";
        if (name.StartsWith(genericPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(')'))
            name = name[genericPrefix.Length..^1];
        return name;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDevinfoData
        {
            internal uint Size;
            internal Guid ClassGuid;
            internal uint DeviceInstance;
            internal IntPtr Reserved;
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
            IntPtr windowHandle, uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
            ref SpDevinfoData deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData, uint property, out uint propertyRegDataType,
            byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
