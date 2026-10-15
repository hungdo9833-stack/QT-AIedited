using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private async Task ConfirmRecoveredSourcesAsync(IEnumerable<RecoveryInfo> sources, IEnumerable<Func<Task>> repairs, string rescueDirectory)
    {
        var recovered = sources.Where(x => x.Source == RecoverySource.Backup)
            .DistinctBy(x => x.PrimaryPath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (recovered.Length == 0) return;
        string rescue = Path.Combine(rescueDirectory, "recovery-before-confirm-" + Guid.NewGuid().ToString("N") + ".zip");
        await RecoveryFiles.ExportRescueAsync(recovered.Select(x => x.PrimaryPath), rescue);
        string details = string.Join("\n", recovered.Select(x =>
            $"• {Path.GetFileName(x.PrimaryPath)}: {(x.PrimaryCondition == RecoveryFileCondition.Missing ? "thiếu tệp chính" : "tệp chính không đọc được")}"));
        if (!ConfirmWorkspaceChange($"QT Studio đã tìm thấy bản dự phòng hợp lệ cho:\n\n{details}\n\n" +
                "Một gói cứu hộ đã được lưu trước khi sửa. Dùng bản dự phòng để tiếp tục mở dự án?",
                "Khôi phục dữ liệu dự án"))
            throw new OperationCanceledException("Bạn chưa chọn khôi phục. Dữ liệu hiện tại được giữ nguyên và gói cứu hộ đã được lưu.");
        foreach (var repair in repairs) await repair();
        SetStatus("Đã khôi phục dữ liệu từ bản dự phòng. Gói cứu hộ vẫn được giữ trong thư mục dự án.");
    }

    private async Task ReviewProjectRecoveryAsync(ProjectSession session)
    {
        if (!session.RecoveryReport.RequiresDecision && session.RecoveryReport.Orphans.Count == 0) return;
        var list = new ListBox { Margin = new Thickness(8), DisplayMemberPath = "Label" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        var rollback = new Button { Content = "Phục hồi bản trước" };
        var commit = new Button { Content = "Giữ dữ liệu hiện tại" };
        var export = new Button { Content = "Lưu gói cứu hộ…" };
        var close = new Button { Content = "Đóng" };
        var technical = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 100 };
        var details = new Expander { Header = "Chi tiết kỹ thuật", IsExpanded = false, Content = technical,
            Margin = new Thickness(8, 0, 8, 8) };
        var buttons = new WrapPanel(); foreach (var button in new[] { rollback, commit, export, close }) buttons.Children.Add(button);
        var bottom = new StackPanel(); bottom.Children.Add(details); bottom.Children.Add(buttons);
        var panel = new DockPanel(); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(status); panel.Children.Add(bottom); panel.Children.Add(list);
        var window = new Window { Owner = this, Title = "Dự án cần kiểm tra trước khi tiếp tục", Width = 900, Height = 560, Content = panel };
        ClampWindowToWorkArea(window, 980, 560);
        bool working = false;
        void Reload()
        {
            var rows = session.RecoveryReport.Transactions.Where(x => x.State is not (ProjectTransactionState.Committed or ProjectTransactionState.RolledBack))
                .Select(x => new RecoveryRow($"Thay đổi “{x.Operation}” chưa hoàn tất · cần chọn cách xử lý", x)).ToList();
            rows.AddRange(session.RecoveryReport.Orphans.Select(x => new RecoveryRow(
                $"Tệp chương chưa liên kết · {x.Title}", null, $"{x.RelativePath}\n{x.Error ?? "Tệp đọc được nhưng chưa thuộc mục lục."}")));
            list.ItemsSource = rows; list.SelectedIndex = rows.Count == 0 ? -1 : 0;
            status.Text = rows.Count == 0 ? "Dữ liệu dự án đã sẵn sàng."
                : $"Có {rows.Count:N0} mục cần kiểm tra. QT Studio sẽ lưu gói cứu hộ trước khi áp dụng lựa chọn của bạn.";
            technical.Text = list.SelectedItem is RecoveryRow row ? row.TechnicalDetails : "";
        }
        void SetWorking(bool value) { working = value; rollback.IsEnabled = commit.IsEnabled = export.IsEnabled = close.IsEnabled = !value; }
        async Task<string> ExportAsync()
        {
            string destination = Path.Combine(session.RootDirectory, "Backups", "transaction-rescue-" + Guid.NewGuid().ToString("N") + ".zip");
            var paths = (Directory.Exists(session.Resolve("History/Transactions"))
                ? Directory.GetFiles(session.Resolve("History/Transactions"), "*", SearchOption.AllDirectories) : [])
                .Concat(Directory.GetFiles(session.Resolve("Chapters"), "*", SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(session.Resolve("History/Deleted"), "*", SearchOption.TopDirectoryOnly))
                .Append(session.ManifestPath).ToArray();
            await RecoveryFiles.ExportRescueAsync(paths, destination); return destination;
        }
        async Task RecoverAsync(ProjectRecoveryDecision decision)
        {
            if (working || list.SelectedItem is not RecoveryRow { Transaction: { } preview }) return;
            if (decision == ProjectRecoveryDecision.RollBack && !preview.CanRollBack ||
                decision == ProjectRecoveryDecision.ConfirmCommitted && !preview.CanConfirmCommitted)
            { status.Text = "Chưa đủ thông tin để áp dụng lựa chọn này. Hãy lưu gói cứu hộ và xem Chi tiết kỹ thuật."; return; }
            SetWorking(true);
            try
            {
                await ExportAsync();
                string action = decision == ProjectRecoveryDecision.RollBack ? "phục hồi bản trước" : "giữ dữ liệu hiện tại";
                if (!ConfirmWorkspaceChange($"Gói cứu hộ đã được lưu. Bạn có muốn {action} cho thay đổi “{preview.Operation}”?",
                        "Xác nhận xử lý dữ liệu")) return;
                await _projectRepository.RecoverTransactionAsync(session, preview.Id, decision);
                await _projectRepository.InspectRecoveryAsync(session); Reload();
            }
            catch (Exception ex) { status.Text = "Chưa hoàn tất: " + ex.Message + " Giữ cửa sổ mở để thử lại sau khi xử lý khóa/quyền ghi."; ShowError(ex); }
            finally { SetWorking(false); }
        }
        rollback.Click += async (_, _) => await RecoverAsync(ProjectRecoveryDecision.RollBack);
        commit.Click += async (_, _) => await RecoverAsync(ProjectRecoveryDecision.ConfirmCommitted);
        list.SelectionChanged += (_, _) => technical.Text = list.SelectedItem is RecoveryRow row ? row.TechnicalDetails : "";
        export.Click += async (_, _) =>
        {
            if (working) return; SetWorking(true);
            try
            {
                var dialog = new SaveFileDialog { Filter = "Báo cáo phục hồi (*.json)|*.json", FileName = "project-recovery-preview.json" };
                if (dialog.ShowDialog(window) != true) return;
                await using var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(file, session.RecoveryReport, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
                status.Text = "Báo cáo: " + dialog.FileName + "\nBản cứu hộ: " + await ExportAsync();
            }
            catch (Exception ex) { status.Text = "Chưa xuất được cứu hộ: " + ex.Message; ShowError(ex); }
            finally { SetWorking(false); }
        };
        close.Click += (_, _) => window.Close();
        window.Closing += (_, e) => { if (working) e.Cancel = true; };
        Reload(); window.ShowDialog();
        if (session.RecoveryReport.RequiresDecision)
            throw new OperationCanceledException("Dự án vẫn còn thay đổi chưa được xử lý. Dữ liệu được giữ nguyên.");
    }
    private sealed record RecoveryRow(string Label, ProjectTransactionPreview? Transaction, string? ExtraDetails = null)
    {
        public string TechnicalDetails => Transaction is null ? ExtraDetails ?? "" :
            $"Operation: {Transaction.Operation}\nId: {Transaction.Id}\nState: {Transaction.State}\n{string.Join("; ", Transaction.Issues)}";
        public override string ToString() => Label;
    }

    private void ShowLegacyImportReport(LegacyImportReport report)
    {
        string rows = string.Join("\n", report.Chapters.Select(x =>
            $"{x.State} · chương {x.Number} · {x.ChapterId}\nNguồn: {x.LoadedSourcePath ?? x.SourcePath}\nĐích: {x.TargetPath}"));
        ShowTextWindow("Báo cáo import legacy",
            $"Transaction: {report.TransactionId}\nRollback hoàn tất: {report.RollbackComplete}\nZIP nguồn: {report.SourceBackupPath}\nLỗi: {report.Error}\n\n{rows}\n\nNguồn legacy không bị thay đổi. Nếu NeedsRecovery, mở đối chiếu transaction trước khi ghi tiếp.");
    }
}
