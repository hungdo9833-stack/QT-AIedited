using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ThemedDialogTests
{
    private static ThemedDialog Capture(Func<MessageBoxResult> show, MessageBoxResult answer, out MessageBoxResult result)
    {
        ThemedDialog? seen = null;
        ThemedDialog.Responder = d => { seen = d; return answer; };
        try { result = show(); }
        finally { ThemedDialog.Responder = null; }
        return seen!;
    }

    [Fact]
    public Task Yes_no_warning_puts_the_affirmative_first_as_a_danger_action_with_enter_and_escape_mapped() => StaHost.Run(() =>
    {
        var dialog = Capture(() => ThemedDialog.Show(null, "Bỏ thay đổi chưa lưu?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Warning),
            MessageBoxResult.No, out var result);
        Assert.Equal(MessageBoxResult.No, result);
        Assert.Equal(["Có", "Không"], dialog.Buttons.Select(b => (string)b.Content));
        Assert.Same(Application.Current.FindResource("DangerButton"), dialog.Buttons[0].Style);
        Assert.True(dialog.Buttons[0].IsDefault); Assert.False(dialog.Buttons[0].IsCancel);
        Assert.True(dialog.Buttons[1].IsCancel);
        Assert.Equal("Xác nhận", dialog.Title);
        Assert.Equal("Bỏ thay đổi chưa lưu?", dialog.Message);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Question_uses_the_primary_style_and_honours_the_default_result() => StaHost.Run(() =>
    {
        var dialog = Capture(() => ThemedDialog.Show(null, "Tiếp tục?", "Hàng đợi", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No),
            MessageBoxResult.Yes, out var result);
        Assert.Equal(MessageBoxResult.Yes, result);
        Assert.Same(Application.Current.FindResource("PrimaryButton"), dialog.Buttons[0].Style);
        Assert.False(dialog.Buttons[0].IsDefault); Assert.True(dialog.Buttons[1].IsDefault);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Ok_only_dialog_answers_ok_to_both_enter_and_escape_and_unanswered_close_is_the_negative_answer() => StaHost.Run(() =>
    {
        var ok = Capture(() => ThemedDialog.Show("Đã xuất xong.", "Xuất bản"), MessageBoxResult.OK, out _);
        var only = Assert.Single(ok.Buttons);
        Assert.Equal("OK", only.Content); Assert.True(only.IsDefault); Assert.True(only.IsCancel);
        // Closing with the title bar's X (no button pressed) reports the negative answer, as the system dialog does.
        var yesNo = new ThemedDialog(null, "?", "", MessageBoxButton.YesNoCancel, MessageBoxImage.None, MessageBoxResult.None);
        Assert.Equal(MessageBoxResult.Cancel, yesNo.Result);
        yesNo.Answer(MessageBoxResult.No);
        Assert.Equal(MessageBoxResult.No, yesNo.Result);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dialog_follows_the_theme() => StaHost.Run(() =>
    {
        try
        {
            ThemeManager.Apply(ShellTheme.Light);
            var dialog = new ThemedDialog(null, "x", "y", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None);
            Assert.Equal(Color.FromRgb(255, 255, 255), ((SolidColorBrush)dialog.Background).Color);
            ThemeManager.Apply(ShellTheme.Dark);
            Assert.Equal(Color.FromRgb(22, 25, 31), ((SolidColorBrush)dialog.Background).Color);
            Assert.Equal(Color.FromRgb(230, 233, 238), ((SolidColorBrush)dialog.Foreground).Color);
        }
        finally { ThemeManager.Apply(ShellTheme.Dark); }
        return Task.CompletedTask;
    });
}
