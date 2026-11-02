namespace QuickTranslator.AiEditor.Core.AutoTranslate;

public static class CodexSurveyPrompt
{
    public static string Survey(string bookTitle, IReadOnlyList<string>? knownGenres = null) => $$"""
        Bạn là biên tập viên dịch truyện Trung → Việt. Thư mục hiện tại có raw.txt: nguyên văn tiếng Trung của truyện “{{bookTitle}}”
        (các chương nối tiếp nhau, mỗi chương bắt đầu bằng dòng tiêu đề). Hãy khảo sát cả bộ và tạo đúng ba file JSON UTF-8
        trong thư mục này để QuickTranslator dùng khi dịch và đăng toàn bộ truyện.

        Cách làm:
        - raw.txt có thể rất dài. Đọc theo từng đoạn bằng lệnh shell (rg, Select-String, Get-Content với -TotalCount/-Skip...),
          đếm tần suất tên để tìm đủ nhân vật chính và nhân vật lặp lại; đọc kỹ các cảnh hội thoại để biết ai xưng hô thế nào với ai.
        - Không sửa raw.txt. Không tạo file nào khác ngoài ba file dưới đây. Không chạy git.

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
        - aliases: các cách gọi tiếng Trung khác của cùng nhân vật (biệt danh, tên gọi thân mật, danh xưng chỉ người đó),
          không lặp tên chính. Alias chỉ dùng để nhận ra nhân vật; cách dịch của danh xưng phải ghi trong glossary.json.
        - setting: "Modern" khi truyện lấy bối cảnh hiện đại (điện thoại, công ty, đô thị...), "Historical" khi cổ trang/tiên hiệp.
        - narrativePronoun/secondaryPronoun: đại từ người kể dùng cho nhân vật, theo tuổi, vai và thái độ người kể.
          Cổ trang: hắn/nàng/chàng/lão... Hiện đại: anh/anh ta, cô/cô ấy, cậu, ông, bà, chị, gã, lão...
          Với truyện "Modern" TUYỆT ĐỐI không dùng hắn, nàng, chàng, y, thị; đưa các từ đó vào avoidPronouns.
          avoidPronouns: các đại từ sai cần tránh cho nhân vật đó.
        - relationships: mỗi dòng là một chiều nói (speaker nói với listener), dùng chineseName để chỉ nhân vật.
          Ghi cho các cặp có hội thoại thực sự; nếu hai chiều xưng hô khác nhau thì ghi cả hai chiều.

        2) glossary.json
        [ { "chinese": "青云宗", "vietnamese": "Thanh Vân Tông" } ]
        - Danh từ riêng và thuật ngữ cố định của truyện: địa danh, môn phái, tổ chức, cảnh giới, công pháp, chiêu thức,
          bảo vật, chức danh, đơn vị. Không đưa tên đầy đủ của nhân vật (đã có ở characters.json) và không đưa từ thông thường.
        - Danh xưng ghép với họ/tên xuất hiện nhiều lần (覃太太, 覃先生, 张总, 王经理, 林少爷, 苏小姐, 老爷, 少奶奶...):
          ghi mỗi dạng một mục với cách dịch thống nhất. Giữ danh xưng Hán-Việt quen thuộc của truyện Trung thay vì Việt hóa
          cứng: phu nhân, thái thái, tiên sinh, lão gia, thiếu gia, thiếu phu nhân, tiểu thư, tổng, quản lý...,
          đặt sau họ theo trật tự tiếng Việt (覃太太 = Đàm phu nhân, 张总 = Trương tổng, 林少爷 = Lâm thiếu gia).
          Không bao giờ đổi danh xưng gốc thành bà/ông/cậu/cô/anh/chị (覃太太 KHÔNG phải "bà Đàm", 覃先生 KHÔNG phải "ông Đàm").
        - Họ của các gia đình/nhân vật chính xuất hiện đứng riêng hoặc ghép (覃, 张...): thêm mục một chữ = họ Hán-Việt
          (覃 = Đàm) để QuickTranslator sửa được chữ Hán còn sót sau khi dịch.
        - Cột vietnamese chỉ chứa tiếng Việt, không còn chữ Hán. Tối đa {{CodexSurveyImport.MaxTerms}} mục, ưu tiên mục
          xuất hiện nhiều. Mỗi chữ Hán chỉ một mục.

        {{BookSection(knownGenres)}}

        Kiểm tra cả ba file đọc được bằng JSON parser trước khi kết thúc, rồi trả lời một tóm tắt ngắn
        (tên tiếng Việt, số nhân vật, số quan hệ, số thuật ngữ, các thể loại).
        """;

    /// <summary>Only book.json, for a book whose characters and glossary were surveyed earlier (raw.txt is still there).</summary>
    public static string BookOnly(string bookTitle, IReadOnlyList<string>? knownGenres = null) => $$"""
        Bạn là biên tập viên truyện Trung → Việt. Thư mục hiện tại có raw.txt (nguyên văn tiếng Trung của truyện “{{bookTitle}}”)
        và characters.json (nhân vật đã khảo sát, dùng đúng tên tiếng Việt trong đó). Đọc đủ raw.txt để nắm cốt truyện,
        rồi chỉ tạo một file JSON UTF-8 trong thư mục này. Không sửa file khác. Không chạy git.

        {{BookSection(knownGenres)}}

        Kiểm tra book.json đọc được bằng JSON parser rồi trả lời ngắn: tên tiếng Việt và các thể loại.
        """;

    private static string BookSection(IReadOnlyList<string>? knownGenres) => $$"""
        3) book.json
        {
          "vietnameseTitle": "Tên truyện tiếng Việt",
          "synopsis": "Đoạn giới thiệu truyện",
          "genres": ["Đô thị", "Ngôn tình", "Sắc"]
        }
        - vietnameseTitle: dịch tên truyện sang tiếng Việt tự nhiên, gợi tò mò, đúng tinh thần truyện; tên riêng dùng Hán-Việt
          giống characters.json. Truyện cổ trang có thể giữ tên Hán-Việt nếu hay hơn. Không thêm ngoặc hay chú thích.
        - synopsis: giới thiệu dài 200–400 từ, viết bằng tiếng Việt cực kỳ cuốn hút để người đọc muốn mở chương 1:
          mở bằng câu móc câu, giới thiệu nhân vật chính và mâu thuẫn trung tâm, gợi không khí và sức hút riêng của truyện,
          kết bằng một câu lửng hoặc câu hỏi. Chỉ dựa trên nội dung thật của truyện, không bịa tình tiết,
          không tiết lộ kết cục. Có thể xuống dòng giữa các đoạn.
        - genres: 3–{{CodexSurveyImport.MaxGenres}} thể loại tiếng Việt ngắn gọn (mỗi thể loại 1–3 từ, viết hoa chữ đầu) để gắn
          tag khi đăng truyện, xếp từ nổi bật nhất. {{KnownGenresRule(knownGenres)}}
        """;

    private static string KnownGenresRule(IReadOnlyList<string>? knownGenres) => knownGenres is { Count: > 0 }
        ? "Ưu tiên dùng đúng cách viết của các thể loại đã có trên trang: " + string.Join(", ", knownGenres) +
          ". Chỉ đặt thể loại mới khi không cái nào ở trên phù hợp."
        : "Dùng tên thể loại phổ biến của truyện mạng Việt Nam (ví dụ Đô thị, Ngôn tình, Cổ đại, Tiên hiệp, Huyền huyễn, Sắc).";

    public static string Repair(string problem) => $"""
        QuickTranslator không dùng được kết quả trước: {problem}
        Hãy sửa characters.json, glossary.json và book.json trong thư mục hiện tại cho đúng định dạng đã mô tả trong prompt-0.md
        (đọc lại file đó nếu cần), giữ nguyên nội dung khảo sát đã có, kiểm tra lại bằng JSON parser rồi trả lời tóm tắt ngắn.
        """;
}
