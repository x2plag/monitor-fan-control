using System.IO;

namespace MonitorFanControl;

// Actual short native Stream seam. The caller owns the original absolute IO
// cancellation budget; this helper never creates or renews a timeout.
internal static class MenuNativeStreamTransfer
{
    internal static byte[] Execute(Stream stream, byte[] report, bool reply, CancellationToken token, bool contextualGet4 = false)
    {
        stream.WriteAsync(report, token).AsTask().GetAwaiter().GetResult();
        if (!reply) return [];
        while (true)
        {
            if (contextualGet4) token.ThrowIfCancellationRequested();
            var bytes = new byte[65];
            var count = stream.ReadAsync(bytes, token).AsTask().GetAwaiter().GetResult();
            if (count != bytes.Length) throw new InvalidDataException("Incomplete short bridge response.");
            if (!contextualGet4 || bytes[2] == report[2]) return bytes;
            // Buffered replies from another transaction are consumed, never
            // returned as GET4 data. Only a valid successful bridge envelope
            // can be skipped. The same original token bounds every read.
            if (bytes[0] != 0 || bytes[1] is < 4 or > 64 || bytes[3] != 0x39 || bytes[4] != 0)
                throw new InvalidDataException("Malformed foreign contextual bridge response.");
        }
    }
}
