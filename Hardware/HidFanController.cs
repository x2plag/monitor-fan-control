using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorFanControl;

internal sealed record FanStatus(
    byte SensorValue,
    byte CurrentDuty,
    byte DerivedValue,
    byte Mode,
    byte GpioValue,
    byte FirmwareConstant)
{
    // A write sets current to target - 1 until the next control step, so a 25% target briefly reads 24.
    public bool IsInTestedRange =>
        CurrentDuty is >= FanDutyLimits.Minimum - 1 and <= FanDutyLimits.Maximum &&
        Mode <= 2 && FirmwareConstant == 9;

    public bool Confirms(int duty) => CurrentDuty == duty && Mode == 1;
}

internal sealed record DeviceIdentity(ushort VendorId, ushort ProductId, ushort Version,
    ushort UsagePage, ushort Usage, ushort InputReportLength, ushort OutputReportLength,
    ushort FeatureReportLength);

internal sealed record HidInterfaceDiagnostic(string Interface, string? Product,
    DeviceIdentity? Identity, string? Error);

internal sealed partial class HidFanController : IAsyncDisposable
{
    private const ushort ExpectedVid = 0x043E;
    private const ushort ExpectedPid = 0x9A8A;
    private const ushort ExpectedUsagePage = 0xFF01;
    private const ushort ExpectedUsage = 0x0001;
    private const int HidReportLength = 64;
    private const byte ReportId = 0;
    private const byte GetFanStatus = 0xDE;
    private const byte SetFanDuty = 0xDF;
    private static readonly TimeSpan ReadbackInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(5);
    private const string ConnectionLostMessage =
        "The USB connection to the monitor's fan controller was lost. The app reconnects automatically when it is available again.";
    internal static readonly Guid HidInterfaceGuid = GetHidGuid();
    private static readonly Semaphore InterprocessExchangeLock =
        new(initialCount: 1, maximumCount: 1, name: @"Local\MonitorFanControl.HidExchange");

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly int _reportLength;
    private readonly bool _allowWrites;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    // Each connection starts at a random tag so replies meant for another handle are unlikely to match.
    private byte _nextTag = (byte)Random.Shared.Next(1, 256);
    private bool _disposed;

    public DeviceIdentity Identity { get; }

    private HidFanController(SafeFileHandle handle, DeviceIdentity identity, bool allowWrites = true)
    {
        _handle = handle;
        Identity = identity;
        _reportLength = identity.OutputReportLength - 1;
        _allowWrites = allowWrites;
        // Windows HID adds a one-byte Report ID to each 64-byte firmware report.
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
    }

    internal static bool IsFanInterfacePath(string? path) =>
        path?.Contains("vid_043e&pid_9a8a&mi_01", StringComparison.OrdinalIgnoreCase) == true;

    // Descriptor inspection deliberately uses a zero-access handle. It sends no
    // HID requests and does not relax the fan controller's connection policy.
    public static IReadOnlyList<HidInterfaceDiagnostic> DiagnoseLgInterfaces()
    {
        var result = new List<HidInterfaceDiagnostic>();
        foreach (var path in EnumerateHidPaths().Where(path =>
                     path.Contains("vid_043e&", StringComparison.OrdinalIgnoreCase)))
        {
            var part = path.Split('#').FirstOrDefault(part =>
                part.Contains("vid_043e&", StringComparison.OrdinalIgnoreCase)) ?? "LG HID";
            using var handle = Native.CreateFileW(path, 0,
                Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
                Native.OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                result.Add(new(part, null, null, $"Open error {Marshal.GetLastWin32Error()}"));
                continue;
            }
            var attributes = new Native.HidAttributes { Size = (uint)Marshal.SizeOf<Native.HidAttributes>() };
            if (!Native.HidD_GetAttributes(handle, ref attributes) ||
                !Native.HidD_GetPreparsedData(handle, out var preparsed))
            {
                result.Add(new(part, null, null, $"Descriptor error {Marshal.GetLastWin32Error()}"));
                continue;
            }
            try
            {
                var status = Native.HidP_GetCaps(preparsed, out var caps);
                if (status != 0x00110000)
                {
                    result.Add(new(part, null, null, $"HID caps status 0x{status:X8}"));
                    continue;
                }
                var name = new StringBuilder(128);
                var hasName = Native.HidD_GetProductString(handle, name, name.Capacity * 2);
                result.Add(new(part, hasName ? name.ToString() : null,
                    new DeviceIdentity(attributes.VendorId, attributes.ProductId,
                        attributes.VersionNumber, caps.UsagePage, caps.Usage,
                        caps.InputReportByteLength, caps.OutputReportByteLength,
                        caps.FeatureReportByteLength), null));
            }
            finally
            {
                Native.HidD_FreePreparsedData(preparsed);
            }
        }
        return result;
    }

    public static async Task ObserveLg39Async()
    {
        var paths = EnumerateHidPaths().Where(path => path.Contains(
            "vid_043e&pid_9c33&mi_01", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length != 1)
            throw new FanToolException($"Expected one 39GX950B candidate HID interface; found {paths.Length}.");
        using var handle = Native.CreateFileW(paths[0], Native.GenericRead,
            Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
            throw Native.Error("Opening candidate for input only");
        if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
            throw Native.Error("Reading candidate HID descriptor");
        Native.HidCaps caps;
        try
        {
            if (Native.HidP_GetCaps(preparsed, out caps) != 0x00110000 ||
                caps.UsagePage != 0xFF01 || caps.Usage != 1 ||
                caps.InputReportByteLength != 33 || caps.OutputReportByteLength != 33 ||
                caps.FeatureReportByteLength != 65)
                throw new FanToolException("The candidate descriptor changed; observation stopped.");
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
        var feature = new byte[caps.FeatureReportByteLength];
        // Standard HID GET_REPORT only; no vendor output report is sent.
        if (Native.HidD_GetFeature(handle, feature, feature.Length))
            Console.WriteLine($"HID feature report: {Convert.ToHexString(feature)}");
        else
            Console.WriteLine($"HID feature report unavailable (Windows error {Marshal.GetLastWin32Error()}).");
        Console.WriteLine("Listening to incoming HID reports for 15 seconds; no output reports will be sent.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: true);
        var count = 0;
        try
        {
            while (count < 50)
            {
                var input = new byte[caps.InputReportByteLength];
                var read = await stream.ReadAsync(input, timeout.Token);
                if (read == 0)
                    break;
                Console.WriteLine($"Input {++count}: {Convert.ToHexString(input.AsSpan(0, read))}");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        Console.WriteLine($"Observation complete: {count} incoming reports; no output reports sent.");
    }

    public static void ObserveLg39I2cFeatureReport()
    {
        var paths = EnumerateHidPaths().Where(path => path.Contains(
            "vid_043e&pid_9c33&mi_00", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length != 1)
            throw new FanToolException($"Expected one 39GX950B HID-I2C interface; found {paths.Length}.");

        using var handle = Native.CreateFileW(paths[0], Native.GenericRead,
            Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
            throw Native.Error("Opening the candidate HID-I2C interface for a feature read");
        var attributes = new Native.HidAttributes { Size = (uint)Marshal.SizeOf<Native.HidAttributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes))
            throw Native.Error("Reading HID-I2C identity");
        if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
            throw Native.Error("Reading HID-I2C descriptor");

        Native.HidCaps caps;
        try
        {
            if (Native.HidP_GetCaps(preparsed, out caps) != 0x00110000 ||
                attributes.VendorId != ExpectedVid || attributes.ProductId != 0x9C33 ||
                attributes.VersionNumber != 0x0111 || caps.UsagePage != 0xFF00 || caps.Usage != 1 ||
                caps.InputReportByteLength != 65 || caps.OutputReportByteLength != 65 ||
                caps.FeatureReportByteLength != 65)
                throw new FanToolException("The HID-I2C descriptor does not match the observed 39GX950B profile; no feature request was sent.");
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }

        var report = new byte[caps.FeatureReportByteLength];
        // A standard HID GET_FEATURE only; this path sends no output/feature report.
        if (!Native.HidD_GetFeature(handle, report, report.Length))
            throw Native.Error("Reading the HID-I2C feature report");
        Console.WriteLine($"HID-I2C GET_FEATURE ({report.Length} bytes): {Convert.ToHexString(report)}");
    }

    public static HidFanController Connect() => ConnectCore(candidate39: false);

    // Experimental status-only connection. This never admits the candidate to
    // the normal GUI/--set path, even if a valid status response is received.
    public static HidFanController ConnectLg39StatusProbe() => ConnectCore(candidate39: true);

    private static HidFanController ConnectCore(bool candidate39)
    {
        var allPaths = EnumerateHidPaths();
        var paths = allPaths
            .Where(path => candidate39
                ? path.Contains("vid_043e&pid_9c33&mi_01", StringComparison.OrdinalIgnoreCase)
                : IsFanInterfacePath(path))
            .ToArray();

        if (paths.Length == 0)
        {
            if (!candidate39 && allPaths.Any(path => path.Contains(
                    "vid_043e&pid_9c33&mi_01", StringComparison.OrdinalIgnoreCase)))
                throw new FanToolException("LG 39GX950B USB controller detected (043E:9C33). Fan status is not decoded for this controller. Controls are disabled; use --diagnose for descriptor inspection or --probe39 for an explicit status-only experiment.");
            throw new FanToolException("No supported LG fan controller is connected over USB. Check the USB cable, hub or KVM switch; the app reconnects automatically.");
        }
        // Identical controllers cannot be told apart or linked to a display, so none is controlled.
        if (paths.Length > 1)
            throw new FanToolException($"{paths.Length} monitors with this fan controller are connected over USB. The app cannot tell them apart, so it controls none of them. Connect only one of these monitors over USB.");

        var handle = Native.CreateFileW(paths[0], Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new FanToolException($"The fan interface could not be opened (Windows error {error}). Close software that may be using it and try again.");
        }

        try
        {
            var attributes = new Native.HidAttributes { Size = (uint)Marshal.SizeOf<Native.HidAttributes>() };
            if (!Native.HidD_GetAttributes(handle, ref attributes))
                throw Native.Error("Reading HID identity");

            if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
                throw Native.Error("Reading HID descriptor");

            Native.HidCaps caps;
            try
            {
                var result = Native.HidP_GetCaps(preparsed, out caps);
                if (result != 0x00110000)
                    throw new FanToolException($"The HID descriptor is invalid (status 0x{result:X8}).");
            }
            finally
            {
                Native.HidD_FreePreparsedData(preparsed);
            }

            var identity = new DeviceIdentity(attributes.VendorId, attributes.ProductId,
                attributes.VersionNumber, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength,
                caps.FeatureReportByteLength);

            var expectedPid = candidate39 ? (ushort)0x9C33 : ExpectedPid;
            var expectedIoLength = candidate39 ? 33 : 65;
            var acceptsFirmware = candidate39 ? identity.Version == 0x0111 :
                MonitorCompatibilityFile.SupportsFirmwareVersion(identity.Version);
            if (identity.VendorId != ExpectedVid || identity.ProductId != expectedPid ||
                !acceptsFirmware ||
                identity.UsagePage != ExpectedUsagePage || identity.Usage != ExpectedUsage ||
                identity.InputReportLength != expectedIoLength || identity.OutputReportLength != expectedIoLength ||
                identity.FeatureReportLength != 65)
            {
                throw new FanToolException(
                    $"The USB descriptor does not match a supported fan-controller profile (VID {identity.VendorId:X4}, PID {identity.ProductId:X4}, firmware {identity.Version:X4}, usage {identity.UsagePage:X4}/{identity.Usage:X4}, reports {identity.InputReportLength}/{identity.OutputReportLength}/{identity.FeatureReportLength} bytes). Nothing was changed. See monitor-compatibility.json for accepted firmware versions.");
            }

            return new HidFanController(handle, identity, allowWrites: !candidate39);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async Task<FanStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var payload = await ExchangeAsync(GetFanStatus, ReadOnlyMemory<byte>.Empty, cancellationToken);
        if (!_allowWrites && (payload.Length != 6 || payload[5] != 9))
            throw new FanToolException($"39GX950B replied with a valid SC envelope but an unrecognized status payload ({payload.Length} byte(s): {Convert.ToHexString(payload)}). The legacy fan status requires six bytes with a firmware marker of 09. Fan duty/RPM/temperature cannot be inferred; setting commands remain disabled.");
        if (payload.Length != 6 || payload[5] != 9)
            throw new FanToolException("The status response has an unexpected format. No new setting was sent.");

        return new FanStatus(payload[0], payload[1], payload[2], payload[3], payload[4], payload[5]);
    }

    public byte[] ProbeLg39FeatureStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_allowWrites || Identity.ProductId != 0x9C33 || Identity.FeatureReportLength != 65)
            throw new FanToolException("Feature-status experiments require the exact status-only 39GX950B connection.");
        var tag = _nextTag++;
        var request = new byte[65];
        BuildPacket(GetFanStatus, tag, ReadOnlySpan<byte>.Empty).CopyTo(request, 1);
        // The opcode and payload are the same read-only status query as --probe39;
        // only the advertised standard HID transfer channel differs.
        if (!Native.HidD_SetFeature(_handle, request, request.Length))
            throw Native.Error("Sending the feature-channel status query");
        var reply = new byte[65];
        if (!Native.HidD_GetFeature(_handle, reply, reply.Length))
            throw Native.Error("Reading the feature-channel status reply");
        Console.WriteLine($"Feature-channel raw reply: {Convert.ToHexString(reply)}");
        return ScHidPacket.DecodeReport(reply, GetFanStatus, tag);
    }

    public async Task ProbeLg39ReadCommandAsync(byte command, CancellationToken cancellationToken)
    {
        if (_allowWrites || Identity.ProductId != 0x9C33 || Identity.Version != 0x0111)
            throw new FanToolException("Read-only command probing requires the exact 39GX950B firmware 0111 profile.");
        if (command == GetFanStatus)
        {
            var status = await ReadStatusAsync(cancellationToken);
            Console.WriteLine($"Fan status response: duty={status.CurrentDuty}, mode={status.Mode}, sensorRaw={status.SensorValue}, derivedRaw={status.DerivedValue}, gpioRaw={status.GpioValue}, constant={status.FirmwareConstant}");
            return;
        }
        // These opcodes were statically identified as read handlers in the
        // published sibling-controller firmware. Their meanings still need
        // confirmation on 39GX950B firmware 0111. No write opcode is admitted.
        if (command is not (0xC3 or 0xC4 or 0xC6 or 0xCB or 0xCC or 0xCE or 0xD1))
            throw new FanToolException($"Opcode {command:X2} is not on the reference read-only allowlist.");

        var payload = await ExchangeAsync(command, ReadOnlyMemory<byte>.Empty, cancellationToken);
        Console.WriteLine($"Read-only query {command:X2}: {payload.Length} byte(s), {Convert.ToHexString(payload)}");
    }

    public async Task SetDutyAsync(byte duty, CancellationToken cancellationToken)
    {
        if (!_allowWrites)
            throw new FanToolException("The 39GX950B experimental connection is status-only. Fan-setting commands are disabled.");
        if (duty is < FanDutyLimits.Minimum or > FanDutyLimits.Maximum)
            throw new ArgumentOutOfRangeException(nameof(duty), "The fan setting must be between 25 and 100.");

        var reply = await ExchangeAsync(SetFanDuty, new byte[] { 1, duty }, cancellationToken);
        if (!reply.AsSpan().SequenceEqual(new byte[6]))
            throw new FanToolException("The controller returned an unexpected acknowledgement. The command may have been processed; read the status before applying again.");
    }

    // Right after a write the firmware reports duty - 1 and reaches the duty on a later control step,
    // so keep reading briefly instead of judging the first readback.
    public async Task<FanStatus> ReadBackDutyAsync(byte duty, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReadbackTimeout;
        while (true)
        {
            var status = await ReadStatusAsync(cancellationToken);
            if (status.Confirms(duty) || DateTimeOffset.UtcNow >= deadline)
                return status;

            await Task.Delay(ReadbackInterval, cancellationToken);
        }
    }

    private async Task<byte[]> ExchangeAsync(byte command, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validDutyPayload = command == SetFanDuty && payload.Length == 2 && payload.Span[0] == 1 &&
            (_allowWrites && payload.Span[1] is >= FanDutyLimits.Minimum and <= FanDutyLimits.Maximum);
        if ((command == GetFanStatus && payload.Length != 0) ||
            (command == SetFanDuty && !validDutyPayload) ||
            (command != GetFanStatus && command != SetFanDuty &&
                !(!_allowWrites && command is 0xC3 or 0xC4 or 0xC6 or 0xCB or 0xCC or 0xCE or 0xD1 && payload.Length == 0)))
            throw new InvalidOperationException("The request is outside the supported fan protocol interface.");

        await _exchangeLock.WaitAsync(cancellationToken);
        var ownsInterprocessLock = false;
        try
        {
            var lockResult = await Task.Run(() => WaitHandle.WaitAny(
                new WaitHandle[] { InterprocessExchangeLock, cancellationToken.WaitHandle },
                TimeSpan.FromSeconds(4)));
            if (lockResult == WaitHandle.WaitTimeout)
                throw new TimeoutException("Timed out waiting for another fan control request to finish.");
            if (lockResult == 1)
                cancellationToken.ThrowIfCancellationRequested();
            ownsInterprocessLock = true;

            // Windows copies every input report to every open handle. Drop replies to other
            // processes' requests, or to an earlier timed-out request, before sending this one.
            // This is also the first call to fail on a handle whose device was removed.
            if (!Native.HidD_FlushQueue(_handle))
                throw new FanControllerDisconnectedException(ConnectionLostMessage);

            var tag = _nextTag++;
            if (_nextTag == 0)
                _nextTag = 1;
            var firmwarePacket = BuildPacket(command, tag, payload.Span, _reportLength);

            var outputReport = new byte[_reportLength + 1];
            outputReport[0] = ReportId;
            firmwarePacket.CopyTo(outputReport, 1);

            var inputReport = new byte[_reportLength + 1];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var readTask = _stream.ReadAsync(inputReport.AsMemory(), timeout.Token).AsTask();

            try
            {
                await _stream.WriteAsync(outputReport.AsMemory(), timeout.Token);
                var bytesRead = await readTask;
                if (bytesRead != outputReport.Length)
                    throw new FanToolException($"Incomplete HID response ({bytesRead} of {outputReport.Length} bytes). Read the status again before repeating a command.");
            }
            catch (IOException)
            {
                throw new FanControllerDisconnectedException(ConnectionLostMessage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No complete HID response was received within 4 seconds. The command may have been processed. Read the status first; the app will not repeat the command.");
            }
            finally
            {
                // A failed write leaves the read pending; it would otherwise consume the next reply.
                if (!readTask.IsCompleted)
                {
                    timeout.Cancel();
                    try
                    {
                        await readTask;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            try
            {
                return ScHidPacket.DecodeReport(inputReport, command, tag);
            }
            catch (InvalidDataException ex)
            {
                throw new FanToolException(ex.Message +
                    (_allowWrites ? " Read the status again before repeating a command." :
                        $" Candidate response: {Convert.ToHexString(inputReport)}. No fan-setting command was sent."));
            }
        }
        finally
        {
            if (ownsInterprocessLock)
                InterprocessExchangeLock.Release();
            _exchangeLock.Release();
        }
    }

    private static byte[] BuildPacket(byte command, byte tag, ReadOnlySpan<byte> payload,
        int reportLength = HidReportLength)
    {
        if (reportLength < 11 || payload.Length > reportLength - 8)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var packet = new byte[reportLength];
        packet[0] = 0x53;
        packet[1] = 0x43;
        packet[2] = command;
        packet[3] = tag;
        packet[4] = (byte)payload.Length;
        payload.CopyTo(packet.AsSpan(5));

        byte checksum = 0;
        for (var i = 0; i < 5 + payload.Length; i++)
            checksum ^= packet[i];

        packet[5 + payload.Length] = checksum;
        packet[6 + payload.Length] = 0x45;
        packet[7 + payload.Length] = 0x44;
        return packet;
    }

    private static Guid GetHidGuid()
    {
        Native.HidD_GetHidGuid(out var guid);
        return guid;
    }

    private static IReadOnlyList<string> EnumerateHidPaths()
    {
        var classGuid = HidInterfaceGuid;
        var set = Native.SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == new IntPtr(-1))
            throw Native.Error("Searching USB devices");

        try
        {
            var result = new List<string>();
            for (uint index = 0; ; index++)
            {
                var iface = new Native.SpDeviceInterfaceData
                {
                    Size = (uint)Marshal.SizeOf<Native.SpDeviceInterfaceData>()
                };

                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero,
                        ref classGuid, index, ref iface))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == Native.ErrorNoMoreItems)
                        break;
                    throw new FanToolException($"Enumerating USB devices failed (Windows error {error}).");
                }

                Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0,
                    out var required, IntPtr.Zero);
                var detailBuffer = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, detailBuffer,
                            required, out _, IntPtr.Zero))
                        throw Native.Error("Reading USB device path");
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                    if (!string.IsNullOrWhiteSpace(path))
                        result.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }

            return result;
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _stream.DisposeAsync();
        _handle.Dispose();
        _exchangeLock.Dispose();
    }

    private static class Native
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 1;
        internal const uint FileShareWrite = 2;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint DigcfPresent = 0x00000002;
        internal const uint DigcfDeviceInterface = 0x00000010;
        internal const int ErrorNoMoreItems = 259;

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidAttributes
        {
            internal uint Size;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDeviceInterfaceData
        {
            internal uint Size;
            internal Guid InterfaceClassGuid;
            internal uint Flags;
            internal UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidCaps
        {
            internal ushort Usage;
            internal ushort UsagePage;
            internal ushort InputReportByteLength;
            internal ushort OutputReportByteLength;
            internal ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort NumberLinkCollectionNodes;
            internal ushort NumberInputButtonCaps;
            internal ushort NumberInputValueCaps;
            internal ushort NumberInputDataIndices;
            internal ushort NumberOutputButtonCaps;
            internal ushort NumberOutputValueCaps;
            internal ushort NumberOutputDataIndices;
            internal ushort NumberFeatureButtonCaps;
            internal ushort NumberFeatureValueCaps;
            internal ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll", EntryPoint = "HidD_GetHidGuid")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", EntryPoint = "HidD_GetAttributes", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int reportLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, int reportLength);

        [DllImport("hid.dll", EntryPoint = "HidD_GetPreparsedData", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll", EntryPoint = "HidD_FreePreparsedData", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", EntryPoint = "HidD_FlushQueue", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FlushQueue(SafeFileHandle handle);

        [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps caps);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        internal static FanToolException Error(string operation)
        {
            var error = Marshal.GetLastWin32Error();
            return new FanToolException($"{operation} failed (Windows error {error}).");
        }
    }
}

internal class FanToolException(string message) : Exception(message);

// The device behind an open handle is gone, e.g. after a KVM switch; a fresh connection may work.
internal sealed class FanControllerDisconnectedException(string message) : FanToolException(message);
