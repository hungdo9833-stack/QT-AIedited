using System.Net;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum SemanticFailureStage { Context, Provider, Completion, Parse, Audit }
public sealed record SemanticDiagnostic(string Code, bool CanRetry, bool StopRun, string Guidance);

/// <summary>Only controlled codes cross the checkpoint/UI boundary; never exception messages.</summary>
public static class SemanticProviderDiagnostics
{
    public static SemanticDiagnostic Classify(Exception error, SemanticFailureStage stage)
    {
        string code = error switch
        {
            SemanticTransportBudgetException => "TransportBudgetExceeded",
            SemanticTransportFailureException { SchemaRejected: true } => "Http400SchemaRejected",
            SemanticTransportFailureException { StatusCode: { } status } => HttpCode(status),
            SemanticTransportFailureException or HttpRequestException => "NetworkFailure",
            GeminiSurveyHttpException http => HttpCode(http.StatusCode),
            GeminiDailyQuotaExceededException => "DailyQuotaExceeded",
            GeminiRequestBudgetExceededException => "RequestBudgetExceeded",
            OperationCanceledException => "Timeout",
            OllamaRequestException { Kind: OllamaErrorKind.Timeout } => "Timeout",
            OllamaRequestException { Kind: OllamaErrorKind.Connection } => "NetworkFailure",
            ArgumentException => "ProviderConfigurationError",
            InvalidDataException when stage == SemanticFailureStage.Context => "ContextBudgetExceeded",
            InvalidDataException when stage == SemanticFailureStage.Completion => "IncompleteProviderResponse",
            InvalidDataException when stage == SemanticFailureStage.Parse => error.InnerException is JsonException { Path: null or "" } ? "SemanticParseFailure" : "SemanticSchemaViolation",
            InvalidDataException when stage == SemanticFailureStage.Provider => error.InnerException is JsonException ? "ProviderParseFailure" : "ProviderResponseInvalid",
            JsonException => stage == SemanticFailureStage.Parse ? "SemanticParseFailure" : "ProviderParseFailure",
            _ => "UnexpectedProviderFailure"
        };
        return Describe(code);
    }
    private static string HttpCode(HttpStatusCode status) => (int)status is >= 300 and <= 599 ? "Http" + (int)status : "UnexpectedProviderFailure";

    public static SemanticDiagnostic Describe(string code)
    {
        var detail = code switch
        {
            "Http400SchemaRejected" => new SemanticDiagnostic(code, false, true, "HTTP 400: provider từ chối schema Structured Outputs. Kiểm tra responseJsonSchema/độ phức tạp; không tự bỏ trường hay giảm kiểm chứng."),
            "Http400" => new(code, false, true, "HTTP 400: yêu cầu không hợp lệ. Kiểm tra model và cấu hình/schema; chưa đủ thông tin để kết luận schema bị từ chối."),
            "Http401" => new(code, false, true, "HTTP 401: xác thực thất bại. Kiểm tra API key an toàn trong Cài đặt; không dán key vào log."),
            "Http403" => new(code, false, true, "HTTP 403: không có quyền. Kiểm tra quyền key, API/project, hạn chế truy cập và điều kiện dịch vụ."),
            "Http404" => new(code, false, true, "HTTP 404: không tìm thấy model/endpoint hoặc không có quyền truy cập model. Kiểm tra ID và API version."),
            "Http408" or "Timeout" => new(code, true, false, "Timeout: đã giữ checkpoint. Kiểm tra kết nối/timeout rồi Resume thủ công; không tự retry."),
            "Http429" or "DailyQuotaExceeded" => new(code, true, true, "Hết quota/tốc độ provider. Chờ hạn mức phục hồi rồi Resume thủ công; không tự đổi model."),
            "TransportBudgetExceeded" => new(code, true, true, "Đã hết ngân sách HTTP hoặc byte/token reserve nội bộ của phiên (kể cả metadata), không phải hết quota Gemini. Kiểm tra Max HTTP/reserve trước khi Resume."),
            "RequestBudgetExceeded" => new(code, true, true, "Đã hết ngân sách request của phiên. Điều chỉnh ngân sách hoặc bắt đầu phiên Resume được xác nhận."),
            "NetworkFailure" => new(code, true, false, "Lỗi kết nối mạng/TLS/DNS. Kiểm tra mạng hoặc proxy rồi Resume thủ công; không tự retry."),
            "ProviderConfigurationError" or "ContextBudgetExceeded" => new(code, false, true, "Cấu hình model/context không hợp lệ. Sửa cấu hình và Dry-run lại trước khi gửi."),
            "ProviderParseFailure" => new(code, true, false, "Không parse được JSON envelope của provider. Giữ checkpoint; kiểm tra endpoint/proxy trước khi Resume thủ công."),
            "ProviderResponseInvalid" or "IncompleteProviderResponse" => new(code, true, false, "Provider trả phản hồi thiếu/chưa hoàn tất, không phải refusal đã xác nhận. Kiểm tra endpoint/output limit trước khi Resume thủ công."),
            "SemanticParseFailure" => new(code, true, false, "Nội dung AI không phải JSON hợp lệ. Chưa chấp nhận findings; kiểm tra Structured Outputs trước khi Resume thủ công."),
            "SemanticSchemaViolation" => new(code, true, false, "JSON AI vi phạm contract cục bộ (thiếu/sai trường, enum hoặc range). Chưa chấp nhận findings; không tự nới schema."),
            "ProviderRefusal" or "InheritedProviderBlock" => new(code, false, false, "Provider refusal: giữ Blocked và chứng cứ. Không tự gửi lại, đổi model hoặc thu nhỏ batch để vượt chặn."),
            "Cancelled" => new(code, true, false, "Đã tạm dừng và giữ checkpoint. Có thể Resume thủ công phần chưa xong."),
            "ProviderFailure" or "InvalidSchemaOrResponse" or "QuotaExhausted" => new(code, false, false, "Diagnostic cũ không giữ đủ HTTP/nguyên nhân; không thể khôi phục hay suy đoán lỗi gốc từ history này."),
            _ => null
        };
        if (detail is not null) return detail;
        if (code.Length == 7 && code.StartsWith("Http", StringComparison.Ordinal) && int.TryParse(code.AsSpan(4), out int status) && status is >= 300 and <= 599)
            return new(code, status >= 500, true, status >= 500 ? $"HTTP {status}: lỗi dịch vụ provider. Đã lưu checkpoint và dừng phiên, không gửi batch tiếp theo. Đợi dịch vụ phục hồi rồi Resume thủ công; không tự retry hoặc đổi model." : $"HTTP {status}: yêu cầu bị từ chối. Kiểm tra endpoint/cấu hình trước khi gửi lại.");
        return new("UnexpectedProviderFailure", false, true, "Lỗi chưa được phân loại. Giữ checkpoint; không suy đoán nguyên nhân hoặc hiển thị exception/raw response nhạy cảm.");
    }
}
