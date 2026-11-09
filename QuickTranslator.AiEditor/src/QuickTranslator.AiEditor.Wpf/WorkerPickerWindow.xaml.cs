using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>The "+ Worker" window: every worker QT Studio can translate on, one button each.</summary>
public partial class WorkerPickerWindow : Window
{
    public WorkerChoice? Chosen { get; private set; }

    internal WorkerPickerWindow(string bookTitle, IReadOnlyList<WorkerChoice> choices, IReadOnlySet<string> alreadyAdded)
    {
        InitializeComponent();
        SubtitleText.Text = $"“{bookTitle}”. Các worker của một truyện cùng chia nhau các chương. " +
            "Truyện khác dùng chung worker sẽ chờ tới lượt.";
        ChoiceList.ItemsSource = choices.Select(choice => new Item(choice, alreadyAdded.Contains(choice.Id))).ToList();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Item { CanAdd: true } item) return;
        Chosen = item.Choice;
        DialogResult = true;
    }

    /// <summary>One row of the window: the worker, and whether it can still be added to this book.</summary>
    public sealed record Item(WorkerChoice Choice, bool Added)
    {
        public bool CanAdd => Choice.Available && !Added;
        public string ButtonText => Added ? "Đã thêm" : Choice.Available ? "Thêm" : "Chưa dùng được";
        public string Detail => Choice.Available ? Choice.Detail : Choice.Reason ?? Choice.Detail;
    }
}
