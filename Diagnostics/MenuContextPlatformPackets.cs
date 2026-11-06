using System.IO;

namespace MonitorFanControl;

// This is an independent final check at the native boundary, not a replacement
// for the session codec. No caller-selected command/payload can escape it.
internal sealed class MenuFramePolicy(bool cleanupOnly)
{
    private bool _opened;
    internal void Validate(byte[] report, bool reply)
    {
        if (report.Length != 65 || report[0] != 0 || report[3] != 0x39 || report[6] != 0 ||
            report[7] != 0x0b || report[8] != 0x37 || report[2] == 0)
            throw new InvalidDataException("Invalid menu bridge frame.");
        var length = report[1] - 8;
        if (length is < 0 or > 56 || report.AsSpan(9 + length).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid menu bridge length/padding.");
        if (!cleanupOnly && report[4] == 1 && report[5] == 0 && length == 0 && reply) return;
        if (!cleanupOnly && report[4] == 4 && length == 0 && report[5] is 11 or 16 && reply) return;
        if (report[4] != 3 || report[5] != length || reply) throw new InvalidDataException("Menu bridge command is not allowlisted.");
        var packet = report.AsSpan(9, length);
        if (packet.SequenceEqual(new byte[] { 0x4f, 0x84, 3, 0x18, 6, 1, 0xb9 }) ||
            packet.SequenceEqual(new byte[] { 0x4f, 0x85, 3, 2, 0x10, 0x5b, 0, 0xee })) return;
        if (cleanupOnly) throw new InvalidDataException("Cleanup permits only automatic1 and close.");
        if (packet.SequenceEqual(new byte[] { 0x51, 0x82, 1, 0x10, 0xac }) ||
            packet.SequenceEqual(new byte[] { 0x4f, 0x83, 3, 0x18, 3, 0xba }) ||
            packet.SequenceEqual(new byte[] { 0x4f, 0x83, 3, 0x18, 4, 0xbd }) ||
            packet.SequenceEqual(new byte[] { 0x4f, 0x83, 3, 0x18, 5, 0xbc })) return;
        if (packet.SequenceEqual(new byte[] { 0x4f, 0x85, 3, 2, 0xa8, 0, 0, 0x0d }) && !_opened)
        {
            _opened = true;
            return;
        }
        throw new InvalidDataException("Menu DDC payload is not allowlisted.");
    }
}
