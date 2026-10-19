# Sổ xưng hô · Bước 1 (CPU)

Thiết kế: [docs/ADDRESSING-LEDGER-DESIGN.vi.md](../../docs/ADDRESSING-LEDGER-DESIGN.vi.md).

Công cụ chỉ dùng CPU: **không gọi AI, không ghi vào Project**. Nó sao chép thư mục Project sang `%TEMP%`, chỉ mở bản sao, rồi xóa bản sao khi xong. Báo cáo được ghi vào thư mục `--out`.

```powershell
dotnet build tools/QuickTranslator.AiEditor.Addressing.Probe -c Release
dotnet run --project tools/QuickTranslator.AiEditor.Addressing.Probe -c Release --no-build -- --project "D:\Truyen\Ten\Ten.qtproj" --out artifacts\addressing\ten
```

Tùy chọn: `--samples 12` (số mẫu thoại mỗi cặp), `--min-mentions 3` (tần suất tối thiểu của tên ứng viên từ census).

Đầu ra:

- `addressing-step1.md`: tóm tắt, gợi ý bí danh, bảng cặp (người nói → người nghe) với số lời thoại theo mức tin cậy, tự xưng, hô gọi, 你/您, và mẫu thoại có chương + offset.
- `addressing-step1.json`: cùng dữ liệu, dạng máy đọc, dùng cho Bước 2.

Báo cáo chứa trích đoạn nguyên văn. Đừng commit nó; `artifacts/` đã nằm trong `.gitignore`.

Mức tin cậy:

- Người nói `Explicit`: tên mở đầu mệnh đề dẫn thoại có động từ nói (`…说道：「`), hoặc tên + động từ nói ngay sau `」` trong cùng đoạn. `Pronoun`: chủ ngữ là 他/她, chỉ ghi ứng viên. `Unattributed`: không có dấu hiệu.
- Người nghe `Vocative` (gọi tên trong lời thoại), `Clause` (tên khác duy nhất trong mệnh đề dẫn), `Scene` (tên khác duy nhất trong đoạn hiện tại/trước, yếu).
- Gợi ý bí danh (`小锋 ≈ 柳轻锋`) chỉ để không coi một người là người nghe của chính họ; danh tính không bị gộp.
