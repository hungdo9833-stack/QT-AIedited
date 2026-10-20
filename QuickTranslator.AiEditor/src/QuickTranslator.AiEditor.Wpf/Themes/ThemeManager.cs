using System.Windows;
using System.Windows.Media;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Applies the Dark/Light palette. Colors live in Themes/Colors.*.xaml, keyed by the brush keys of Brushes.xaml.
/// Each brush is recolored in place rather than replaced: code that took a brush with FindResource keeps the same
/// instance, so it follows the theme like every DynamicResource reference does.
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
            if (palette[key] is not Color color) continue;
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
        Current = theme;
    }
}
