using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>Explicit human dates/participants; no inferred defaults for a CPU event.</summary>
internal sealed class EntityMemoryReviewDialog : Window
{
    private readonly MemoryReviewRow _row;
    private readonly MemoryReviewStatus _action;
    private readonly bool _promotion;
    internal readonly TextBox From = new(), To = new(), Discovered = new(), Reveal = new(), Note = new() { AcceptsReturn = true, MinHeight = 65 };
    internal readonly ComboBox EventType = new() { ItemsSource = Enum.GetValues<TranslationImpactEventType>() };
    internal readonly ComboBox EventEffect = new() { ItemsSource = Enum.GetValues<MemoryEventEffect>() };
    internal readonly ComboBox Promotion = new() { ItemsSource = new[] { MemoryFactKind.Alias, MemoryFactKind.Title }, SelectedIndex = -1 };
    internal readonly ListBox Participants = new() { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = nameof(MemoryChoice.Label), Height = 110 };
    internal readonly ListBox Affected = new() { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = nameof(MemoryChoice.Label), Height = 95 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    internal MemoryReviewEdit? Result { get; private set; }

    internal EntityMemoryReviewDialog(MemoryReviewRow row, EntityMemoryState memory, MemoryReviewStatus action, bool promotion)
    {
        _row = row; _action = action; _promotion = promotion;
        Title = "Duyệt Memory · " + action; Width = 640; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40); SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        From.Text = row.Timeline.EffectiveFromChapter?.ToString(CultureInfo.InvariantCulture) ?? "";
        To.Text = row.Timeline.EffectiveToChapter?.ToString(CultureInfo.InvariantCulture) ?? "";
        Discovered.Text = row.Timeline.DiscoveredAtChapter?.ToString(CultureInfo.InvariantCulture) ?? "";
        Reveal.Text = row.Timeline.RevealedAtChapter?.ToString(CultureInfo.InvariantCulture) ?? "";
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Accepted chỉ ghi nhận đã duyệt. Confirmed cần nguồn hiện hành, mốc hiệu lực và tiết lộ do bạn kiểm tra. Không sửa Resources/prompt; không gộp danh tính.", TextWrapping = TextWrapping.Wrap });
        Add("Có hiệu lực từ chương (không phải mốc tiết lộ)", From); Add("Đến chương (trống = chưa biết)", To);
        Add("Phát hiện tại chương", Discovered); Add("Tiết lộ tại chương (không đoán khi chưa biết)", Reveal);
        if (promotion) Add("Chọn riêng loại dữ kiện nâng từ audit", Promotion);
        if (row.IsEvent)
        {
            var item = memory.Events.Single(x => x.Id == row.Id);
            EventType.SelectedItem = item.Type; EventEffect.SelectedItem = item.Effect;
            Participants.ItemsSource = memory.Entities.Where(x => x.Kind != MemoryEntityKind.AddressingCatalogue)
                .Select(x => new MemoryChoice(x.Id, $"{x.Label} · {x.Kind} · {x.Id.ToString()[..8]}")).ToArray();
            Affected.ItemsSource = memory.Facts.Select(x => new MemoryChoice(x.Id,
                $"{memory.Entities.Single(e => e.Id == x.SubjectId).Label} · {x.Kind} · {x.Value} · {x.Id.ToString()[..8]}")).ToArray();
            // CPU co-occurrence is not identity resolution. Require manual selection.
            if (item.Provenance.Origin != MemoryOrigin.CpuTrigger)
            {
                foreach (MemoryChoice choice in Participants.Items) if (item.EntityIds.Contains(choice.Id!.Value)) Participants.SelectedItems.Add(choice);
                foreach (MemoryChoice choice in Affected.Items) if (item.AffectedFactIds.Contains(choice.Id!.Value)) Affected.SelectedItems.Add(choice);
            }
            Add("Loại sự kiện", EventType); Add("Ảnh hưởng (None không thay đổi dữ kiện)", EventEffect);
            Add("Danh tính liên quan — chọn rõ từng mục, không đoán vai trò", Participants); Add("Dữ kiện bị ảnh hưởng", Affected);
        }
        Add("Ghi chú quyết định / nguồn đã kiểm tra (bắt buộc)", Note); panel.Children.Add(_error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = promotion ? "Xác nhận riêng alias/title" : "Lưu " + action, IsDefault = true };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        save.Click += (_, _) => { if (TryBuild(out var result, out string error)) { Result = result; DialogResult = true; } else _error.Text = error; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Add(string label, Control control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) }); panel.Children.Add(control); }
    }

    internal bool TryBuild(out MemoryReviewEdit? result, out string error)
    {
        result = null; error = "";
        try
        {
            int? Parse(TextBox box)
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return null;
                if (!int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0)
                    throw new ArgumentException("Mốc chương phải là số nguyên dương hoặc để trống.");
                return n;
            }
            var timeline = new MemoryTimeline(Parse(From), Parse(To), Parse(Discovered), Parse(Reveal)); timeline.Validate();
            if ((_promotion || _action == MemoryReviewStatus.Confirmed) && (timeline.EffectiveFromChapter is null || timeline.RevealedAtChapter is null))
                throw new ArgumentException("Cần xác minh riêng mốc hiệu lực và mốc tiết lộ trước khi Confirmed.");
            if (string.IsNullOrWhiteSpace(Note.Text) || Note.Text.Trim().Length > 2000) throw new ArgumentException("Cần ghi chú quyết định (tối đa 2.000 ký tự).");
            if (_promotion && Promotion.SelectedItem is not MemoryFactKind) throw new ArgumentException("Chọn rõ Alias hoặc Title; không tự nâng liên kết audit.");
            Guid[] participants = Participants.SelectedItems.Cast<MemoryChoice>().Select(x => x.Id!.Value).ToArray();
            Guid[] facts = Affected.SelectedItems.Cast<MemoryChoice>().Select(x => x.Id!.Value).ToArray();
            if (_row.IsEvent && _action == MemoryReviewStatus.Confirmed && participants.Length == 0)
                throw new ArgumentException("Chọn danh tính tham gia sau khi kiểm tra nguyên tác; CPU không chọn thay bạn.");
            var effect = EventEffect.SelectedItem is MemoryEventEffect selected ? selected : MemoryEventEffect.None;
            if (_row.IsEvent && _action == MemoryReviewStatus.Confirmed && effect != MemoryEventEffect.None && facts.Length == 0)
                throw new ArgumentException("Chọn dữ kiện bị ảnh hưởng hoặc giữ Effect = None.");
            result = new(timeline, Note.Text.Trim(), EventType.SelectedItem is TranslationImpactEventType type ? type : default,
                effect, participants, facts, Promotion.SelectedItem as MemoryFactKind?);
            return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        catch (InvalidDataException ex) { error = ex.Message; return false; }
    }
}
