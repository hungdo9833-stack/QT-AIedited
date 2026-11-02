namespace QuickTranslator.AiEditor.Core.AutoTranslate;

public static class CodexSurveyPrompt
{
    public static string Survey(string bookTitle) => $$"""
        Bạn là biên tập viên dịch truyện Trung → Việt. Thư mục hiện tại có raw.txt: nguyên văn tiếng Trung của truyện “{{bookTitle}}”
        (các chương nối tiếp nhau, mỗi chương bắt đầu bằng dòng tiêu đề). Hãy khảo sát cả bộ và tạo đúng hai file JSON UTF-8
        trong thư mục này để QuickTranslator dùng khi dịch toàn bộ truyện.

        Cách làm:
        - raw.txt có thể rất dài. Đọc theo từng đoạn bằng lệnh shell (rg, Select-String, Get-Content với -TotalCount/-Skip...),
          đếm tần suất tên để tìm đủ nhân vật chính và nhân vật lặp lại; đọc kỹ các cảnh hội thoại để biết ai xưng hô thế nào với ai.
        - Không sửa raw.txt. Không tạo file nào khác ngoài hai file dưới đây. Không chạy git.

        1) characters.json
        {
          "setting": "Historical" | "Modern" | "Unspecified",
          "characters": [
            {
              "chineseName": "林凡",
              "vietnameseName": "Lâm Phàm",
              "aliases": ["凡哥", "林师兄"],
              "gender": "Male" | "Female" | "Other" | "Unknown",
              "narrativePronoun": "hắn",
              "secondaryPronoun": "hắn ta",
              "avoidPronouns": ["anh ấy", "cậu ấy"],
              "notes": "vai trò ngắn gọn, cách gọi đặc biệt"
            }
          ],
          "relationships": [
            {
              "speaker": "林凡", "listener": "苏雪",
              "relationship": "Sư huynh - Sư muội",
              "selfReference": "ta", "addresseeReference": "muội",
              "notes": "từ khi nào đổi cách xưng hô, nếu có"
            }
          ]
        }
        - Tên tiếng Việt dùng Hán-Việt chuẩn, viết hoa từng chữ. Mỗi tên tiếng Việt chỉ dùng cho một nhân vật.
        - aliases: các cách gọi tiếng Trung khác của cùng nhân vật (biệt danh, danh xưng), không lặp tên chính.
        - narrativePronoun/secondaryPronoun: đại từ người kể dùng cho nhân vật, hợp bối cảnh (cổ trang: hắn/nàng/lão...;
          hiện đại: anh ấy/cô ấy/cậu ta...). avoidPronouns: các đại từ sai cần tránh cho nhân vật đó.
        - relationships: mỗi dòng là một chiều nói (speaker nói với listener), dùng chineseName để chỉ nhân vật.
          Ghi cho các cặp có hội thoại thực sự; nếu hai chiều xưng hô khác nhau thì ghi cả hai chiều.

        2) glossary.json
        [ { "chinese": "青云宗", "vietnamese": "Thanh Vân Tông" } ]
        - Danh từ riêng và thuật ngữ cố định của truyện: địa danh, môn phái, tổ chức, cảnh giới, công pháp, chiêu thức,
          bảo vật, chức danh, đơn vị. Không đưa tên nhân vật (đã có ở characters.json) và không đưa từ thông thường.
        - Tối đa {{CodexSurveyImport.MaxTerms}} mục, ưu tiên mục xuất hiện nhiều. Mỗi chữ Hán chỉ một mục.

        Kiểm tra cả hai file đọc được bằng JSON parser trước khi kết thúc, rồi trả lời một tóm tắt ngắn
        (số nhân vật, số quan hệ, số thuật ngữ).
        """;

    public static string Repair(string problem) => $"""
        QuickTranslator không dùng được kết quả trước: {problem}
        Hãy sửa characters.json và glossary.json trong thư mục hiện tại cho đúng định dạng đã mô tả trong prompt-0.md
        (đọc lại file đó nếu cần), giữ nguyên nội dung khảo sát đã có, kiểm tra lại bằng JSON parser rồi trả lời tóm tắt ngắn.
        """;
}
