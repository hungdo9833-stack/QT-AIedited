# QT Studio (QuickTranslator.AiEditor): quy tắc cho Codex

Ứng dụng WPF dịch tiểu thuyết Trung → Việt, C# / .NET 10. Mã nguồn nằm trong `QuickTranslator.AiEditor/`
(solution `QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx`). Hướng dẫn cho Claude nằm ở `CLAUDE.md`.

## Phạm vi

- Chỉ sửa các file mà prompt giao việc cho phép. Không đổi tên, format lại hay dọn dẹp file ngoài phạm vi.
- Không động vào `artifacts/` và `.claude/`.
- Không chạy lệnh git ghi dữ liệu (`add`, `commit`, `checkout`, `stash`, `reset`): sandbox khóa `.git`. Claude review diff rồi commit.

## Build và test

- Không chạy `dotnet restore` (sandbox không đọc được NuGet.Config). Claude đã restore trước.
- Build: `dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx --no-restore -nologo -v q`
- Test: `dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx --no-build -nologo -v q` (khoảng 1,5 phút).
  Khi chỉ sửa một vùng, chạy trước với `--filter "FullyQualifiedName~<TênLớpTest>"`, rồi chạy cả bộ một lần cuối.
- Build phải 0 warning. Nếu không ghi được `bin/` hoặc `obj/` (access denied), báo lại thay vì build sang thư mục khác.

## Bản publish

- Bản build chỉ để kiểm chứng: publish vào thư mục tạm ngoài repo, kiểm tra xong thì xóa. Không publish vào `artifacts/`.
- `QuickTranslator.AiEditor/artifacts/releases/` chỉ chứa bản giao cho user; giữ 3 bản mới nhất.

## Mã nguồn

- Core (`src/QuickTranslator.AiEditor.Core`) không phụ thuộc WPF. Trong Core, báo tiến độ bằng `DirectProgress<T>`,
  không dùng `new Progress<T>` (callback chạy trễ trên thread pool, sai thứ tự).
- File C# có ký tự Trung và dấu ngoặc cong: giữ UTF-8, không viết lại bằng PowerShell 5.1.
- Kết thúc bằng tóm tắt ngắn: file đã sửa, kết quả build/test, việc còn dở.
