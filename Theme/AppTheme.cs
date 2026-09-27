using Microsoft.Win32;
using System.Windows;

namespace MonitorFanControl;

internal static class AppTheme
{
    private const string PersonalizationKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizationKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
    }

    public static void Apply(bool isDark)
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Theme/{(isDark ? "Dark" : "Light")}.xaml")
        };
        // Replace only the theme dictionary so other merged dictionaries survive a theme change.
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var index = dictionaries.ToList().FindIndex(IsThemeDictionary);
        if (index >= 0)
            dictionaries[index] = theme;
        else
            dictionaries.Add(theme);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.Contains("Theme/", StringComparison.OrdinalIgnoreCase) == true;
}
