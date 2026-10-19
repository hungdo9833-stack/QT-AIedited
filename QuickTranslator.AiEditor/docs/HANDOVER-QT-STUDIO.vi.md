# Bàn giao QT Studio (AI Editor) — 2026-10-20

Tài liệu này tóm tắt trạng thái dự án tại nhánh `codex/abi3-adaptive-semantic-batching`, cách build/chạy bản Windows, kết quả kiểm thử và các lỗi còn tồn tại. Chi tiết từng module nằm trong `README.vi.md`, `docs/ARCHITECTURE.vi.md` và các tài liệu M1–M6.7 / Session 2A–2B.

## 1. Kiến trúc và module

```text
WPF (src/QuickTranslator.AiEditor.Wpf, WinExe net10.0-windows)
  MainWindow.*            Shell, Project, Chapter, Editor 2×2, Reader, Settings, Characters, Glossary
  ImportPreviewWindow     nhập TXT, chia chương, xem trước an toàn
  ChapterManagerWindow    đổi tên, nhân bản, xóa mềm/phục hồi chương
  ProjectTranslationQueueWindow + GpuManagerWindow   hàng đợi dịch nhiều chương, nhiều GPU (Local/Remote Ollama)
  ProjectPublicationWindow + ProjectPdfExporter      xuất TXT UTF-8 / PDF A4
  BookIntelligenceReviewWindow   khảo sát nhân vật bằng AI (Gemini pool / Ollama), corpus audit CPU
  EntityMemoryReviewWindow + SemanticSurveyPanel     Entity Memory/Event Ledger (2A) và AI Semantic Survey (2B)
Core (src/QuickTranslator.AiEditor.Core, net10.0)
  Projects / Persistence   ProjectRepository, manifest *.qtproj, SimpleFileStorage (ghi file tạm rồi replace)
  Importing / Segmentation chia chương, chia đoạn, Verified Chapter Reconstruction
  Translation              Ollama client, ProjectTranslationCoordinator (multi-worker), prompt presets
  Editing / TextCleaning / Traditional   batch replace, chuẩn hóa xưng hô, Hán Việt/VP
  Reading                  ChapterTranslationComposer dùng chung Reader và Export
  BookIntelligence         survey nhân vật, census, Entity Memory, Semantic Survey (contract, engine, auditor, Gemini wire)
```

Luồng UI tới Gemini Semantic Survey: **Thiết lập dịch → Nhân vật & xưng hô → Khảo sát bằng AI** → chọn backend *Gemini Flash Pool* và model → **Entity Memory · Event Ledger (2A)** → tab **AI Semantic Survey (2B)** → *Dry-run* (không gọi AI) → *Khảo sát / Resume AI* (hỏi xác nhận, ngân sách Max HTTP mặc định 5).

## 2. Thay đổi trong đợt tiếp quản

| Commit | Nội dung |
|---|---|
| `c63b403` | Sửa nguyên nhân gốc HTTP 400: Gemini nhận `gemini-semantic-wire-1` (không `anyOf`, không numeric enum, không bound); decode strict về contract v2 rồi Parse/Auditor như cũ. |
| `9a41af4`, `bc7dbc1` | Ghi kết quả gọi Gemini thật; smoke test tổng hợp báo status/hint lỗi provider theo allowlist. |
| `d28ac36` | Sửa cảnh báo nullability làm hỏng build `-warnaserror`; thêm WPF integration test: Semantic Survey panel + `SemanticSurveyBackend.Gemini` thật (HTTP mock) → lưu đề xuất → mở lại Project → không gửi lại. |
| `5b4d674` | **Lỗi cách ly test nghiêm trọng:** test host WPF và `M5.UiSmoke` tạo `App` thật; WPF vẫn gọi `App.OnStartup` nên tạo `MainWindow` thật trên LocalAppData thật, đọc bố cục thật (làm test Dark thất bại khi người dùng chọn Light) và có thể tự mở/lưu Project gần nhất của người dùng. Thêm `App.SuppressStartup` (mặc định false, production không đổi) + regression test. |

| (sau bàn giao) | **Auditor tự định vị câu trích.** Lần chạy Gemini thật đầu tiên trên Project (`gemini-3.5-flash-lite`, HTTP 200, 3 đề xuất) có 2/3 đề xuất `SupportSpanMismatch`: câu trích đúng nhưng model báo sai offset (296 thay vì 922) hoặc dùng LF thay CRLF. `SemanticQualityAuditor` nay tìm câu trích **nguyên văn** trong đúng evidence đã khai (chỉ dung sai CRLF/LF/CR), chọn lần xuất hiện gần offset model báo nhất, rồi lưu offset thật. Câu trích sai chữ, khác khoảng trắng, không có trong evidence hoặc nằm ở evidence khác vẫn bị `SupportSpanMismatch`. |
| (sau bàn giao) | Lần chạy thật thứ hai xác nhận bản sửa (柳轻锋 ở evidence 2: model báo 296, Auditor định vị 922, hợp lệ). Bổ sung: (1) câu trích được **bỏ qua dấu xuống dòng nằm giữa hai ký tự** của nguyên văn (truyện web ngắt dòng cứng giữa câu); xuống dòng có trong câu trích vẫn phải có trong nguyên văn, khoảng trắng không được dung sai. (2) **Resume không lặp:** evidence có ít nhất một đề xuất qua kiểm chứng là `Analyzed` (chờ duyệt), không tự gửi lại; evidence chỉ có đề xuất bị loại vẫn `Failed` và được gửi lại. (3) **Không tạo đề xuất trùng:** cùng nội dung/danh tính/mốc/vị trí chứng cứ/kết quả audit thì chỉ lưu một lần (bỏ qua khác biệt lời giải thích, confidence, ID tạm). |
| (sau bàn giao) | Lần chạy thật thứ ba xác nhận cả ba bản sửa (câu trích qua xuống dòng được chấp nhận, không đề xuất trùng, batch chuyển sang Analyzed/Validated). Đề xuất sự kiện "dreaming" vẫn bị loại đúng vì chỉ trích `他知道自己又在做梦了` (đại từ, không có tên). **Prompt** nay yêu cầu: với EventVerification, RelationshipExtraction và DirectionalAddressing, mỗi nhân vật liên quan phải có một câu trích chứa đúng tên trong nguyên văn; đại từ/danh xưng không chứng minh được là ai, không có thì để Unresolved. Auditor không đổi. `PromptVersion` giữ nguyên để evidence đã khảo sát không bị gửi lại. |

Không sửa pipeline dịch, không đổi schema Project/checkpoint, không xóa dữ liệu.

## 3. Build và bản chạy thử

Yêu cầu: Windows 10/11 x64, .NET 10 SDK (đã kiểm với 10.0.401) để build. Bản phát hành self-contained **không cần** cài .NET.

```powershell
cd QuickTranslator.AiEditor
dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore -warnaserror
dotnet test QuickTranslator.AiEditor.slnx -c Release --no-build
dotnet publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release -r win-x64 --self-contained true -o artifacts/releases/<tên-bản>
```

File chạy: `artifacts/releases/<tên-bản>/QuickTranslator.AiEditor.exe`. Giữ nguyên cả thư mục (runtime .NET, WPF native `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `D3DCompiler_47_cor3.dll`, `vcruntime140_cor3.dll`, PDFsharp/MigraDoc, `THIRD-PARTY-NOTICES.txt`). Không chỉ sao chép riêng EXE. `artifacts/` bị `.gitignore`, bản build không nằm trong Git.

Phụ thuộc bên ngoài (không đóng gói): Ollama + model local (ví dụ `translategemma:12b`) để dịch; Gemini API key (nhập trong **Thiết lập dịch → AI & hiệu năng → Lưu API key**, lưu trong Windows Credential Manager của user hiện tại, không nằm trong Project) cho khảo sát Gemini; Data QuickTranslator (tùy chọn) cho ô Hán Việt/VP.

Bản đã build trong đợt này (máy phát triển, không nằm trong Git):

```text
QuickTranslator.AiEditor\artifacts\releases\QTStudio-win-x64-5b4d674-20261020-015301\QuickTranslator.AiEditor.exe
```

Self-contained win-x64, .NET 10.0.12 (`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`), 276 file, khoảng 152 MB. SHA-256 `QuickTranslator.AiEditor.exe` = `07f13a98…d70bd`.

Mở bằng `QuickTranslator.AiEditor.exe "D:\Truyen\TenTruyen.qtproj"` hoặc chạy không tham số (mở Project gần nhất/Thư viện). Ứng dụng chỉ cho một instance (mutex `Local\QuickTranslatorAiEditor.Phase1`).

## 4. Kết quả kiểm thử và smoke test (2026-10-20, SDK 10.0.401)

| Hạng mục | Kết quả |
|---|---|
| `dotnet restore --locked-mode` | Đạt |
| Release build toàn solution `-warnaserror` | **0 lỗi, 0 cảnh báo** |
| Core tests (`QuickTranslator.AiEditor.Tests`) | **786/786** |
| WPF integration (`QuickTranslator.AiEditor.Wpf.Tests`) | **114/114**; LocalAppData giống hệt từng byte trước/sau khi chạy |
| UI smoke off-screen 1600×900 (Project tổng hợp) | 9/9 chế độ render: two, four, reader, library, chapter, queue, parallel-queue, gpu, titles |
| EXE đã publish, mở Project tổng hợp | Cửa sổ chính hiển thị, Responding, ~364 MB RAM, đóng sạch bằng CloseMainWindow |
| Lưu/mở lại Project (Storage benchmark, 1.000 checkpoint) | `FinalCheckpointExact=true`, mở lại 4,6 ms, không tạo file thừa |
| Hàng đợi 200 chương, 2 worker (provider giả) | 200/200, 200 ChapterId riêng, Local/Remote 101/99, resume/cancel hoạt động |
| Chia chương (ChapterDetector demo) | Đạt |
| Gemini Semantic Survey (mock HTTP) | Wire → decode → Parse → Auditor → đề xuất chưa xác nhận → resume không gửi lại; qua engine, backend và panel WPF |
| Gemini thật (người dùng chạy, 1 request/lần) | Neutral wire **HTTP 200**, JSON hợp lệ. Synthetic survey: **HTTP 503** hai lần |

Khi smoke test EXE, ứng dụng ghi `Projects/library.json` (thêm Project tổng hợp vào danh sách gần đây) và `PromptPresets/presets.qtaip.json` khi đóng. Hai file đã được sao lưu trước và khôi phục đúng từng byte sau khi đóng. Không Project thật nào bị sửa trong đợt này.

## 5. Lỗi / giới hạn còn tồn tại

1. **Gemini Semantic Survey đã chạy thật trên Project** (`gemini-3.5-flash-lite`, HTTP 200, JSON hợp lệ, 3 đề xuất chưa duyệt), nhưng chất lượng đề xuất sau bản sửa định vị câu trích chưa được kiểm chứng bằng lần chạy thật mới. Project "test cpu" đã có 7 đề xuất từ hai lần chạy (một số bị trùng); chúng giữ nguyên, không bị xóa. Chống trùng chỉ áp dụng cho đề xuất mới. Hai evidence của batch đó vẫn `Failed` trong lịch sử cũ, nên lần **Resume** kế tiếp gửi lại batch này **một lần nữa**; sau đó quy tắc mới đánh `Analyzed` và chuyển sang batch tiếp theo. `CrossEvidenceIdentityNeedsReview` (Ambiguous) xuất hiện khi model dùng cùng ID tạm cho một tên ở nhiều evidence; đó là thiết kế chống gộp nhầm người trùng tên, cần người duyệt. Synthetic survey qua probe từng trả HTTP 503 hai lần.
2. **Dữ liệu thật có thể đã bị test cũ chạm tới (trước bản sửa `5b4d674`).** Lúc 00:44:31 ngày 2026-10-20, trùng thời điểm phiên trước chạy WPF tests, Project gần nhất "trao doi gia dinh" (`.qtproj`, `Settings/reader.json`, `Settings/translation.json`, một chương) và `presets.qtaip.json` được ghi. Không có bản sao để đối chiếu, nên chưa xác định là ghi lại y nguyên hay có thay đổi. Nên mở Project đó kiểm tra chương được ghi lúc 00:44 và cấu hình Reader/dịch. Có thể ngày trước đó cũng bị nếu các phiên cũ chạy WPF tests.
3. Dịch bằng Ollama/GPU thật chưa được chạy trong đợt này (chỉ provider giả). Cần kiểm tra trên máy có `translategemma:12b`.
4. `M5.UiSmoke` chụp off-screen: một số cửa sổ con hiện nền trắng sau tiêu đề. EXE thật hiển thị đúng, đây là giới hạn của công cụ chụp.
5. EXE không ký số; Windows SmartScreen có thể cảnh báo lần đầu.
6. `tools/QuickTranslator.AiEditor.GeminiSurvey.Preflight` là công cụ cũ (schema v1, budget/pool khác); dùng `GeminiSchema.Probe` cho kiểm tra Gemini.

## 6. Hướng dẫn sử dụng nhanh

1. Chạy `QuickTranslator.AiEditor.exe`. Lần đầu: **Tệp → Dự án mới**, chọn thư mục cha.
2. **Nhập TXT** (UTF-8, nhiều chương): xem trước ranh giới chương, xác nhận rồi nhập.
3. **Thiết lập dịch → AI & hiệu năng**: chọn Ollama Local/Remote và model, **Kiểm tra kết nối** (chỉ đọc danh sách model). Nhiều GPU: **AI & hiệu năng → Mở Quản lý GPU**.
4. Dịch chương (**Dịch toàn chương**) hoặc cả Project (**Hàng đợi**). Tạm dừng/Tiếp tục an toàn; tiến độ lưu theo từng đoạn.
5. **Nhân vật & xưng hô**, **Từ điển & ngữ cảnh**: hồ sơ, glossary, khóa xưng hô.
6. Gemini: nhập key ở **AI & hiệu năng → Lưu API key** → **Nhân vật & xưng hô → Khảo sát bằng AI** → backend *Gemini Flash Pool* → **Entity Memory · Event Ledger (2A)** → tab **AI Semantic Survey (2B)** → **Dry-run** → **Khảo sát / Resume AI** (giữ Max HTTP nhỏ, ví dụ 1–5). Đề xuất AI phải được duyệt/xác nhận thủ công.
7. **Đọc** (Reader) và **Xuất bản** (TXT UTF-8/PDF A4).
8. Project là một thư mục tự chứa (`*.qtproj` + `Chapters/`, `Settings/`, `Resources/`…). Sao lưu bằng cách sao chép cả thư mục khi ứng dụng đã đóng.
