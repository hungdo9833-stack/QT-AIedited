using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private AddressingLedgerWindow? _addressingLedgerWindow;

    private void OnOpenAddressingLedger(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || _projectResources is null)
        {
            ShowError(new InvalidOperationException("Hãy mở một Project trước khi mở sổ xưng hô."));
            return;
        }
        if (_addressingLedgerWindow is { IsVisible: true }) { _addressingLedgerWindow.Activate(); return; }
        // Accepted rules are committed by the review service; this only refreshes the in-memory editor state.
        _addressingLedgerWindow = new AddressingLedgerWindow(_projectSession, _projectRepository, ApplyAddressingResources, _gpuBackends.Profiles)
            { Owner = IsVisible ? this : null };
        _addressingLedgerWindow.Closed += (_, _) => _addressingLedgerWindow = null;
        _addressingLedgerWindow.Show();
    }

    private void ApplyAddressingResources(ProjectResources resources)
    {
        _projectResources = resources.Clone();
        LoadCharacterProject(_projectResources.Characters);
        SetGlossaryTerms(_projectResources.Terms);
        ApplyCurrentProjectResources();
        SetStatus("Đã lưu đề xuất được xác nhận vào tài nguyên Project.");
    }
}
