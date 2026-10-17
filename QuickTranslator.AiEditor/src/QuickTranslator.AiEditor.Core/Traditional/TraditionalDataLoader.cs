using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Traditional;

public sealed class TraditionalDataLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<TraditionalDictionarySnapshot> LoadAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Hãy chọn thư mục Data của QuickTranslator.", nameof(rootDirectory));
        string root = Path.GetFullPath(rootDirectory);
        string iniPath = Path.Combine(root, "Dictionaries.ini");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Không tìm thấy thư mục Data: {root}");
        if (!File.Exists(iniPath)) throw new FileNotFoundException("Không tìm thấy Dictionaries.ini trong thư mục đã chọn.", iniPath);

        var diagnostics = new List<DictionaryDiagnostic>();
        var filesForFingerprint = new List<string> { iniPath };
        var config = await ReadConfigurationAsync(iniPath, diagnostics, cancellationToken).ConfigureAwait(false);

        string Required(string key)
        {
            if (!config.TryGetValue(key, out string? configured) || string.IsNullOrWhiteSpace(configured))
                throw new TraditionalDataException($"Dictionaries.ini thiếu cấu hình bắt buộc {key}.",
                    [.. diagnostics, new(DictionaryDiagnosticSeverity.Error, "Dictionaries.ini", null, $"Thiếu khóa {key}.")]);
            return Resolve(root, configured);
        }

        string? Optional(string key)
        {
            if (!config.TryGetValue(key, out string? configured) || string.IsNullOrWhiteSpace(configured)) return null;
            string path = Resolve(root, configured);
            if (File.Exists(path)) return path;
            diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, Relative(root, path), null,
                $"Không tìm thấy file tùy chọn được cấu hình cho {key}."));
            return null;
        }

        string hanPath = Required("ChinesePhienAmWords");
        string vpPath = Required("VietPhrase");
        string namesPath = Required("Names");
        EnsureRequired(root, hanPath, "ChinesePhienAmWords", diagnostics);
        EnsureRequired(root, vpPath, "VietPhrase", diagnostics);
        EnsureRequired(root, namesPath, "Names", diagnostics);

        filesForFingerprint.AddRange([hanPath, vpPath, namesPath]);
        var han = await ReadDictionaryAsync(root, hanPath, diagnostics, cancellationToken).ConfigureAwait(false);
        var phrases = await ReadDictionaryAsync(root, vpPath, diagnostics, cancellationToken).ConfigureAwait(false);
        var names = await ReadDictionaryAsync(root, namesPath, diagnostics, cancellationToken).ConfigureAwait(false);
        await OverlayAsync("VietPhraseAddition", phrases).ConfigureAwait(false);
        await OverlayAsync("NamesPhu", names).ConfigureAwait(false);
        var pronouns = await ReadOptionalAsync("Pronouns").ConfigureAwait(false);
        var rules = await ReadOptionalAsync("LuatNhan", skipComments: true).ConfigureAwait(false);
        rules = rules.OrderByDescending(item => item.Key.Length).ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var marks = await ReadOptionalAsync("Mark").ConfigureAwait(false);
        ValidateRelatedFiles(root, config, diagnostics);

        int maximum = ReadBoundedInteger(config, "CumTuToiDa", 20, 1, 64, diagnostics);
        int nhan = ReadBoundedInteger(config, "ThuatToanNhan", 3, 1, 3, diagnostics);
        string fingerprint = await FingerprintAsync(filesForFingerprint.Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        var manifest = new TraditionalDataManifest
        {
            RootDirectory = root,
            Fingerprint = fingerprint,
            HanVietEntries = han.Count,
            VietPhraseEntries = phrases.Count,
            NameEntries = names.Count,
            PronounEntries = pronouns.Count,
            RuleEntries = rules.Count,
            MaximumPhraseLength = maximum,
            NhanAlgorithm = nhan,
            Diagnostics = diagnostics.AsReadOnly()
        };
        return new(manifest, Freeze(han), Freeze(phrases), Freeze(names), Freeze(pronouns), Freeze(rules), Freeze(marks));

        async Task OverlayAsync(string key, Dictionary<string, string> destination)
        {
            string? path = Optional(key);
            if (path is null) return;
            filesForFingerprint.Add(path);
            var overlay = await ReadDictionaryAsync(root, path, diagnostics, cancellationToken).ConfigureAwait(false);
            foreach (var item in overlay) destination[item.Key] = item.Value;
        }

        async Task<Dictionary<string, string>> ReadOptionalAsync(string key, bool skipComments = false)
        {
            string? path = Optional(key);
            if (path is null) return new(StringComparer.Ordinal);
            filesForFingerprint.Add(path);
            return await ReadDictionaryAsync(root, path, diagnostics, cancellationToken, skipComments).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadConfigurationAsync(string path,
        List<DictionaryDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await ReadLinesAsync(path, cancellationToken, (line, number) =>
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) return;
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator != line.LastIndexOf('='))
            {
                diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, "Dictionaries.ini", number, "Bỏ qua dòng không đúng định dạng key=value."));
                return;
            }
            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!result.TryAdd(key, value)) diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, "Dictionaries.ini", number, $"Khóa {key} bị lặp; giữ giá trị đầu tiên."));
        }).ConfigureAwait(false);
        return result;
    }

    private static async Task<Dictionary<string, string>> ReadDictionaryAsync(string root, string path,
        List<DictionaryDiagnostic> diagnostics, CancellationToken cancellationToken, bool skipComments = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string display = Relative(root, path);
        try
        {
            await ReadLinesAsync(path, cancellationToken, (line, number) =>
            {
                if (line.Length == 0 || skipComments && line.StartsWith('#')) return;
                int separator = line.IndexOf('=');
                if (separator <= 0 || separator != line.LastIndexOf('='))
                {
                    diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, display, number, "Bỏ qua dòng không có đúng một dấu '='."));
                    return;
                }
                string key = line[..separator];
                string value = line[(separator + 1)..];
                if (!result.TryAdd(key, value)) diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, display, number, $"Khóa '{key}' bị lặp; giữ dòng đầu tiên như QT cũ."));
            }).ConfigureAwait(false);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{display} không phải UTF-8 hợp lệ. Data không được sửa.", ex);
        }
        return result;
    }

    private static async Task ReadLinesAsync(string path, CancellationToken cancellationToken, Action<string, int> consume)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] prefix = new byte[4];
        int prefixLength = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        if (prefixLength >= 2 && (prefix[0] == 0xff && prefix[1] == 0xfe || prefix[0] == 0xfe && prefix[1] == 0xff) ||
            prefixLength >= 4 && (prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff ||
                                  prefix[0] == 0xff && prefix[1] == 0xfe && prefix[2] == 0 && prefix[3] == 0))
            throw new InvalidDataException($"{Path.GetFileName(path)} dùng UTF-16/UTF-32; cần bộ Data UTF-8 để tránh đọc sai chữ.");
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: false);
        int lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            consume(line, lineNumber);
        }
    }

    private static string Resolve(string root, string configured)
    {
        configured = configured.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(root, configured));
        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dictionaries.ini tham chiếu file nằm ngoài thư mục Data.");
        return path;
    }

    private static void ValidateRelatedFiles(string root, IReadOnlyDictionary<string, string> config,
        List<DictionaryDiagnostic> diagnostics)
    {
        string[] related = ["ChinesePhienAmEnglishWords", "CEDict", "Babylon", "LacViet", "TrichDan", "ThieuChuu", "IgnoredChinesePhrases", "PanGuDict"];
        foreach (string key in related)
        {
            if (!config.TryGetValue(key, out string? configured) || string.IsNullOrWhiteSpace(configured)) continue;
            string path = Resolve(root, configured);
            if (!File.Exists(path)) diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, Relative(root, path), null,
                $"File liên quan {key} được khai báo nhưng không tồn tại; Editor bốn ô không cần file này."));
        }
    }

    private static void EnsureRequired(string root, string path, string key, List<DictionaryDiagnostic> diagnostics)
    {
        if (File.Exists(path)) return;
        var all = diagnostics.Append(new DictionaryDiagnostic(DictionaryDiagnosticSeverity.Error, Relative(root, path), null,
            $"Thiếu file bắt buộc cho {key}.")).ToArray();
        throw new TraditionalDataException($"Bộ Data thiếu file bắt buộc: {Relative(root, path)}", all);
    }

    private static int ReadBoundedInteger(Dictionary<string, string> config, string key, int fallback, int minimum, int maximum,
        List<DictionaryDiagnostic> diagnostics)
    {
        if (!config.TryGetValue(key, out string? text)) return fallback;
        if (int.TryParse(text, out int value) && value >= minimum && value <= maximum) return value;
        diagnostics.Add(new(DictionaryDiagnosticSeverity.Warning, "Dictionaries.ini", null,
            $"{key} không hợp lệ; dùng {fallback}."));
        return fallback;
    }

    private static async Task<string> FingerprintAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant()));
            await using var stream = File.OpenRead(path);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> Freeze(Dictionary<string, string> source) =>
        new ReadOnlyDictionary<string, string>(source);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}
