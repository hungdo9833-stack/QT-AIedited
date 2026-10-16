using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Persistence;

internal static class JsonFileCodec
{
    public static T DecodeDirect<T>(byte[] bytes, Action<T> validate, params string[] required) where T : class
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || required.Any(name => !root.TryGetProperty(name, out _)))
            throw new InvalidDataException("Thiếu trường dữ liệu bắt buộc.");
        T value = root.Deserialize<T>(DocumentJson.Options)
            ?? throw new InvalidDataException("Dữ liệu JSON trống.");
        validate(value);
        return value;
    }

}
