using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitorFanControl;

// Parent-only observation. Faulted is published after FinishAsync/DisposeAsync;
// the window queues its existing UI update before reaching this optional IO.
internal sealed class PersistentQuietFaultCapture
{
    private const string Variable = "LG_QUIET_FAULT_STATUS_DIRECTORY";
    private const string Root = @"A:\ChatGPT\LG\.scratch\lg-reference-five-second\diagnostics";
    private int _attempted;

    public void TryCapture(PersistentQuietStatus status)
    {
        if (status.State != PersistentQuietState.Faulted) return;
        try
        {
            var configured = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(configured)) return; // Normal packages perform no filesystem IO.
            if (Interlocked.Exchange(ref _attempted, 1) != 0) return; // One bounded fault, no append/retry loop.
            if (!Path.IsPathFullyQualified(configured)) return;
            var directory = Path.GetFullPath(configured);
            if (!string.Equals(Path.GetDirectoryName(directory), Root, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(Path.GetFileName(directory), @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z")) return;
            // Refuse existing destinations and redirected ancestors; never overwrite a prior capture.
            if (!PlainDirectoryTree(Root) || Directory.Exists(directory) || File.Exists(directory)) return;
            Directory.CreateDirectory(directory);
            if (!PlainDirectoryTree(directory)) return;
            var assembly = typeof(PersistentQuietFaultCapture).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unavailable";
            if (version.Length > 160 || !Regex.IsMatch(version, @"\A[A-Za-z0-9.+_-]+\z")) version = "unavailable";
            var record = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1, CapturedAtUtc = DateTimeOffset.UtcNow, State = "Faulted", Detail = Redact(status.Detail),
                ProcessId = Environment.ProcessId, Version = version, Profile = ShortRampSoftwareProfile.ReferencePersistentQuiet.Id
            });
            if (record.Length > 8192) return;
            using var file = new FileStream(Path.Combine(directory, "fault-status.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(record);
        }
        catch { } // Observation failure cannot affect status, UI dispatch, control or recovery.
    }

    private static bool PlainDirectoryTree(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static string Redact(string detail)
    {
        // Exception.Message only: discard subsequent lines rather than retain unknown stack paths.
        var lineEnd = detail.IndexOfAny(['\r', '\n']);
        var text = detail[..Math.Min(lineEnd < 0 ? detail.Length : lineEnd, 4096)];
        text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\|(?<![A-Za-z0-9])/(?![ /])|\bhid#)[^;]*", "[redacted-path]");
        text = Regex.Replace(text, @"(?i)\b(?:device(?:path|id)?|serial(?:number)?|payload|packet|report|frame|bytes|data)\s*[:=]\s*[^;]*", "[redacted-data]");
        text = Regex.Replace(text, @"\b(?:0x)?[A-Fa-f0-9]{12,}\b|\b(?:[A-Fa-f0-9]{2}[ -]){4,}[A-Fa-f0-9]{2}\b", "[redacted-data]");
        text = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        return text.Length <= 1024 ? text : text[..1024];
    }
}
