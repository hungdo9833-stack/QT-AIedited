using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public static class RawSourceReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static RawSourceReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static RawSourceSnapshot FromText(string text, string sourceName = "clipboard")
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Nguồn chứa UTF-16 không hợp lệ; không thể giữ nguyên khi lưu UTF-8.", nameof(text), ex);
        }
        return Create(sourceName, text, bytes, "UTF-8");
    }

    public static RawSourceSnapshot Decode(ReadOnlySpan<byte> bytes, SourceTextEncoding encoding = SourceTextEncoding.Auto,
        string sourceName = "source.txt")
    {
        byte[] original = bytes.ToArray();
        bool hasUtf8Bom = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
        Encoding decoder;
        int preambleLength = 0;
        string encodingName;

        switch (encoding)
        {
            case SourceTextEncoding.Auto:
            case SourceTextEncoding.Utf8:
                decoder = StrictUtf8;
                if (hasUtf8Bom) { preambleLength = 3; encodingName = "UTF-8 BOM"; }
                else encodingName = "UTF-8";
                break;
            case SourceTextEncoding.Gbk:
                decoder = StrictCodePage(936);
                encodingName = "GBK (CP936)";
                break;
            case SourceTextEncoding.Gb18030:
                decoder = StrictCodePage(54936);
                encodingName = "GB18030";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        try
        {
            string text = decoder.GetString(original, preambleLength, original.Length - preambleLength);
            return Create(sourceName, text, original, encodingName);
        }
        catch (DecoderFallbackException ex) when (encoding == SourceTextEncoding.Auto)
        {
            throw new SourceDecodingException("Tệp không phải UTF-8 hợp lệ. Hãy chọn rõ GBK hoặc GB18030; QT Studio không tự đoán bảng mã để tránh đổi nội dung.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SourceDecodingException($"Dữ liệu không hợp lệ theo bảng mã {encodingName}; không có ký tự thay thế nào được chèn.", ex);
        }
    }

    public static async Task<RawSourceSnapshot> ReadFileAsync(string path, SourceTextEncoding encoding = SourceTextEncoding.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Decode(bytes, encoding, Path.GetFileName(fullPath));
    }

    private static Encoding StrictCodePage(int codePage)
    {
        try { return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        catch (ArgumentException ex)
        {
            throw new SourceDecodingException($"Runtime không hỗ trợ code page {codePage}; không thể giải mã an toàn.", ex);
        }
    }

    private static RawSourceSnapshot Create(string sourceName, string text, byte[] originalBytes, string encodingName)
    {
        string name = string.IsNullOrWhiteSpace(sourceName) ? "source.txt" : sourceName;
        byte[] textBytes;
        try { textBytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new SourceDecodingException("Văn bản giải mã thành UTF-16 không hợp lệ.", ex);
        }
        return new(name, text, originalBytes.ToImmutableArray(), encodingName,
            Convert.ToHexString(SHA256.HashData(originalBytes)), Convert.ToHexString(SHA256.HashData(textBytes)),
            DetectNewLines(text));
    }

    private static SourceNewLineStyle DetectNewLines(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') { crlf++; index++; }
                else cr++;
            }
            else if (text[index] == '\n') lf++;
        }
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds == 0) return SourceNewLineStyle.None;
        if (kinds > 1) return SourceNewLineStyle.Mixed;
        if (crlf > 0) return SourceNewLineStyle.CrLf;
        return lf > 0 ? SourceNewLineStyle.Lf : SourceNewLineStyle.Cr;
    }
}

public sealed class SourceDecodingException : IOException
{
    public SourceDecodingException(string message, Exception? innerException = null) : base(message, innerException) { }
}
