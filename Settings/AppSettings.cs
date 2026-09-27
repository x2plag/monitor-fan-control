using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace MonitorFanControl;

internal static class FanDutyLimits
{
    public const byte Minimum = 25;
    public const byte Maximum = 100;
}

internal sealed class AppSettings
{
    public int? TargetDuty { get; set; }
    public bool KeepTargetApplied { get; set; } = true;

    public AppSettings Clone() => new() { TargetDuty = TargetDuty, KeepTargetApplied = KeepTargetApplied };
}

// Callers load, change one field and save the whole object. A transient read failure must
// therefore return the last known settings, never defaults that would then be written back.
internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly object _sync = new();
    private AppSettings? _cached;
    private DateTime _cachedWriteTimeUtc;

    public AppSettingsStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(localAppData, "MonitorFanControl", "settings.json");
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            // Rereads only after the file changes, e.g. when the CLI saves a new target.
            DateTime writeTimeUtc;
            try
            {
                writeTimeUtc = File.GetLastWriteTimeUtc(_settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (_cached ?? new AppSettings()).Clone();
            }

            if (_cached is null || writeTimeUtc != _cachedWriteTimeUtc)
            {
                var settings = TryRead();
                if (settings is not null)
                {
                    _cached = settings;
                    _cachedWriteTimeUtc = writeTimeUtc;
                }
            }

            return (_cached ?? new AppSettings()).Clone();
        }
    }

    public void Save(AppSettings settings)
    {
        if (settings.TargetDuty is < FanDutyLimits.Minimum or > FanDutyLimits.Maximum)
            throw new ArgumentOutOfRangeException(nameof(settings), "The saved fan setting must be between 25 and 100.");

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temporaryPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            _cached = settings.Clone();
            _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_settingsPath);
        }
    }

    // Returns null when the file exists but cannot be read right now, e.g. while another
    // process replaces it. A missing or corrupt file yields defaults.
    private AppSettings? TryRead()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                    return new AppSettings();

                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                    ?? new AppSettings();
                if (settings.TargetDuty is < FanDutyLimits.Minimum or > FanDutyLimits.Maximum)
                    settings.TargetDuty = null;

                return settings;
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                    return null;
                Thread.Sleep(50);
            }
        }
    }
}

internal static class WindowsStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorFanControl";

    // An entry left by a copy in another folder does not start this copy, so it does not count.
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string command &&
               string.Equals(command, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The current user's startup registry key could not be opened.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var command = StartupCommand()
            ?? throw new InvalidOperationException("The application executable path could not be determined.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string? StartupCommand() =>
        Environment.ProcessPath is { } executablePath ? $"\"{executablePath}\" --background" : null;
}
