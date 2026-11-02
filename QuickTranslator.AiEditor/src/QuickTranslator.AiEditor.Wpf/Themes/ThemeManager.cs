using System.Windows;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Applies the Dark/Light palette. Colors live in Themes/Colors.*.xaml, keyed by the brush keys of Brushes.xaml.
/// Each brush is replaced, not recolored: WPF freezes brushes that style and template triggers use, so an instance
/// cannot be recolored in place. Everything that must follow the theme therefore refers to the key (DynamicResource
/// in XAML, SetResourceReference in code); a brush taken with FindResource keeps the colors of the theme it was taken in.
/// The WPF UI (Fluent) brushes are pointed at the same palette, and every window's title bar follows the theme.
/// </summary>
internal static class ThemeManager
{
    // Fluent (WPF UI) brush key → palette key. Keeps InfoBar/ProgressRing/Card on the app's colors instead of Windows'.
    private static readonly (string Fluent, string Palette)[] FluentBrushes =
    [
        ("ApplicationBackgroundBrush", "AppBackgroundBrush"),
        ("TextFillColorPrimaryBrush", "TextBrush"),
        ("TextFillColorSecondaryBrush", "MutedBrush"),
        ("TextFillColorTertiaryBrush", "SubtleBrush"),
        ("ControlFillColorDefaultBrush", "ControlBrush"),
        ("ControlFillColorSecondaryBrush", "HoverBrush"),
        ("ControlFillColorTertiaryBrush", "PressedBrush"),
        ("ControlStrokeColorDefaultBrush", "BorderStrongBrush"),
        ("CardBackground", "PanelBrush"),
        ("CardBackgroundFillColorDefaultBrush", "PanelBrush"),
        ("CardBorderBrush", "BorderBrush"),
        ("CardStrokeColorDefaultBrush", "BorderBrush"),
        ("CardForeground", "TextBrush"),
        ("DividerStrokeColorDefaultBrush", "BorderBrush"),
        ("SubtleFillColorSecondaryBrush", "HoverBrush"),
        ("SubtleFillColorTertiaryBrush", "PressedBrush"),
        ("KeyboardFocusBorderColorBrush", "FocusBrush"),
        ("InfoBarTitleForeground", "TextBrush"),
        ("InfoBarBorderBrush", "BorderBrush"),
        ("InfoBarInformationalSeverityBackgroundBrush", "PanelAltBrush"),
        ("InfoBarSuccessSeverityBackgroundBrush", "SuccessSubtleBrush"),
        ("InfoBarWarningSeverityBackgroundBrush", "WarningSubtleBrush"),
        ("InfoBarErrorSeverityBackgroundBrush", "DangerSubtleBrush"),
        ("InfoBarInformationalSeverityIconBackground", "AccentTextBrush"),
        ("InfoBarSuccessSeverityIconBackground", "SuccessBrush"),
        ("InfoBarWarningSeverityIconBackground", "WarningBrush"),
        ("InfoBarErrorSeverityIconBackground", "DangerBrush"),
        ("SystemFillColorSuccessBrush", "SuccessBrush"),
        ("SystemFillColorCautionBrush", "WarningBrush"),
        ("SystemFillColorCriticalBrush", "DangerBrush"),
        ("ProgressRingForegroundThemeBrush", "PrimaryBrush"),
        ("AccentFillColorDefaultBrush", "PrimaryBrush"),
        ("AccentTextFillColorPrimaryBrush", "AccentTextBrush"),
    ];

    private static bool _titleBarHookRegistered;

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
        ApplyFluentTheme(theme, palette);
        foreach (object key in palette.Keys)
        {
            if (palette[key] is Color color) resources[key] = new SolidColorBrush(color);
        }
        foreach (var (fluent, paletteKey) in FluentBrushes)
        {
            if (palette[paletteKey] is Color color) resources[fluent] = new SolidColorBrush(color);
        }
        Current = theme;
        RegisterTitleBarHook();
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    private static void ApplyFluentTheme(ShellTheme theme, ResourceDictionary palette)
    {
        var fluent = theme == ShellTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(fluent, WindowBackdropType.None, updateAccent: false);
        if (palette["PrimaryBrush"] is Color accent) ApplicationAccentColorManager.Apply(accent, fluent);
    }

    // Every window, including ones created later, gets a title bar that matches the app theme rather than Windows' app mode.
    private static void RegisterTitleBarHook()
    {
        if (_titleBarHookRegistered) return;
        _titleBarHookRegistered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) ApplyTitleBar(window);
        }));
    }

    private static void ApplyTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = Current == ShellTheme.Dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 2004+ / 11). Older systems ignore it and keep their title bar.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
