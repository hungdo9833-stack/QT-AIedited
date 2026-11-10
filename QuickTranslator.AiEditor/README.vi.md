# QT Studio — Biên dịch truyện Trung–Việt

## 1. QT Studio là gì?

QT Studio là ứng dụng Windows dùng WPF, C# và .NET 10 để dịch và biên tập tiểu thuyết Trung → Việt.
Ứng dụng quản lý truyện theo dự án, từ nhập nguyên văn đến khảo sát nhân vật, dịch nhiều chương, soát lỗi và xuất bản.
Dịch có thể chạy hoàn toàn trên máy bằng llama-server sau khi có đủ phần mềm và model; Ollama là backend cũ đã được chuyển sang llama-server trong mã hiện tại, còn Gemini, Codex, máy GPU thuê và đăng web là các lựa chọn có gửi dữ liệu ra ngoài.

## 2. Tính năng chính

- **Tệp → Thư viện dự án…**: mở, tạo và tiếp tục các dự án truyện.
- **Nhập → Từ tệp TXT…**: nhận diện bảng mã, chia chương, xem trước và kiểm tra phần nguồn được nhập.
- **Nhập → Từ website…**: đọc mục lục, tải chương và đưa sang bước xem trước nhập TXT.
- **Nhập → Tự động dịch từ link…**: nhận nhiều link hoặc **Thêm file TXT…**, tạo dự án, khảo sát bằng Codex rồi dịch.
- **Dự án → Quản lý chương…**: quản lý thứ tự, tiêu đề, nhân bản và xóa mềm chương.
- **Dự án → Hàng đợi dịch…**: dịch nhiều chương bằng nhiều worker, tạm dừng và tiếp tục từ tiến độ đã lưu.
- **Cài đặt → Mở Quản lý GPU**: quản lý llama-server trên máy và máy thuê qua SSH, chọn nguồn dịch GPU hoặc API.
- **Dịch & Biên tập → Biên tập / Soát lỗi**: làm việc với hai ô song ngữ hoặc bốn ô Trung, Hán Việt, VietPhrase và AI.
- **Nhân vật & Từ điển → Khảo sát nhân vật bằng AI**: mở **Sổ xưng hô**, khảo sát, kiểm chứng và ghi kết quả vào hồ sơ.
- **Nhân vật & Từ điển**: quản lý thuật ngữ, ngữ cảnh, tên, đại từ và quy tắc xưng hô đã khóa.
- **Kiểm tra & Duyệt → Quét chữ Hán còn sót / Thay thế hàng loạt**: tìm phần cần sửa, áp dụng và hoàn tác.
- **Dự án → Sửa chữ Hán sót cả thư viện…**: sửa các bản dịch đã lưu, giữ bản cũ trong lịch sử và hỗ trợ hoàn tác.
- **Đọc**: ghép bản dịch toàn chương, tìm kiếm, đánh dấu và quay về đoạn tương ứng trong Editor.
- **Xuất bản**: xuất TXT UTF-8, PDF A4, EPUB hoặc chọn **Sắc Viện · Đăng lên web**.
- **Xuất bản → Đăng hàng loạt lên Sắc Viện…**: chọn nhiều truyện trong thư viện, gửi chương còn thiếu hoặc cập nhật.

Luồng tự động tách việc tải, khảo sát và dịch để các truyện có thể tiến hành ở các bước khác nhau.
Codex khảo sát tên, đại từ, quan hệ và thuật ngữ; có thể bổ sung tên Việt, giới thiệu và thể loại truyện.
Tiêu đề chương dùng VietPhrase khi đã nạp Data; nếu chưa nạp, giữ tiêu đề Trung.
Mỗi truyện có thể chọn worker riêng qua **+ Worker**, hoặc theo cấu hình dịch của ứng dụng.
Có **Chỉ chuẩn bị (tải + khảo sát), chưa dịch**, **Thử lại** và **Bỏ** cho hàng đợi tự động.

Nhập web đọc trang qua HTTP, có bộ nhớ đệm và kiểm tra mẫu nội dung.
Trang cần trình duyệt, đăng nhập hoặc bị giới hạn tải có thể làm luồng dừng/chờ; không bảo đảm đọc được mọi website.
Trong **Sổ xưng hô**, kết quả khảo sát chỉ thành hồ sơ dùng khi dịch sau bước **Hoàn thành**; có thể hoàn tác.

## 3. Các backend dịch

| Tên | Chạy ở đâu | Cần gì để dùng |
|---|---|---|
| llama-server Local | Máy Windows đang chạy QT Studio | Gói llama.cpp CUDA, driver NVIDIA phù hợp và model GGUF; ứng dụng có bộ tải và khởi động server. |
| llama-server Remote | Server ngoài máy, qua địa chỉ HTTP(S) hoặc SSH tunnel | Server/model sẵn có; nếu dùng máy thuê qua SSH, cần tài khoản SSH và xác nhận host key. |
| Gemini | Dịch vụ Google | Internet và Gemini API key lưu qua giao diện ứng dụng. |
| Codex | Dịch vụ dùng bởi Codex CLI | Node.js, Codex CLI cài qua npm và workspace Codex đã đăng nhập; chọn workspace/model trong cửa sổ tự động. |
| Ollama Local/Remote (cũ) | Cấu hình của các phiên bản trước | Mã hiện tại chuyển cấu hình Local cũ sang llama-server; không còn client gọi Ollama riêng trong `src/`. |

Hồ sơ GPU Local mặc định dùng `IndexTeam/Index-Translate-9B-GGUF:Q4_K_M`.
Lần thiết lập đầu có thể cần Internet để tải llama.cpp và model; dịch local sau đó dùng server trên loopback.
Máy thuê có luồng nhận diện và thiết lập llama-server cho Windows hoặc Linux (Ubuntu) qua SSH.
Thông tin SSH/token của hồ sơ GPU được bảo vệ bằng Windows DPAPI.

Gemini có cơ chế đổi key/model khi gặp hạn mức; hàng đợi dừng khi không còn tuyến khả dụng.
Gemini và Codex được cấu hình dịch toàn chương; worker GPU dùng thiết lập chia đoạn của hồ sơ.
Sổ xưng hô chọn backend Gemini hoặc llama-server; khảo sát Codex toàn truyện thuộc luồng tự động từ link/TXT.

## 4. Yêu cầu và build

- Windows x64 để chạy giao diện WPF.
- .NET 10 SDK để build và chạy từ mã nguồn; `global.json` chọn dòng SDK 10.0 với `latestFeature`.
- Backend/model, API key hoặc tài khoản tương ứng chỉ cần khi dùng lựa chọn đó.
- Data QuickTranslator là tùy chọn để tra Hán Việt, VietPhrase và dịch tiêu đề bằng từ điển.

Từ gốc repo:

```powershell
cd QuickTranslator.AiEditor
dotnet build QuickTranslator.AiEditor.slnx
dotnet test QuickTranslator.AiEditor.slnx
dotnet run --project src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj
```

Nếu dependencies đã được restore, có thể thêm `--no-restore` khi build và `--no-build` khi test.

Sau khi build, cũng có thể mở `src/QuickTranslator.AiEditor.Wpf/bin/Debug/net10.0-windows/QuickTranslator.AiEditor.exe`.
Truyền đường dẫn `*.qtproj` làm tham số để mở một dự án cụ thể.
Bắt đầu bằng **Tệp → Dự án mới…**, rồi **Nhập → Từ tệp TXT…** hoặc **Từ website…**.
Chọn nguồn dịch trong **Cài đặt**, hoặc dùng **Nhập → Tự động dịch từ link…** để chuẩn bị nhiều truyện.

## 5. Cấu trúc thư mục

- `src/QuickTranslator.AiEditor.Core/`: dự án, lưu trữ, nhập nguồn, dịch, khảo sát, soát chữ và xuất bản; không phụ thuộc WPF.
- `src/QuickTranslator.AiEditor.Wpf/`: giao diện, điều phối thao tác, thông tin xác thực Windows và xuất PDF.
- `tests/QuickTranslator.AiEditor.Tests/`: kiểm thử Core.
- `tests/QuickTranslator.AiEditor.Wpf.Tests/`: kiểm thử tích hợp giao diện WPF.
- `tools/`: các công cụ dưới đây; tên đều có tiền tố `QuickTranslator.AiEditor.`.
- `docs/`: tài liệu kiến trúc, vận hành và bàn giao; báo cáo các mốc cũ ở `docs/archive/`.
- `scripts/Validate.ps1`: kiểm chứng build, test và publish; xem cuối mục này.

Mỗi công cụ trong `tools/` có dự án riêng:

- `Addressing.Probe`: phân tích Sổ xưng hô, chạy offline/mock hoặc khảo sát Gemini có chọn rõ chế độ.
- `AutoTranslate.Probe`: kiểm tra nhập web và chuẩn bị dự án bằng Codex, không dịch nội dung chương.
- `ChapterDetector.Demo`: nhận diện chương và kiểm tra tái tạo nguyên văn từ kế hoạch nhập.
- `CharacterConsistency.Benchmark`: đo bộ chọn quy tắc nhân vật và dựng prompt bằng dữ liệu tổng hợp.
- `Import.Probe`: kiểm tra bảng mã, chương, vùng chưa phân loại và độ bao phủ nguồn TXT.
- `M5.UiSmoke`: dựng và chụp các chế độ giao diện bằng dự án tổng hợp.
- `ProjectCatalog.Benchmark`: đo thao tác danh mục chương trên dự án tổng hợp.
- `Prompt.Probe`: xuất prompt dịch của các đoạn trong dự án để kiểm tra.
- `Pronoun.Probe`: khảo sát các đề xuất chuẩn hóa đại từ trên bản dịch của dự án.
- `QueueWorkflow.Benchmark`: đo luồng hàng đợi nhiều chương bằng provider giả.
- `Storage.Benchmark`: đo lưu checkpoint và mở lại dự án tổng hợp.

`scripts/Validate.ps1` là script kiểm chứng duy nhất: restore, build Release (warning tính là lỗi) và chạy toàn bộ test.
Thêm `-Publish` để thử publish bản self-contained win-x64. Log và bản publish nằm trong thư mục tạm ngoài repo;
bản publish bị xóa sau khi kiểm tra, trừ khi thêm `-KeepPublish`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Validate.ps1 -Publish
```

## 6. Dữ liệu dự án

Một truyện là một thư mục có manifest `*.qtproj`; manifest ghi danh sách chương, tiêu đề và đường dẫn dữ liệu.
Thư mục dự án gồm:

- `Chapters/`: nguyên văn, đoạn, bản dịch, trạng thái và lịch sử trong các file `*.qtaie.json`.
- `Settings/`: thiết lập dịch, đọc, xuất bản và dữ liệu Sổ xưng hô.
- `Resources/`: thuật ngữ, ngữ cảnh và hồ sơ nhân vật của dự án.
- `Queue/`: tiến độ hàng đợi dịch dự án.
- `Deleted/`: dữ liệu chương xóa mềm; `Cache/`: dữ liệu tạm, gồm nguồn nhập web.

Sao lưu cả thư mục dự án khi ứng dụng đã đóng; riêng manifest không chứa toàn bộ bản dịch.
Một số cấu hình ứng dụng nằm trong `%LOCALAPPDATA%/QTStudio/`.
Gemini API key và phiên đăng nhập Sắc Viện nằm trong Windows Credential Manager của người dùng.

Theo `.gitignore`, không commit `bin/`, `obj/`, `TestResults/`, `.vs/`, `artifacts/`, Data/từ điển cá nhân,
`*.qtproj`, `*.qtaie.json`, `*.qtaip.json`, `*.characters.json`, các bản sao/tệp tạm tương ứng,
`Backups/`, `Settings/resources.json*`, `library.json` và `*.user`.
Quy tắc ignore không bao phủ mọi file trong một thư mục truyện; nên đặt dự án và thông tin xác thực ngoài repo.

## 7. Tài liệu

- [Kiến trúc](docs/ARCHITECTURE.vi.md).
- [Bàn giao QT Studio](docs/HANDOVER-QT-STUDIO.vi.md).
- [Thiết kế Sổ xưng hô](docs/ADDRESSING-LEDGER-DESIGN.vi.md).
- [Khóa nhất quán nhân vật](docs/CHARACTER-CONSISTENCY-LOCK-V1.md).
- [Xuất EPUB](docs/EPUB-EXPORT.vi.md).
- [Tái cấu trúc lưu trữ cũ](docs/FULL-LEGACY-STORAGE-REFACTOR.md).
- [Thiết lập EzyCloudX](docs/M6.5-EZYCLOUDX-SETUP.vi.md).
- [Local/Remote Ollama — tài liệu giai đoạn cũ](docs/M6.5-LOCAL-REMOTE-OLLAMA.vi.md).
- [Quản lý nhiều GPU](docs/M6.6-DUAL-GPU-MANAGER.vi.md).
- [Thiết lập GPU Windows](docs/WINDOWS-GPU-AUTO-SETUP.vi.md).
- [Đăng lên Sắc Viện](docs/VANCAC-PUBLISHING.vi.md).
- [Hệ thống thiết kế UI](docs/UI-DESIGN-SYSTEM.vi.md).
- [Rà soát UI/UX tháng 11/2026](docs/UI-UX-AUDIT-2026-11.vi.md).
- [Khảo sát nhiều model Gemini](docs/gemini-multimodel-survey.md).

Báo cáo các mốc cũ nằm trong `docs/archive/`; chúng ghi trạng thái tại thời điểm thực hiện.
