namespace QuickTranslator.AiEditor.Core.Models;

public sealed record ModelOptions
{
    public string Model { get; init; } = "translategemma:12b";
    public double Temperature { get; init; } = 0.1;
    public int ContextSize { get; init; } = 4096;
    public int MaximumOutputTokens { get; init; } = 1536;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200)
            throw new ArgumentException("Tên model không được để trống.");
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2)
            throw new ArgumentException("Temperature phải nằm trong khoảng 0–2.");
        if (ContextSize is < 512 or > 131072 || MaximumOutputTokens < 1 || MaximumOutputTokens > ContextSize - 256)
            throw new ArgumentException("Context phải từ 512 đến 131072; giới hạn đầu ra phải nhỏ hơn context ít nhất 256 token.");
        if (RequestTimeout < TimeSpan.FromMilliseconds(50) || RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentException("Timeout phải từ 50 mili giây đến 1 giờ.");
    }
}
