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
        string details = string.Join("\n", recovered.Select(x => $"{Path.GetFileName(x.PrimaryPath)} ← {x.SourcePath}\n{(x.PrimaryCondition == RecoveryFileCondition.Missing ? "Primary thiếu" : "Primary hỏng")}: {x.PrimaryError}"));
        if (!ConfirmWorkspaceChange($"Đang dùng các bản backup đã đọc và kiểm tra theo định dạng:\n\n{details}\n\nĐã xuất raw primary/backup và checksum vào:\n{rescue}\n\nCho phép thay primary bằng backup đã kiểm tra để tiếp tục mở/lưu? Chọn Không để giữ nguyên dữ liệu và context hiện tại.",
                "Xác nhận phục hồi dữ liệu"))
            throw new OperationCanceledException("Bạn chưa xác nhận thay primary. File nguồn/backup và context hiện tại được giữ; bản cứu hộ đã xuất.");
        foreach (var repair in repairs) await repair();
        SetStatus("Đã phục hồi các primary được xác nhận; backup tốt và bản cứu hộ được giữ: " + rescue);
    }

    private async Task ReviewProjectRecoveryAsync(ProjectSession session)
    {
        if (!session.RecoveryReport.RequiresDecision && session.RecoveryReport.Orphans.Count == 0) return;
        var list = new ListBox { Margin = new Thickness(8), DisplayMemberPath = "Label" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        var rollback = new Button { Content = "Rollback transaction đã chọn" };
        var commit = new Button { Content = "Xác nhận commit đủ file" };
        var export = new Button { Content = "Xuất bản cứu hộ…" };
        var close = new Button { Content = "Tiếp tục / Đóng" };
        var buttons = new WrapPanel(); foreach (var button in new[] { rollback, commit, export, close }) buttons.Children.Add(button);
        var panel = new DockPanel(); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(status); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Owner = this, Title = "Đối chiếu transaction và chương mất liên kết", Width = 980, Height = 560, Content = panel };
        bool working = false;
        void Reload()
        {
            var rows = session.RecoveryReport.Transactions.Where(x => x.State is not (ProjectTransactionState.Committed or ProjectTransactionState.RolledBack))
                .Select(x => new RecoveryRow($"{x.Operation} · {x.Id} · {x.State}\n{string.Join("; ", x.Issues)}", x)).ToList();
            rows.AddRange(session.RecoveryReport.Orphans.Select(x => new RecoveryRow($"CHƯƠNG MẤT LIÊN KẾT · {x.RelativePath} · {x.Title}\n{x.Error ?? "File đọc được; không tự nhận/xóa liên kết."}", null)));
            list.ItemsSource = rows; list.SelectedIndex = rows.Count == 0 ? -1 : 0;
            status.Text = $"Còn transaction cần quyết định: {session.RecoveryReport.RequiresDecision}. Orphan: {session.RecoveryReport.Orphans.Count}.\nChỉ đề xuất dựa trên hash before/after. File mâu thuẫn hoặc journal hỏng cần đối chiếu thủ công; có thể xuất toàn bộ dữ liệu cứu hộ.";
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
            { status.Text = "Không đủ bằng chứng before/after cho quyết định này. Hãy xuất cứu hộ và đối chiếu; không tự sửa."; return; }
            SetWorking(true);
            try
            {
                string rescue = await ExportAsync();
                if (!ConfirmWorkspaceChange($"Đã xuất cứu hộ: {rescue}\n\nÁp dụng {decision} cho {preview.Operation} / {preview.Id}? Các file khác sẽ được giữ nguyên.", "Xác nhận transaction")) return;
                await _projectRepository.RecoverTransactionAsync(session, preview.Id, decision);
                await _projectRepository.InspectRecoveryAsync(session); Reload();
            }
            catch (Exception ex) { status.Text = "Chưa hoàn tất: " + ex.Message + " Giữ cửa sổ mở để thử lại sau khi xử lý khóa/quyền ghi."; ShowError(ex); }
            finally { SetWorking(false); }
        }
        rollback.Click += async (_, _) => await RecoverAsync(ProjectRecoveryDecision.RollBack);
        commit.Click += async (_, _) => await RecoverAsync(ProjectRecoveryDecision.ConfirmCommitted);
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
            throw new OperationCanceledException("Dự án còn transaction chưa đối chiếu; chưa thay context. Journal/orphans được giữ nguyên.");
    }
    private sealed record RecoveryRow(string Label, ProjectTransactionPreview? Transaction);
}
