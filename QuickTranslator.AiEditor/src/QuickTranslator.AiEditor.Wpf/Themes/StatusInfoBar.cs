namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// The WPF UI InfoBar under a name XAML can give an x:Name: generated fields spell the type as Wpf.Ui.Controls.InfoBar,
/// which inside this namespace resolves to QuickTranslator.AiEditor.Wpf and does not compile. Use it with
/// Style="{StaticResource {x:Type wpfui:InfoBar}}" so it keeps the InfoBar template.
/// </summary>
public sealed class StatusInfoBar : global::Wpf.Ui.Controls.InfoBar
{
}
