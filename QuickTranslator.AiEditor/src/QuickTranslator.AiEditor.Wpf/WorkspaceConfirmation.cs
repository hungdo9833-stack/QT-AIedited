using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    internal Func<string, string, bool>? WorkspaceConfirmation { get; set; }

    private bool ConfirmWorkspaceChange(string message, string title) =>
        WorkspaceConfirmation?.Invoke(message, title) ??
        ThemedDialog.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
