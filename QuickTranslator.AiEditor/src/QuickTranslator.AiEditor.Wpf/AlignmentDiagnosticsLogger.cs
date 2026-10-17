using System.Globalization;
using System.IO;
using System.Text;
using QuickTranslator.AiEditor.Core.Traditional;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed class AlignmentDiagnosticsLogger
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Logs", "alignment.log");

    public async Task WriteAsync(Guid? segmentId, string sourceVersion, TraditionalPane origin, TextRange selected,
        SelectionProjection? projection, string outcome)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:O}\tsegment={segmentId?.ToString() ?? "none"}\tversion={sourceVersion}\torigin={origin}" +
            $"\tselected={Format(selected)}\tzh={Format(projection?.Chinese)}\thv={Format(projection?.HanViet)}" +
            $"\tvp={Format(projection?.VietPhrase)}\toutcome={outcome}{Environment.NewLine}");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            await File.AppendAllTextAsync(DefaultPath, line, new UTF8Encoding(false)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string Format(TextRange? range) => range is { } value ? $"{value.Start}+{value.Length}" : "none";
}
