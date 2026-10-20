using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Drop-in replacement for <see cref="MessageBox"/> that follows the app theme. Same overloads, same results:
/// buttons sit at the bottom right with the affirmative one first; Enter answers the default button, Esc the
/// negative one (No / Cancel / OK); Ctrl+C copies the caption and message like the system dialog does.
/// </summary>
internal sealed class ThemedDialog : Window
{
    /// <summary>Tests answer dialogs here instead of showing them.</summary>
    internal static Func<ThemedDialog, MessageBoxResult>? Responder { get; set; }



    internal MessageBoxResult Result { get; private set; }
    internal string Message { get; }
    internal IReadOnlyList<Button> Buttons { get; }

    internal ThemedDialog(Window? owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        // WPF makes the first window created the application's MainWindow; a dialog shown before the shell exists must not take that role.
        if (Application.Current is { } app && ReferenceEquals(app.MainWindow, this)) app.MainWindow = null;
        Message = message;
        Title = string.IsNullOrEmpty(caption) ? "QT Studio" : caption;
        if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = owner is null;
        MinWidth = 380;
        MaxWidth = 600;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var (results, escape) = buttons switch
        {
            MessageBoxButton.OKCancel => (new[] { MessageBoxResult.OK, MessageBoxResult.Cancel }, MessageBoxResult.Cancel),
            MessageBoxButton.YesNo => (new[] { MessageBoxResult.Yes, MessageBoxResult.No }, MessageBoxResult.No),
            MessageBoxButton.YesNoCancel => (new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel }, MessageBoxResult.Cancel),
            _ => (new[] { MessageBoxResult.OK }, MessageBoxResult.OK)
        };

        Result = escape; // Closing with the title bar's X means the negative answer, as for the system dialog.
        MessageBoxResult defaultButton = results.Contains(defaultResult) ? defaultResult : results[0];

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var created = new List<Button>();
        foreach (MessageBoxResult result in results)
        {
            var button = new Button { Content = Label(result), MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            // The affirmative answer of a warning usually discards or overwrites something: show it as a danger action.
            if (result == results[0] && results.Length > 1)
                button.Style = (Style)Application.Current.FindResource(image == MessageBoxImage.Warning ? "DangerButton" : "PrimaryButton");
            else if (results.Length == 1) button.Style = (Style)Application.Current.FindResource("PrimaryButton");
            button.IsDefault = result == defaultButton;
            button.IsCancel = result == escape;
            button.Click += (_, _) => Answer(result);
            AutomationName(button, Label(result));
            footer.Children.Add(button);
            created.Add(button);
        }
        Buttons = created;

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 470, LineHeight = 20, VerticalAlignment = VerticalAlignment.Center };
        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        if (ImageGlyph(image) is { } icon) { DockPanel.SetDock(icon, Dock.Left); body.Children.Add(icon); }
        body.Children.Add(text);

        var footerBar = new Border { Padding = new Thickness(24, 12, 24, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        footerBar.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        footerBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var root = new DockPanel();
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.Add(footerBar);
        root.Children.Add(body);
        Content = root;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
        {
            try { Clipboard.SetText(Title + Environment.NewLine + Environment.NewLine + message); } catch { /* Clipboard busy: copying is best effort. */ }
        }));
        Loaded += (_, _) => created.First(b => b.IsDefault).Focus();
    }

    internal void Answer(MessageBoxResult result)
    {
        Result = result;
        if (IsVisible) Close();
    }

    internal static string Label(MessageBoxResult result) => result switch
    {
        MessageBoxResult.Yes => "Có",
        MessageBoxResult.No => "Không",
        MessageBoxResult.Cancel => "Hủy",
        _ => "OK"
    };

    private static FrameworkElement? ImageGlyph(MessageBoxImage image)
    {
        (string glyph, string brush)? spec = image switch
        {
            MessageBoxImage.Error => (Glyphs.Error, "DangerBrush"),
            MessageBoxImage.Warning => (Glyphs.Warning, "WarningBrush"),
            MessageBoxImage.Question => (Glyphs.Question, "AccentTextBrush"),
            MessageBoxImage.Information => (Glyphs.Info, "AccentTextBrush"),
            _ => null
        };
        if (spec is not { } s) return null;
        var icon = new TextBlock
        {
            Text = s.glyph, FontSize = 24, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
        };
        icon.SetResourceReference(TextElement.ForegroundProperty, s.brush);
        System.Windows.Automation.AutomationProperties.SetName(icon, image.ToString());
        return icon;
    }

    /// <summary>
    /// The standard dialog footer for code-built dialogs: a bar at the bottom, buttons right-aligned in the order given
    /// (the primary action first, Cancel/Close last). Marks <paramref name="primary"/> as IsDefault (Enter) and
    /// <paramref name="cancel"/> as IsCancel (Esc). Place it with DockPanel.Dock="Bottom" or in the dialog's last row.
    /// </summary>
    internal static Border Footer(Button? primary, Button? cancel, params Button[] others)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { primary }.Concat(others).Append(cancel).OfType<Button>())
        {
            button.MinWidth = Math.Max(button.MinWidth, 88);
            button.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(button);
        }
        if (primary is not null) primary.IsDefault = true;
        if (cancel is not null) cancel.IsCancel = true;
        var bar = new Border { Padding = new Thickness(24, 12, 24, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = panel };
        bar.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return bar;
    }

    private static void AutomationName(UIElement element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static MessageBoxResult Run(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        var dialog = new ThemedDialog(owner, text, caption, buttons, image, defaultResult);
        if (Responder is { } respond) return respond(dialog);
        dialog.ShowDialog();
        return dialog.Result;
    }

    // The MessageBox.Show overloads the app uses.
    public static MessageBoxResult Show(string text) => Run(null, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption) => Run(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text) => Run(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Run(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) =>
        Run(owner, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Run(owner, text, caption, button, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon,
        MessageBoxResult defaultResult) => Run(owner, text, caption, button, icon, defaultResult);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Run(null, text, caption, button, icon, MessageBoxResult.None);
}
