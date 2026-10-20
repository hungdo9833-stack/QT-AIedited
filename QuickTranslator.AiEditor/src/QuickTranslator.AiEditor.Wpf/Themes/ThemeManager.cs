using System.Windows;
using System.IO;
using System.Windows.Media;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Applies the Dark/Light palette. Colors live in Themes/Colors.*.xaml, keyed by the brush keys of Brushes.xaml.
/// Each brush is replaced, not recolored: WPF freezes brushes that style and template triggers use, so an instance
/// cannot be recolored in place. Everything that must follow the theme therefore refers to the key (DynamicResource
/// in XAML, SetResourceReference in code); a brush taken with FindResource keeps the colors of the theme it was taken in.
/// </summary>
internal static class ThemeManager
{
    /// <summary>The palette on screen: always Dark or Light, also when the user chose "Theo Windows".</summary>
    internal static ShellTheme Current { get; private set; } = ShellTheme.Dark;

    /// <summary>Dark or Light for a choice; System reads Windows' "app mode" (AppsUseLightTheme), Dark if it cannot be read.</summary>
    internal static ShellTheme Resolve(ShellTheme theme)
    {
        if (theme != ShellTheme.System) return theme;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light != 0 ? ShellTheme.Light : ShellTheme.Dark;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ShellTheme.Dark; }
    }

    internal static void Apply(ShellTheme theme)
    {
        theme = Resolve(theme);
        ResourceDictionary resources = Application.Current.Resources;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/QuickTranslator.AiEditor;component/Themes/Colors.{theme}.xaml", UriKind.Absolute)
        };
        foreach (object key in palette.Keys)
        {
            if (palette[key] is Color color) resources[key] = new SolidColorBrush(color);
        }
        Current = theme;
    }
}
