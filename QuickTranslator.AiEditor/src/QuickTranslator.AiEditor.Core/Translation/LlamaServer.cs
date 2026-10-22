using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// A GGUF model as llama-server loads it: a Hugging Face repo with a quant tag (llama-server downloads and caches it
/// itself with <c>-hf</c>) or a .gguf file already on that machine (<c>-m</c>). The same text works for the local GPU and
/// a rented one, so a profile stores this string as its model.
/// </summary>
public sealed partial record GgufModelReference
{
    public string? Repository { get; private init; }
    public string? Quant { get; private init; }
    public string? FilePath { get; private init; }

    /// <summary>"owner/repo:Q4_K_M", "hf.co/owner/repo:Q4_K_M" (the form Ollama used) or an absolute path ending in .gguf.</summary>
    public static GgufModelReference Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string text = value.Trim();
        if (text.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            // "/models/x.gguf" is a file on a rented Linux GPU; Windows does not count it as fully qualified.
            if (!(Path.IsPathFullyQualified(text) || text.StartsWith('/')) || text.Any(char.IsControl))
                throw new ArgumentException("File GGUF phải là đường dẫn tuyệt đối.", nameof(value));
            return new() { FilePath = text };
        }
        if (text.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        Match match = HuggingFaceForm().Match(text);
        if (!match.Success)
            throw new ArgumentException("Model cần dạng chủ/repo:quant (ví dụ IndexTeam/Index-Translate-9B-GGUF:Q4_K_M) hoặc đường dẫn file .gguf.", nameof(value));
        return new() { Repository = match.Groups["repo"].Value, Quant = match.Groups["quant"].Value };
    }

    public bool IsHuggingFace => Repository is not null;

    /// <summary>Canonical text stored in a profile and shown to the user.</summary>
    public override string ToString() => FilePath ?? $"{Repository}:{Quant}";

    /// <summary>Short name for the server alias and UI: the repo or file name without the -GGUF / .gguf suffix.</summary>
    public string DisplayName => FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath)
        : Regex.Replace(Repository![(Repository!.IndexOf('/') + 1)..], "-GGUF$", "", RegexOptions.IgnoreCase) + "-" + Quant;

    public IReadOnlyList<string> ServerArguments() => FilePath is not null ? ["-m", FilePath] : ["-hf", $"{Repository}:{Quant}"];

    [GeneratedRegex(@"^(?<repo>[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}):(?<quant>[A-Za-z0-9._-]{1,40})$")]
    private static partial Regex HuggingFaceForm();
}

/// <summary>
/// How QT Studio starts llama-server, on this PC or on a rented GPU: loopback only, every layer on the GPU, the chat
/// template from the GGUF, and a total context of <see cref="ContextPerSlot"/> × <see cref="Slots"/> (llama-server splits
/// -c across -np slots, while a profile's Context means one request).
/// </summary>
public sealed record LlamaServerLaunch(GgufModelReference Model, int Port, int Slots, int ContextPerSlot)
{
    public const int MaximumSlots = 8;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Model);
        if (Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (Slots is < 1 or > MaximumSlots) throw new ArgumentOutOfRangeException(nameof(Slots));
        if (ContextPerSlot is < 1024 or > 262_144) throw new ArgumentOutOfRangeException(nameof(ContextPerSlot));
    }

    public IReadOnlyList<string> Arguments()
    {
        Validate();
        return [.. Model.ServerArguments(), "--alias", Model.DisplayName,
            "--host", "127.0.0.1", "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ngl", "99", "-c", (ContextPerSlot * Slots).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-np", Slots.ToString(System.Globalization.CultureInfo.InvariantCulture), "--jinja",
            // Text translation only: skip the vision projector some GGUF repos ship (its download and its VRAM).
            "--no-mmproj"];
    }
}

/// <summary>The llama.cpp Windows CUDA build QT Studio runs and ships to rented GPUs (pinned, tested release).</summary>
public static class LlamaCppRelease
{
    public const string Build = "b11796";
    public const string ServerZip = "llama-" + Build + "-bin-win-cuda-12.4-x64.zip";
    public const string CudaRuntimeZip = "cudart-llama-bin-win-cuda-12.4-x64.zip";
    public static Uri DownloadUri(string asset) => new($"https://github.com/ggml-org/llama.cpp/releases/download/{Build}/{asset}");
}

/// <param name="ZipPaths">The downloaded release zips (server + CUDA runtime), kept so they can be uploaded to a rented GPU.</param>
/// <param name="ServerExecutable">llama-server.exe after extraction on this PC.</param>
public sealed record LlamaCppPackage(string Build, IReadOnlyList<string> ZipPaths, string ServerExecutable);

/// <summary>Gets the pinned llama.cpp build onto this PC (download once, verify, extract) and reports progress text.</summary>
public interface ILlamaCppPackageProvider
{
    Task<LlamaCppPackage> EnsureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
