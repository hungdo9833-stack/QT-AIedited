using System.Windows;
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
    internal static ShellTheme Current { get; private set; } = ShellTheme.Dark;

    internal static void Apply(ShellTheme theme)
    {
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
