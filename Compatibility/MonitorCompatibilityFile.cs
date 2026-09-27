using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MonitorFanControl;

internal static class MonitorCompatibilityFile
{
    private const string FileName = "monitor-compatibility.json";
    private const string EmbeddedResourceName = "MonitorFanControl.monitor-compatibility.json";
    private static readonly Lazy<CompatibilityData> Data = new(Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool SupportsFirmwareVersion(ushort version) =>
        Data.Value.SupportedFirmwareVersions.Contains(version);

    public static bool TryGetKnownModelName(string hardwareId, out string modelName)
    {
        try
        {
            return Data.Value.KnownModelNames.TryGetValue(hardwareId, out modelName!);
        }
        catch (FanToolException)
        {
            modelName = string.Empty;
            return false;
        }
    }

    private static CompatibilityData Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        try
        {
            using var stream = OpenCompatibilityFileOrDefault(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new FanToolException($"'{FileName}' uses an unsupported schema version.");

            var profile = root.GetProperty("protocolProfile");
            ValidateHex(profile, "usbVendorId", 0x043E);
            ValidateHex(profile, "usbProductId", 0x9A8A);
            ValidateHex(profile, "hidUsagePage", 0xFF01);
            ValidateHex(profile, "hidUsage", 0x0001);
            ValidateLength(profile, "inputReportLength", 65);
            ValidateLength(profile, "outputReportLength", 65);
            ValidateLength(profile, "featureReportLength", 65);

            var versions = new HashSet<ushort>();
            foreach (var entry in profile.GetProperty("supportedFirmwareVersions").EnumerateArray())
            {
                var text = entry.GetString();
                if (text is null || text.Length != 4 ||
                    !ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var version))
                    throw new FanToolException($"'{FileName}' contains an invalid firmware version. Use four hexadecimal digits, such as 0202.");
                versions.Add(version);
            }

            if (versions.Count == 0)
                throw new FanToolException($"'{FileName}' does not list any supported firmware versions.");

            var modelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in root.GetProperty("windowsModelNames").EnumerateArray())
            {
                var hardwareId = entry.GetProperty("hardwareId").GetString()?.Trim();
                var displayName = entry.GetProperty("displayName").GetString()?.Trim();
                if (string.IsNullOrEmpty(hardwareId) || string.IsNullOrEmpty(displayName))
                    throw new FanToolException($"'{FileName}' contains an empty Windows model-name mapping.");
                modelNames[hardwareId] = displayName;
            }

            return new CompatibilityData(versions, modelNames);
        }
        catch (FanToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FanToolException($"Could not read '{FileName}' next to the app ({ex.GetType().Name}). No fan command was sent.");
        }
    }

    private static Stream OpenCompatibilityFileOrDefault(string path)
    {
        if (File.Exists(path))
            return File.OpenRead(path);

        byte[] defaults;
        using (var resource = typeof(MonitorCompatibilityFile).Assembly
                   .GetManifestResourceStream(EmbeddedResourceName)
               ?? throw new InvalidOperationException("The embedded compatibility file is missing."))
        using (var buffer = new MemoryStream())
        {
            resource.CopyTo(buffer);
            defaults = buffer.ToArray();
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, defaults);
            File.Move(temporaryPath, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another app instance may have created the file after the first check.
        }
        catch (IOException)
        {
            // The app folder may not be writable; use the embedded copy for this run.
        }
        catch (UnauthorizedAccessException)
        {
            // The app folder may not be writable; use the embedded copy for this run.
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return File.Exists(path)
            ? File.OpenRead(path)
            : new MemoryStream(defaults, writable: false);
    }

    private static void ValidateHex(JsonElement profile, string property, ushort expected)
    {
        var text = profile.GetProperty(property).GetString();
        if (text is null || text.Length != 4 ||
            !ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ||
            value != expected)
            throw new FanToolException($"'{FileName}' does not match the controller protocol built into this app.");
    }

    private static void ValidateLength(JsonElement profile, string property, int expected)
    {
        if (profile.GetProperty(property).GetInt32() != expected)
            throw new FanToolException($"'{FileName}' does not match the controller protocol built into this app.");
    }

    private sealed record CompatibilityData(
        HashSet<ushort> SupportedFirmwareVersions,
        Dictionary<string, string> KnownModelNames);
}
