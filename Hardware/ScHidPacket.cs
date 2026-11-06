using System.IO;

namespace MonitorFanControl;

// The shared SC envelope is not evidence that a command's payload has the
// same meaning on different monitor firmware. Callers still validate payloads.
internal static class ScHidPacket
{
    public static byte[] DecodeReport(ReadOnlySpan<byte> report, byte command, byte tag)
    {
        if (report.Length is not (33 or 65))
            throw new InvalidDataException("Unexpected HID report size.");
        if (report[0] != 0 || report[1] != 0x52 || report[2] != command || report[3] != tag)
            throw new InvalidDataException("The HID response does not match this request.");
        var length = report[4];
        if (length > report.Length - 6)
            throw new InvalidDataException("The HID response payload length exceeds its report.");
        byte checksum = 0;
        for (var index = 1; index < 5 + length; index++)
            checksum ^= report[index];
        if (checksum != report[5 + length])
            throw new InvalidDataException("The HID response checksum is invalid.");
        return report.Slice(5, length).ToArray();
    }
}
