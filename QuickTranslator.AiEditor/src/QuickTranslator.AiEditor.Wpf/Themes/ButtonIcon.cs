using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// A Segoe Fluent Icons glyph shown before a button's content (<c>ui:ButtonIcon.Glyph="&amp;#xE768;"</c>).
/// The button's Content stays plain text, so code and tests that read it keep working.
/// </summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ButtonIcon), new FrameworkPropertyMetadata(null));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);
}

/// <summary>Segoe Fluent Icons code points used across the app (Segoe MDL2 Assets has the same ones on Windows 10).</summary>
internal static class Glyphs
{
    internal const string Translate = "", Chapters = "", Import = "", Queue = "", Library = "",
        Publish = "", Read = "", Settings = "", Play = "", Pause = "", Stop = "",
        Accept = "", Warning = "", Error = "", Info = "", Question = "", Add = "",
        Delete = "", Undo = "", Refresh = "", Save = "", Back = "", Forward = "",
        Search = "", Contact = "", More = "", Sidebar = "", Font = "", Edit = "";
}

/// <summary>Marks the navigation button of the workspace currently shown (NavigationButton style highlights it).</summary>
public static class NavState
{
    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsCollapsed", typeof(bool), typeof(NavState), new FrameworkPropertyMetadata(false));

    public static bool GetIsCollapsed(DependencyObject element) => (bool)element.GetValue(IsCollapsedProperty);
    public static void SetIsCollapsed(DependencyObject element, bool value) => element.SetValue(IsCollapsedProperty, value);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(NavState), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
