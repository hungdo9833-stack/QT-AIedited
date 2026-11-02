using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

/// <summary>
/// Machine-checkable rules of docs/UI-DESIGN-SYSTEM.vi.md: sizes, radii and colors come from Themes/, the menu bar and
/// rail keep their agreed shape, and UI built in C# does not keep growing. A failure here means "use a token / update
/// the design doc", not "relax the test".
/// </summary>
public sealed class DesignSystemGuardTests
{
    private static readonly string WpfSource = FindWpfSource();

    // Code that legitimately builds colors: the Reader's user-chosen page palette and the alignment highlight overlay.
    private static readonly HashSet<string> ColorAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "MainWindow.Reader.cs", "TextBoxRangeHighlightAdorner.cs", "MainWindow.Chapter.cs"
    };

    // Controls created in C# (new Button/TextBlock/Grid…). Lower it when UI moves to XAML; never raise it.
    private const int CodeBuiltControlBudget = 236;

    [Fact]
    public void Xaml_outside_Themes_uses_tokens_for_type_size_radius_and_color()
    {
        var literal = new Regex(@"\b(FontSize|CornerRadius)=""[0-9]|""#[0-9A-Fa-f]{3,8}""|Property=""(FontSize|CornerRadius)"" Value=""[0-9]");
        var offenders = XamlFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(x => literal.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.path)}:{x.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Use Themes/Tokens.xaml keys instead of literals: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Code_outside_Themes_takes_colors_and_font_sizes_from_resources()
    {
        var color = new Regex(@"new SolidColorBrush\(|\bBrushes\.[A-Z]|Color\.From(Rgb|Argb)\(|ColorConverter\.");
        var fontSize = new Regex(@"\bFontSize\s*=\s*[0-9]");
        var offenders = CodeFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(x => fontSize.IsMatch(x.line) || (color.IsMatch(x.line) && !ColorAllowList.Contains(Path.GetFileName(x.path))))
            .Select(x => $"{Path.GetFileName(x.path)}:{x.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Use SetResourceReference with a brush/token key: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Icon_only_buttons_have_an_accessible_name()
    {
        var emptyButton = new Regex(@"<Button\b(?![^>]*\bContent=)(?![^>]*AutomationProperties\.Name=)[^>]*/>");
        var offenders = XamlFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(x => emptyButton.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.path)}:{x.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Give icon-only buttons AutomationProperties.Name: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Dialogs_go_through_ThemedDialog()
    {
        string[] offenders = CodeFiles()
            .Where(path => !Path.GetFileName(path).Equals("ThemedDialog.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("MessageBox.Show(", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray()!;
        Assert.True(offenders.Length == 0, "Use ThemedDialog.Show: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Ui_built_in_code_does_not_grow()
    {
        var control = new Regex(@"new (Button|TextBlock|TextBox|CheckBox|ComboBox|RadioButton|StackPanel|Grid|Border|DockPanel|WrapPanel|ListBox|DataGrid|Expander|ScrollViewer|Window)\b");
        int count = CodeFiles().Sum(path => control.Matches(File.ReadAllText(path)).Count);
        Assert.True(count <= CodeBuiltControlBudget,
            $"{count} controls are created in C# (budget {CodeBuiltControlBudget}). Put static UI in XAML (docs/UI-DESIGN-SYSTEM.vi.md §6.3).");
    }

    [Fact]
    public void Menu_bar_and_rail_keep_the_workflow_structure()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument main = XDocument.Load(Path.Combine(WpfSource, "MainWindow.xaml"));
        XElement menu = main.Descendants(wpf + "Menu").First();
        string[] roots = menu.Elements(wpf + "MenuItem").Select(item => (string?)item.Attribute("Header") ?? "").ToArray();
        Assert.Equal(["_Tệp", "_Nhập", "_Dự án", "_Xuất bản", "_Hiển thị"], roots);

        XElement rail = main.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ShellNavigation");
        int destinations = rail.Descendants(wpf + "Button")
            .Count(b => (string?)b.Attribute("Click") == "OnShellNavigate" && (string?)b.Attribute("Visibility") != "Collapsed");
        Assert.InRange(destinations, 1, 5);
    }

    private static IEnumerable<string> XamlFiles() => Directory.EnumerateFiles(WpfSource, "*.xaml", SearchOption.AllDirectories)
        .Where(path => !IsExcluded(path));

    private static IEnumerable<string> CodeFiles() => Directory.EnumerateFiles(WpfSource, "*.cs", SearchOption.AllDirectories)
        .Where(path => !IsExcluded(path));

    private static bool IsExcluded(string path)
    {
        string relative = Path.GetRelativePath(WpfSource, path);
        return relative.StartsWith("Themes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindWpfSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "QuickTranslator.AiEditor.Wpf");
            if (File.Exists(Path.Combine(candidate, "MainWindow.xaml"))) return candidate;
        }
        throw new DirectoryNotFoundException("QuickTranslator.AiEditor.Wpf source folder not found above the test output.");
    }
}
