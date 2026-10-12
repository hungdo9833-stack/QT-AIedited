# M1 — Chapter Detection Core

Ngày nghiệm thu kỹ thuật: 12/10/2026

Branch: `codex/m1-chapter-detection`

Điểm nền: `b511687751ea6ca15cb6bd5aece51bd71bc8aa01` (`codex/data-safety-phase3`)

## Phạm vi

M1 chỉ bổ sung Core detector, tests và demo console. Không ghi Project, không thay schema, không thêm Import Preview, queue nhiều chapter hoặc UI production. Ollama, TranslateGemma, TraditionalTranslator, VietPhrase và Reader không thay đổi.

## Pipeline

`RawSourceSnapshot → UnicodeLineScanner → recognizers → CandidateClassifier → ChapterSequenceAnalyzer → BoundaryPlanner → ImmutableImportPlan`

- Snapshot giữ raw bytes bất biến, text, bảng mã được chọn, SHA-256 raw/text và kiểu newline.
- Scanner ghi offset UTF-16 riêng cho nội dung dòng, terminator và toàn dòng; hỗ trợ CRLF, LF, CR và mixed.
- Recognizer tách riêng Chinese ordinal, Latin/Vietnamese numbered và special chapter; quyết định cuối còn dùng classifier/evidence, không dựa vào một regex duy nhất.
- Candidate công bố raw heading, kind, number, title, recognizer, confidence, quyết định và toàn bộ evidence có trọng số.
- Sequence được phân tích độc lập theo từng volume. Detector không tự sửa hoặc đánh lại số.
- Boundary plan phân vùng liên tục từ offset 0 đến cuối nguồn. Preamble/vùng không nhận diện được giữ dưới dạng `Unclassified`.
- `ValidateExactCoverage` xác minh không hở/chồng/rỗng và ghép lại đúng từng ký tự nguồn.

## Định dạng được nhận diện

- Số Hán giản thể/phồn thể, `两/兩`, `〇/零`, `廿/卅`, số Ả Rập và full-width digits.
- `第…章`, `第…回`, `第…节/節`; có thể có khoảng trắng, bracket, `正文` prefix, có hoặc không có tên.
- `Chapter`, `Chương`, `Chuong` + số.
- Volume `第…卷/部/篇/集` và `卷/部/篇/集 + số`; chapter được phép bắt đầu lại từ 1 sau volume.
- `序章`, `楔子`, `引子`, `前言`, `番外/番外篇`, `终章/終章`, `尾声/尾聲`, `后记/後記`, `大结局/大結局`.

## Encoding

- Auto chỉ chấp nhận UTF-8 nghiêm ngặt, có hoặc không có BOM.
- GBK (CP936) và GB18030 phải được caller chọn rõ. Decoder dùng exception fallback; không chèn ký tự thay thế.
- Khi Auto gặp bytes không phải UTF-8, API báo lỗi và yêu cầu chọn GBK/GB18030 thay vì tự đoán.
- Raw SHA-256 luôn tính trên bytes đầu vào; text SHA-256 tính trên biểu diễn UTF-8 hợp lệ của text đã decode.

## Kiểm thử

- Baseline trước sửa: 392 Core + 34 WPF = 426 đạt, Release 0 lỗi/0 cảnh báo.
- Red tests trước implementation: 41 fail / 0 pass.
- M1 targeted cuối: 51 pass, gồm golden corpus, sequence, false positives, volume reset, exact offsets, raw/text hash, UTF-8/GBK/GB18030, invalid Unicode, property round-trip, chapter ngắn/dài và 5.000 chapter.
- Full regression sau M1: 443 Core + 34 WPF = 477 tests đạt, 0 fail, 0 skipped.
- Demo synthetic Windows: 5.000 chapter + 5 volume, 5.005 candidate accepted, 0 uncertain/rejected, exact round-trip `true`; thời gian tham chiếu khoảng 45 ms trên máy phát triển.

NuGet audit cần Internet và môi trường sandbox không truy cập được endpoint audit. Nghiệm thu Release dùng locked restore với `NuGetAudit=false` cho invocation, sau đó build `--no-restore --no-incremental`; package graph vẫn bị khóa bởi `packages.lock.json`.

## Demo

Project: `tools/QuickTranslator.AiEditor.ChapterDetector.Demo`.

```powershell
QTStudio.ChapterDetector.Demo.exe --synthetic 5000
QTStudio.ChapterDetector.Demo.exe source.txt
QTStudio.ChapterDetector.Demo.exe source.txt --encoding gb18030
```

Demo chỉ đọc nguồn và in JSON; không mở hoặc ghi Project.

## Giới hạn chủ đích của M1

- Bare numeric headings như `001 - Tên` chưa tự nhận vì có tỷ lệ false positive cao; cần corpus/thao tác xác nhận ở M2.
- Roman numerals, tiêu đề OCR hỏng và tiêu đề bị tách qua nhiều dòng được giữ nguyên nhưng có thể nằm trong vùng unclassified.
- Dòng giống tiêu đề nhưng mang dấu hiệu câu văn được `Rejected` hoặc `NeedsReview`; Import Preview M2 sẽ cho người dùng sửa quyết định.
- Confidence là heuristic có evidence rõ, chưa phải mô hình học máy.
- Offset công khai là UTF-16 để khớp .NET/WPF. Raw bytes và hash vẫn được giữ cho truy vết encoding.
- M1 chưa ghi chapter, manifest hoặc source rescue vào Project.
