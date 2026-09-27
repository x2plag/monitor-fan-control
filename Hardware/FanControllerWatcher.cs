using System.Runtime.InteropServices;

namespace MonitorFanControl;

// Reports when the fan controller's HID interface appears or disappears, e.g. after a KVM switch,
// so the app can reconnect at once instead of waiting for the next poll. If registration fails,
// the app still finds the controller by polling.
internal sealed class FanControllerWatcher : IDisposable
{
    private const int FilterTypeDeviceInterface = 0;
    private const int ActionInterfaceArrival = 0;
    private const int ActionInterfaceRemoval = 1;
    private const int SymbolicLinkOffset = 24;

    // Held in a field so the delegate outlives the unmanaged registration.
    private readonly Native.NotifyCallback _callback;
    private IntPtr _registration;

    // Raised on a thread-pool thread; the argument is true when the controller arrived.
    public event Action<bool>? Changed;

    public FanControllerWatcher()
    {
        _callback = OnNotification;
        var filter = new Native.NotifyFilter
        {
            Size = (uint)Marshal.SizeOf<Native.NotifyFilter>(),
            FilterType = FilterTypeDeviceInterface,
            ClassGuid = HidFanController.HidInterfaceGuid
        };
        if (Native.CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out var registration) == 0)
            _registration = registration;
    }

    private int OnNotification(IntPtr notification, IntPtr context, int action, IntPtr eventData, int eventDataSize)
    {
        // An exception must not escape into the unmanaged caller.
        try
        {
            if (action is ActionInterfaceArrival or ActionInterfaceRemoval &&
                eventDataSize > SymbolicLinkOffset &&
                HidFanController.IsFanInterfacePath(Marshal.PtrToStringUni(eventData + SymbolicLinkOffset)))
                Changed?.Invoke(action == ActionInterfaceArrival);
        }
        catch (Exception)
        {
        }

        return 0;
    }

    public void Dispose()
    {
        if (_registration == IntPtr.Zero)
            return;

        Native.CM_Unregister_Notification(_registration);
        _registration = IntPtr.Zero;
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int NotifyCallback(IntPtr notification, IntPtr context, int action,
            IntPtr eventData, int eventDataSize);

        // CM_NOTIFY_FILTER: four DWORDs, then a union whose largest member is WCHAR[200].
        [StructLayout(LayoutKind.Explicit, Size = 416)]
        internal struct NotifyFilter
        {
            [FieldOffset(0)] internal uint Size;
            [FieldOffset(4)] internal uint Flags;
            [FieldOffset(8)] internal int FilterType;
            [FieldOffset(12)] internal uint Reserved;
            [FieldOffset(16)] internal Guid ClassGuid;
        }

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification")]
        internal static extern int CM_Register_Notification(ref NotifyFilter filter, IntPtr context,
            NotifyCallback callback, out IntPtr notification);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
        internal static extern int CM_Unregister_Notification(IntPtr notification);
    }
}
