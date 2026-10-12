# M0 và Data Safety Phase 1 — 12/10/2026

Phạm vi được duyệt: M0, F01–F04 theo báo cáo kiểm toán ngày 12/10/2026. Branch phát triển `codex/m0-data-safety-phase1`, tách từ `codex/project-workspace` tại `965cf6832f6c9831eb4a8da5527fd3f2aa5199c5`. Không merge vào branch bàn giao.

## Baseline M0

- Windows x64 10.0.26100; Runtime hệ thống Core/Desktop 10.0.12; ban đầu không có SDK/MSBuild được tìm thấy trong PATH.
- Người dùng cho phép SDK riêng trong workspace. Microsoft dotnet-install tải SDK **10.0.401**, MSBuild **18.9.11**, vào `.tools/dotnet`; CLI home/cache NuGet ở `.tools`. Không sửa PATH hệ thống, Registry hoặc runtime hiện có. `.tools` được ignore.
- Lần đầu CLI tự tạo chứng chỉ HTTPS phát triển ASP.NET của tài khoản Windows; chưa trust. Sự kiện này được công bố; các lần chạy sau đặt `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`. Không tự xóa chứng chỉ của tài khoản.
- Restore solution gốc `--locked-mode`: thành công.
- Build Release solution gốc: **0 lỗi, 0 cảnh báo**.
- Bộ test bàn giao chạy lại trên Windows: **193 đạt, 0 lỗi, 0 bỏ qua**. TRX: `artifacts/m0/baseline.trx`; logs build/restore/environment cùng thư mục, không đưa vào Git.
- Thêm test harness WPF STA: controls được tạo nhưng không Show/Startup, transport giả lập, stores và project đều nằm trong `%TEMP%/qtaie-safety-wpf-{GUID}`. Smoke test đạt; 193 tests gốc vẫn đạt. Không mở project/LocalAppData thật.
- Các seam constructor/confirmation và hàm rename được tách để test dùng đúng đường production, chưa thay hành vi dữ liệu trong checkpoint M0.

## Cách chạy lại

## F01 — Quyền sở hữu tài nguyên Project

- Ba regression tests WPF đã chạy trước sửa và đều thất bại đúng vì snapshot cũ/đổi chương thay tài nguyên hiện hành (`f01-red-verified`). Một lần đầu có lỗi truyền tham số ở test; đã sửa harness và chạy lại trước khi sửa production.
- `ProjectResourceStore` lưu một bundle nguyên tử `Settings/resources.json`: Character Profiles, glossary, context và revision tăng; checksum/backup và guard revision chống lưu snapshot đã cũ. Chỉ đọc các tệp tài nguyên project cũ khi chưa có bundle; không tự lấy tài nguyên từ chapter.
- Không tăng schema `.qtproj` hoặc `.qtaie.json`. Bundle mới là định dạng tài nguyên cần thiết cho F01; các tệp legacy giữ nguyên để phục hồi. Khi đã có bundle, không dùng bản ứng dụng cũ ghi đồng thời; bản cũ không hiểu nguồn tài nguyên mới.
- Attach/load chương không thay tài nguyên UI/project. Chỉnh tài nguyên được commit trước chuyển chương; dịch/quét dùng tài nguyên project hiện hành. Snapshot chương cập nhật khi bắt đầu dịch; prompt/history/draft/approval/checkpoint cũ không bị xóa.
- Regression tests bổ sung: revision conflict, import legacy giữ nguyên bytes/schema, giữ backup tốt khi phục hồi, dịch dùng tài nguyên mới nhưng giữ draft/lịch sử cũ.

Trên Windows với .NET 10 SDK:

```powershell
./QuickTranslator.AiEditor/scripts/Validate-DataSafety.ps1 -Stage final
```

Script ưu tiên SDK `.tools/dotnet` nếu có, nếu không dùng dotnet trong PATH; restore locked, build Release, chạy cả Core và WPF tests. Exit lỗi không bị che; log/TRX mỗi assembly ở `artifacts/data-safety/{stage}`. WPF tests cần Windows Desktop Runtime; đây không phải kiểm thử thị giác/DPI/IME hoặc model thật.

## Chưa xác minh thủ công

WPF Show/thao tác chuột/bàn phím/DPI/IME/clipboard; prompt/template và chất lượng Ollama/TranslateGemma thực; offline end-to-end; disk-full, kill process/mất điện; tương thích toàn bộ Data thực. Không chạy request model hoặc thay dữ liệu truyện thật để có baseline.
