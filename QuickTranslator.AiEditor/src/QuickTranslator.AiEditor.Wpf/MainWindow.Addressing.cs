using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private AddressingLedgerWindow? _addressingLedgerWindow;

    private void OnOpenAddressingLedger(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || _projectResources is null)
        {
            ShowError(new InvalidOperationException("Hãy mở một Project trước khi duyệt sổ xưng hô."));
            return;
        }
        if (_addressingLedgerWindow is { IsVisible: true }) { _addressingLedgerWindow.Activate(); return; }
        // Accepted rules are committed by the review service; this only refreshes the in-memory editor state.
        _addressingLedgerWindow = new AddressingLedgerWindow(_projectSession, _projectRepository, ApplyBookIntelligenceResources)
            { Owner = IsVisible ? this : null };
        _addressingLedgerWindow.Closed += (_, _) => _addressingLedgerWindow = null;
        _addressingLedgerWindow.Show();
    }
}
