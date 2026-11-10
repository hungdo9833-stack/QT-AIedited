# Bàn giao QT Studio (AI Editor) — 2026-11-10

Tài liệu này cập nhật kiến trúc và thao tác theo mã nguồn hiện có ngày 2026-11-10. Đây là đợt chỉ sửa tài liệu: không build, không chạy test, không gọi backend thật hoặc đăng lên website. Các bản build, số liệu kiểm thử và lỗi ghi từ đợt trước được giữ nguyên với nhãn **trạng thái ngày 2026-10-20, chưa kiểm lại**.

Xem [README](../README.vi.md), [kiến trúc](ARCHITECTURE.vi.md) và [thiết kế Sổ xưng hô](ADDRESSING-LEDGER-DESIGN.vi.md). Báo cáo M1–M6.7 và Session 2A–2B đã chuyển vào `docs/archive/`; ví dụ [xuất bản dự án M6.7](archive/M6.7-PROJECT-PUBLICATION.vi.md) và [bàn giao Session 2](archive/ai-book-intelligence/SESSION_2_HANDOFF.md). Tên nhánh `codex/abi3-adaptive-semantic-batching` trong bản bàn giao trước chỉ là thông tin lịch sử.

## 1. Kiến trúc và module

```text
WPF (src/QuickTranslator.AiEditor.Wpf, WinExe net10.0-windows)
  MainWindow.*                 shell, dự án, biên tập song ngữ/bốn ô, Đọc, Cài đặt, Nhân vật & Từ điển
  ImportPreviewWindow          nhập TXT, nhận diện bảng mã, chia chương, kiểm tra bao phủ nguồn
  WebImportWindow              phân tích link, tải chương, chuyển sang xem trước nhập
  AutoTranslateWindow          hàng đợi link/TXT, chọn worker từng truyện, Thử lại/Bỏ
  MainWindow.AutoTranslate + AutoBookPipeline   tải → tạo dự án/tiêu đề → Codex khảo sát → dịch
  ChapterManagerWindow         danh mục, tiêu đề, nhân bản, xóa mềm/phục hồi chương
  ProjectTranslationQueueWindow + GpuManagerWindow   hàng đợi nhiều chương/worker, cấu hình GPU/API
  AddressingLedgerWindow       Sổ xưng hô: khảo sát, kiểm chứng, Hoàn thành, Hoàn tác
  HanSweepWindow               sửa chữ Hán sót trong các truyện đã lưu ở thư viện, có hoàn tác
  ProjectPublicationWindow/View + ProjectPdfExporter   xuất TXT, PDF A4, EPUB, chuyển sang đăng web
  VanCacAccountWindow / VanCacNovelsWindow / VanCacPublishWindow   tài khoản, truyện và đăng Sắc Viện
  VanCacBatchPublishWindow      đăng nhiều dự án trong thư viện, chọn chương thiếu trên web
Core (src/QuickTranslator.AiEditor.Core, net10.0; không phụ thuộc WPF)
  Projects / Persistence       manifest *.qtproj, chương/tài nguyên, lưu tệp tạm rồi thay thế, phục hồi
  Importing / Segmentation     nhận diện/tái tạo chương, xem trước nhập, chia/tách/gộp đoạn
  WebImport                    mục lục, trích nội dung, ghép trang, cache, kiểm tra mẫu và giới hạn tải
  AutoTranslate                AutoBookPreparer, CodexSurveyRunner, chuyển khảo sát thành hồ sơ/thuật ngữ
  Translation                  ILlmClient, llama-server Local/Remote, GeminiTranslationClient, CodexTranslationClient
  Translation (GPU/SSH)        LocalLlamaServerHost, GpuBackends, GpuConnectionManager, RemoteLlamaSetup
  Projects (hàng đợi)          ProjectTranslationCoordinator, nhiều worker, tiến độ và tiếp tục sau gián đoạn
  Addressing                   phân tích CPU, khảo sát AI, kiểm chứng bằng nguồn, ghi hồ sơ và hoàn tác
  BookIntelligence             census tên, backend khảo sát, dữ liệu khảo sát cũ làm ngữ cảnh nếu đã bật
  Editing / TextCleaning       thay thế hàng loạt, chuẩn hóa xưng hô, quét/sửa chữ Hán sót
  Traditional                  nạp Data QuickTranslator, Hán Việt/VietPhrase, đối chiếu lựa chọn
  Reading                      tài liệu đọc, ánh xạ về đoạn, tùy chọn hiển thị
  Projects (xuất bản)          ProjectPublicationService: TXT/EPUB, cung cấp chương cho PDF và đăng web
  Publishing                   VanCacPublisherClient / VanCacPublishRunner: đăng nhập, truyện, gửi chương
```

**Backend hiện tại.** Hồ sơ GPU dùng llama-server và model GGUF; Local mặc định là `IndexTeam/Index-Translate-9B-GGUF:Q4_K_M`. `LlamaServerClient` gọi `/v1/chat/completions`, dù một số trường endpoint còn giữ dạng `/api/chat` để tương thích. `GpuBackends.cs` đọc cấu hình Ollama cũ và chuyển hồ sơ Local sang llama-server; hiện không có client Ollama riêng trong `src/`. Không dùng hướng dẫn Ollama cũ để suy ra backend đang chạy.

`LocalLlamaServerHost` tải gói llama.cpp và khởi động server trên loopback. `GpuConnectionManager` quản lý tunnel SSH, kiểm tra kết nối và thử kết nối lại có giới hạn. `RemoteLlamaSetup` nhận diện Windows/Linux (Ubuntu), kiểm tra GPU NVIDIA và thiết lập llama-server trên máy thuê. `GeminiTranslationClient` quản lý key/model và hạn mức; `CodexTranslationClient` gọi CLI trong workspace đã đăng nhập. Gemini và Codex được UI cấu hình dịch toàn chương; GPU theo cấu hình chia đoạn của hồ sơ. Local có thể dịch hoàn toàn trên máy khi đã chuẩn bị đủ gói/model; Gemini, Codex, máy thuê và đăng web gửi dữ liệu ra ngoài.

**Hàng đợi và tự động.** `ProjectTranslationCoordinator` chia chương cho nhiều worker, lưu tiến độ và bảo vệ đoạn đã sửa/duyệt theo lựa chọn dịch lại. `AutoBookPreparer` nhận web hoặc TXT, tạo dự án, dịch tiêu đề bằng VietPhrase nếu đã nạp Data, rồi dùng Codex khảo sát nhân vật/thuật ngữ và bổ sung tên Việt, giới thiệu, thể loại. `AutoBookPipeline` tách bước tải, khảo sát và dịch; cho phép tối đa 3 lượt khảo sát đồng thời theo hằng số trong mã. Truyện có thể chọn worker riêng; các truyện dùng chung tài nguyên dịch chờ nhau. Đây là cấu hình điều phối, không phải số đo hiệu năng.

**Nhập web.** `WebImport` đọc HTTP, phát hiện mục lục, trích và ghép trang, bỏ thành phần rác, lưu cache và kiểm tra mẫu. Luồng báo khi cần trình duyệt/đăng nhập hoặc bị giới hạn tải. Luồng tự động giữ cache để tải tiếp; link có dự án đã khảo sát và đủ chương có thể tiếp tục bản dịch đã có.

**Sổ xưng hô.** Mở **Nhân vật & Từ điển → Khảo sát nhân vật bằng AI** → cửa sổ **QT Studio · Sổ xưng hô** → chọn Gemini hoặc hồ sơ llama-server → **Khảo sát / chạy tiếp** → **Hoàn thành: ghi tất cả vào hồ sơ và dùng khi dịch**. Có **Hoàn tác lần Hoàn thành gần nhất**; quy tắc đã khóa được bảo vệ. Quy tắc xưng hô có mốc chương và đại từ lời kể theo giai đoạn.

UI khảo sát cũ không còn là lối vào chính. Mã và mô hình `BookIntelligence` vẫn tồn tại; khi `BookKnowledgeContextEnabled` đã bật, điều phối dịch vẫn đọc ngữ cảnh từ `Settings/ai-book-intelligence.json`. Không diễn giải việc thay luồng UI thành đã xóa toàn bộ mã khảo sát cũ.

**Soát và xuất bản.** `HanSweepWindow` chạy sửa chữ Hán sót trên thư viện, lưu lịch sử thay thế và hỗ trợ hoàn tác; việc dịch lại đoạn dùng llama-server local khi đang sẵn sàng. Reader và các đầu ra bản dịch dùng chung `ChapterTranslationComposer`. Nhóm cửa sổ `VanCac*` hiện mang nhãn **Sắc Viện**: đăng nhập tài khoản, liên kết/tạo truyện, gửi chương theo thứ tự. Cửa sổ đăng hàng loạt có tùy chọn **Chỉ gửi chương chưa có trên web**, trạng thái bản nháp/xuất bản ngay và dừng tác vụ. Xem [tài liệu đăng web](VANCAC-PUBLISHING.vi.md), [xuất EPUB](EPUB-EXPORT.vi.md) và [hệ thống UI](UI-DESIGN-SYSTEM.vi.md).

## 2. Thay đổi trong đợt tiếp quản

Các dòng tiếp quản cũ dưới đây là **trạng thái ngày 2026-10-20, chưa kiểm lại**. Những câu “sau bàn giao”, “chưa nối UI” hoặc “chưa gọi AI thật” được giữ như ghi chép lịch sử, không mô tả lại trạng thái hiện tại ở mục 1.

| Commit | Nội dung |
|---|---|
| `c63b403` | Sửa nguyên nhân gốc HTTP 400: Gemini nhận `gemini-semantic-wire-1` (không `anyOf`, không numeric enum, không bound); decode strict về contract v2 rồi Parse/Auditor như cũ. |
| `9a41af4`, `bc7dbc1` | Ghi kết quả gọi Gemini thật; smoke test tổng hợp báo status/hint lỗi provider theo allowlist. |
| `d28ac36` | Sửa cảnh báo nullability làm hỏng build `-warnaserror`; thêm WPF integration test: Semantic Survey panel + `SemanticSurveyBackend.Gemini` thật (HTTP mock) → lưu đề xuất → mở lại Project → không gửi lại. |
| `5b4d674` | **Lỗi cách ly test nghiêm trọng:** test host WPF và `M5.UiSmoke` tạo `App` thật; WPF vẫn gọi `App.OnStartup` nên tạo `MainWindow` thật trên LocalAppData thật, đọc bố cục thật (làm test Dark thất bại khi người dùng chọn Light) và có thể tự mở/lưu Project gần nhất của người dùng. Thêm `App.SuppressStartup` (mặc định false, production không đổi) + regression test. |
| (sau bàn giao) | **Auditor tự định vị câu trích.** Lần chạy Gemini thật đầu tiên trên Project (`gemini-3.5-flash-lite`, HTTP 200, 3 đề xuất) có 2/3 đề xuất `SupportSpanMismatch`: câu trích đúng nhưng model báo sai offset (296 thay vì 922) hoặc dùng LF thay CRLF. `SemanticQualityAuditor` nay tìm câu trích **nguyên văn** trong đúng evidence đã khai (chỉ dung sai CRLF/LF/CR), chọn lần xuất hiện gần offset model báo nhất, rồi lưu offset thật. Câu trích sai chữ, khác khoảng trắng, không có trong evidence hoặc nằm ở evidence khác vẫn bị `SupportSpanMismatch`. |
| (sau bàn giao) | Lần chạy thật thứ hai xác nhận bản sửa (柳轻锋 ở evidence 2: model báo 296, Auditor định vị 922, hợp lệ). Bổ sung: (1) câu trích được **bỏ qua dấu xuống dòng nằm giữa hai ký tự** của nguyên văn (truyện web ngắt dòng cứng giữa câu); xuống dòng có trong câu trích vẫn phải có trong nguyên văn, khoảng trắng không được dung sai. (2) **Resume không lặp:** evidence có ít nhất một đề xuất qua kiểm chứng là `Analyzed` (chờ duyệt), không tự gửi lại; evidence chỉ có đề xuất bị loại vẫn `Failed` và được gửi lại. (3) **Không tạo đề xuất trùng:** cùng nội dung/danh tính/mốc/vị trí chứng cứ/kết quả audit thì chỉ lưu một lần (bỏ qua khác biệt lời giải thích, confidence, ID tạm). |
| (sau bàn giao) | Lần chạy thật thứ ba xác nhận cả ba bản sửa (câu trích qua xuống dòng được chấp nhận, không đề xuất trùng, batch chuyển sang Analyzed/Validated). Đề xuất sự kiện "dreaming" vẫn bị loại đúng vì chỉ trích `他知道自己又在做梦了` (đại từ, không có tên). **Prompt** nay yêu cầu: với EventVerification, RelationshipExtraction và DirectionalAddressing, mỗi nhân vật liên quan phải có một câu trích chứa đúng tên trong nguyên văn; đại từ/danh xưng không chứng minh được là ai, không có thì để Unresolved. Auditor không đổi. `PromptVersion` giữ nguyên để evidence đã khảo sát không bị gửi lại. |
| (sau bàn giao) | **Đổi hướng khảo sát:** đánh giá cho thấy 2A/2B không nối vào prompt dịch và khảo sát theo cửa sổ không mở rộng được. Hướng mới là **sổ xưng hô theo cặp nhân vật** ([ADDRESSING-LEDGER-DESIGN.vi.md](ADDRESSING-LEDGER-DESIGN.vi.md)). 2A/2B **tạm dừng mở rộng**, dữ liệu giữ nguyên. Đã làm Bước 1 (CPU: tách lời thoại, gán người nói/nghe theo mức tin cậy, gom theo cặp) và công cụ `tools/QuickTranslator.AiEditor.Addressing.Probe`; chưa nối vào UI hay dịch. |
| (sau bàn giao) | **Sổ xưng hô Bước 2–4.**<br>• Bước 2: khảo sát AI theo cặp (xưng hô trong thoại) và theo nhân vật (đại từ lời kể, biến thể tên), chỉ chạy với mock.<br>• Bước 3: cửa sổ **Sổ xưng hô** để duyệt và ghi vào hồ sơ nhân vật, có kiểm tra revision và không ghi đè quy tắc đã khóa.<br>• Bước 4: quy tắc theo mốc chương (`EffectiveFromChapter/ToChapter`, `NarrativePhases`) và theo nhân vật có mặt trong cảnh (2 đoạn trước) khi dựng prompt dịch. Dữ liệu cũ không có mốc chương vẫn hoạt động như trước.<br>Chưa gọi AI thật cho sổ xưng hô. |

Ghi chú của đợt cũ — **trạng thái ngày 2026-10-20, chưa kiểm lại**: “Không sửa pipeline dịch, không đổi schema Project/checkpoint, không xóa dữ liệu.”

Các bổ sung sau mốc bàn giao được đối chiếu với mã nguồn hiện tại và `git log --oneline -60`:

| Commit | Nội dung hiện có trong mã |
|---|---|
| `3a809e8`, `d3633ac` | Tự động từ link đến dự án đã khảo sát bằng Codex; bổ sung tên truyện, giới thiệu và thể loại. |
| `875e0fd`, `830df13`, `02bfcea` | TXT tham gia hàng đợi, tiếp tục link đã có dự án, Thử lại/Bỏ và chờ tải lại khi bị giới hạn. |
| `453a830` | Điều phối tối đa 3 khảo sát Codex đồng thời. |
| `3efa67c` | Hàng đợi theo cấu hình GPU hiện tại khi thay máy thuê. |
| `4e01e2d`, `068e31a` | Đăng nhập Sắc Viện qua tài khoản, bỏ lối dán mã kết nối thủ công. |
| `2d1fcfa` | Worker dịch Codex, sửa chữ Hán sót cả thư viện, đăng hàng loạt và sửa luồng hàng đợi. |
| `8155993`, `b9b41cc`, `504beba` | Menu theo công việc, thuật ngữ Việt, token giao diện và kiểu cửa sổ dùng chung. |
| `be220e0`, `2d20740` | Báo tiến độ đồng bộ và chuyển báo cáo mốc đã xong vào `docs/archive/`. |

Bảng này ghi tính năng và thay đổi đã thấy trong mã/lịch sử; không xác nhận kết quả chạy thực tế của các commit.

## 3. Build và bản chạy thử

Yêu cầu hiện tại: Windows x64 và .NET 10 SDK để build/chạy WPF. `global.json` dùng SDK `10.0.100` với `rollForward: latestFeature`. Thông tin “đã kiểm với SDK 10.0.401” thuộc **trạng thái ngày 2026-10-20, chưa kiểm lại**.

Lệnh hướng dẫn hiện tại, chạy từ thư mục gốc repo; đợt cập nhật tài liệu này không thực thi:

```powershell
cd QuickTranslator.AiEditor
dotnet build QuickTranslator.AiEditor.slnx --no-restore -nologo -v q
dotnet test QuickTranslator.AiEditor.slnx --no-build -nologo -v q
dotnet run --project src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj --no-build
```

Không chạy restore trong sandbox theo `AGENTS.md`. Nếu kiểm chứng publish, xuất vào thư mục tạm ngoài repo rồi xóa sau khi kiểm tra; không xuất bản kiểm chứng vào `artifacts/`. `artifacts/releases/` chỉ dành cho bản giao người dùng, giữ 3 bản mới nhất. `scripts/Validate.ps1` chưa tồn tại trong worktree này; hiện chỉ có các script kiểm chứng theo mốc, chưa xác nhận được quy trình script duy nhất build/test/publish tạm.

Khối lệnh dưới đây được giữ nguyên như ghi chép của đợt cũ — **trạng thái ngày 2026-10-20, chưa kiểm lại**; đường dẫn publish trong khối không phải hướng dẫn kiểm chứng hiện tại:

```powershell
cd QuickTranslator.AiEditor
dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore -warnaserror
dotnet test QuickTranslator.AiEditor.slnx -c Release --no-build
dotnet publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release -r win-x64 --self-contained true -o artifacts/releases/<tên-bản>
```

Bố cục bản chạy đã ghi nhận — **trạng thái ngày 2026-10-20, chưa kiểm lại**: `artifacts/releases/<tên-bản>/QuickTranslator.AiEditor.exe`. Giữ nguyên cả thư mục (runtime .NET, WPF native `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `D3DCompiler_47_cor3.dll`, `vcruntime140_cor3.dll`, PDFsharp/MigraDoc, `THIRD-PARTY-NOTICES.txt`). Không chỉ sao chép riêng EXE. `artifacts/` bị `.gitignore`, bản build không nằm trong Git.

Phụ thuộc bên ngoài hiện tại (không đóng gói trong ứng dụng): gói llama.cpp/model GGUF cho local; endpoint hoặc SSH và model trên máy thuê cho remote; Gemini API key khi dùng Gemini; Node.js và Codex CLI cùng workspace đã đăng nhập khi dùng Codex. Data QuickTranslator là tùy chọn cho Hán Việt/VietPhrase và tiêu đề. Gemini key và phiên đăng nhập Sắc Viện lưu trong Windows Credential Manager; bí mật hồ sơ GPU được bảo vệ bằng DPAPI. Ollama/`translategemma:12b` trong khối lịch sử không phải điều kiện mặc định của mã hiện tại.

Bản đã build trong đợt cũ (máy phát triển, không nằm trong Git) — **trạng thái ngày 2026-10-20, chưa kiểm lại**:

```text
QuickTranslator.AiEditor\artifacts\releases\QTStudio-win-x64-5b4d674-20261020-015301\QuickTranslator.AiEditor.exe
```

Self-contained win-x64, .NET 10.0.12 (`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`), 276 file, khoảng 152 MB. SHA-256 `QuickTranslator.AiEditor.exe` = `07f13a98…d70bd`.

Mã khởi động hiện vẫn nhận đường dẫn dự án: `QuickTranslator.AiEditor.exe "D:\Truyen\TenTruyen.qtproj"` hoặc chạy không tham số (mở Project gần nhất/Thư viện). Ứng dụng chỉ cho một instance (mutex `Local\QuickTranslatorAiEditor.Phase1`).

## 4. Kết quả kiểm thử và smoke test (2026-10-20, SDK 10.0.401)

Toàn bộ kết quả và ghi chép trong mục này là **trạng thái ngày 2026-10-20, chưa kiểm lại**. Giữ nguyên số liệu; không dùng làm kết quả kiểm chứng cho mã ngày 2026-11-10.

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

Các lỗi và giới hạn dưới đây được giữ từ bàn giao cũ. Mỗi mục là **trạng thái ngày 2026-10-20, chưa kiểm lại**; chưa xác nhận còn tái hiện trên mã hiện tại.

1. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** **Gemini Semantic Survey đã chạy thật trên Project** (`gemini-3.5-flash-lite`, HTTP 200, JSON hợp lệ, 3 đề xuất chưa duyệt), nhưng chất lượng đề xuất sau bản sửa định vị câu trích chưa được kiểm chứng bằng lần chạy thật mới. Project "test cpu" đã có 7 đề xuất từ hai lần chạy (một số bị trùng); chúng giữ nguyên, không bị xóa. Chống trùng chỉ áp dụng cho đề xuất mới. Hai evidence của batch đó vẫn `Failed` trong lịch sử cũ, nên lần **Resume** kế tiếp gửi lại batch này **một lần nữa**; sau đó quy tắc mới đánh `Analyzed` và chuyển sang batch tiếp theo. `CrossEvidenceIdentityNeedsReview` (Ambiguous) xuất hiện khi model dùng cùng ID tạm cho một tên ở nhiều evidence; đó là thiết kế chống gộp nhầm người trùng tên, cần người duyệt. Synthetic survey qua probe từng trả HTTP 503 hai lần.
2. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** **Dữ liệu thật có thể đã bị test cũ chạm tới (trước bản sửa `5b4d674`).** Lúc 00:44:31 ngày 2026-10-20, trùng thời điểm phiên trước chạy WPF tests, Project gần nhất "trao doi gia dinh" (`.qtproj`, `Settings/reader.json`, `Settings/translation.json`, một chương) và `presets.qtaip.json` được ghi. Không có bản sao để đối chiếu, nên chưa xác định là ghi lại y nguyên hay có thay đổi. Nên mở Project đó kiểm tra chương được ghi lúc 00:44 và cấu hình Reader/dịch. Có thể ngày trước đó cũng bị nếu các phiên cũ chạy WPF tests.
3. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** Dịch bằng Ollama/GPU thật chưa được chạy trong đợt này (chỉ provider giả). Cần kiểm tra trên máy có `translategemma:12b`.
4. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** `M5.UiSmoke` chụp off-screen: một số cửa sổ con hiện nền trắng sau tiêu đề. EXE thật hiển thị đúng, đây là giới hạn của công cụ chụp.
5. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** EXE không ký số; Windows SmartScreen có thể cảnh báo lần đầu.
6. **Trạng thái ngày 2026-10-20, chưa kiểm lại.** Kiểm tra khảo sát offline dùng `tools/QuickTranslator.AiEditor.Addressing.Probe` (các công cụ cũ `GeminiSurvey.Preflight`, `GeminiSchema.Probe`, `BookIntelligence.Benchmark` đã bị gỡ).

Điểm cần kiểm chứng ở lần chạy sau: backend local/remote, Gemini và Codex thật; đăng nhập/đăng đơn/đăng hàng loạt trên Sắc Viện; luồng tự động trên website đích; hoàn tác sửa chữ Hán. Đợt tài liệu này chỉ xác nhận có mã và đường đi UI, không xác nhận chất lượng dịch, tốc độ, hạn mức dịch vụ, bản EXE cụ thể hay tình trạng dữ liệu cá nhân từng được nêu ở trên.

## 6. Hướng dẫn sử dụng nhanh

1. Chạy `QuickTranslator.AiEditor.exe`; tạo truyện qua **Tệp → Dự án mới…**, hoặc mở `*.qtproj` bằng **Tệp → Mở dự án…**.
2. **Nhập → Từ tệp TXT…**: chọn nguồn/bảng mã, xem trước chương và xác nhận nhập. **Nhập → Từ website…** cần dự án đang mở; tải xong chuyển sang cùng bước xem trước.
3. **Cài đặt → Mở Quản lý GPU**: chuẩn bị llama-server Local hoặc hồ sơ Remote/SSH và chọn nguồn dịch. Dùng Gemini thì thêm key; dùng Codex thì workspace CLI phải đã đăng nhập.
4. Để chuẩn bị nhiều truyện, dùng **Nhập → Tự động dịch từ link…**, dán link hoặc **Thêm file TXT…**. Chọn workspace/model Codex, thư mục dự án và **+ Worker** nếu cần chỉ định nguồn dịch từng truyện; bấm **Bắt đầu**. Có chế độ **Chỉ chuẩn bị (tải + khảo sát), chưa dịch**, **Thử lại** và **Bỏ**.
5. Trong Editor, dùng **Thêm ▾ → Dịch chương** hoặc **Dự án → Hàng đợi dịch…** cho nhiều chương. **Tạm dừng**, **Tiếp tục**, **Dịch lại lỗi** theo trạng thái; tiến độ lưu trong dự án.
6. **Nhân vật & Từ điển**: sửa thuật ngữ/ngữ cảnh/hồ sơ; **Khảo sát nhân vật bằng AI** mở Sổ xưng hô. Chọn backend → **Khảo sát / chạy tiếp** → **Hoàn thành: ghi tất cả vào hồ sơ và dùng khi dịch**; có hoàn tác.
7. **Kiểm tra & Duyệt**: quét chữ Hán, xưng hô, thay thế hàng loạt và duyệt đoạn/chương. Với bản dịch đã lưu trong thư viện, dùng **Dự án → Sửa chữ Hán sót cả thư viện…**; cửa sổ có **Hoàn tác các lần sửa trước**.
8. **Đọc** để kiểm tra toàn chương. **Xuất bản** để xuất TXT/PDF/EPUB hoặc chọn **Sắc Viện · Đăng lên web**. Đăng nhập ở **Xuất bản → Tài khoản Sắc Viện…**; nhiều truyện dùng **Xuất bản → Đăng hàng loạt lên Sắc Viện…**.
9. Dự án là cả thư mục `*.qtproj` + `Chapters/`, `Settings/`, `Resources/`, `Queue/`, `Deleted/`, `Cache/`. Sao lưu cả thư mục khi ứng dụng đã đóng. Giữ dự án/từ điển/thông tin xác thực ngoài repo; xem `.gitignore` và [README](../README.vi.md).
