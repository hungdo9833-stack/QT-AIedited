# QTStudio — Kiểm toán UI/UX và kế hoạch tái thiết kế (11/2026)

Nhánh: `codex/qtstudio-ux-redesign` (từ `3a7cb16`). Phạm vi: `QuickTranslator.AiEditor.Wpf` (XAML, style, code dựng UI).
Không đổi Core, schema Project, pipeline dịch, prompt, quy tắc xưng hô, hàng đợi hay lưu trữ.

Tài liệu này nối tiếp các đợt trước: M5 (shell Dark Navy), UI-SIMPLIFICATION (sidebar ba mục, menu **Thêm**),
USER-FIRST-UI Phase 4 (Nhập & Dịch, Tiếp tục dịch một nút). Các quyết định của những đợt đó được giữ, không làm ngược.

## 1. Hiện trạng

| Thành phần | Quy mô |
|---|---|
| `MainWindow` (partial) | ~10.000 dòng C# trong 20 file, `MainWindow.xaml` 1.111 dòng, 115 Button |
| Cửa sổ phụ | 15 cửa sổ/view: GPU, Import Preview, Web Import, Auto Translate, Chapter Manager, Queue, Publication, Library, Sắc Viện (3), Addressing Ledger, Chapter Titles, ThemedDialog |
| Theme | `Themes/` tự viết: Colors.Dark/Light, Brushes, Controls.xaml, App.xaml (style Button/TextBox/ComboBox/Tab/DataGrid…), icon Segoe Fluent |
| UI dựng bằng C# | AddressingLedgerWindow (28 control), TextCleaning (23), Characters (22), SettingsUi (14), ContextMenus (7)… |
| Test | Core 1.193 + WPF 276 (nhiều test tra control theo `x:Name` và method theo tên bằng reflection) |

Ảnh trước: `ux-redesign/before/*.png` (render offscreen bằng M5.UiSmoke, Project tổng hợp).

## 2. Vấn đề phát hiện

### 2.1 Kiến trúc thông tin, lệnh phân tán
- **Nhập truyện** nằm ở 3 nơi không đồng nhất: menu *Dự án* có "Nhập từ web" nhưng **không có TXT**; sidebar có menu "Nhập truyện" (TXT + web);
  "Tự động dịch từ link" chỉ ở menu *Dự án*. Người dịch không có một chỗ "Nhập" duy nhất.
- Menu **Workspace** (tiếng Anh) chỉ chứa Xuất bản + Sắc Viện, trong khi rail cũng có *Xuất bản* → hai tên cho một nhóm.
- Menu *Dự án* trộn 4 loại lệnh: nhập, tự động hóa, thư viện, thuộc tính/chương/hàng đợi/thư mục.
- "Quản lý chương…" có ở menu *Dự án* và menu "Tác vụ chương" của sidebar; "Hàng đợi dịch…" ở menu, thanh trạng thái và menu **Thêm**.
  Trùng ở menu là chấp nhận được (menu = chỗ phụ), nhưng menu chính lại thiếu lệnh (TXT).
- Thanh `GlobalToolbar` (Project ▾, switcher) đã ẩn vĩnh viễn nhưng còn nguyên trong XAML → nợ kỹ thuật, dễ gây nhầm khi thêm tính năng.

### 2.2 Ngôn ngữ giao diện không nhất quán
- Trộn Anh/Việt: "Project", "Inspector", "INSPECTOR", "Glossary", "segment", "Workspace", "Profile mới", "CHAPTER", "SOURCE".
- Cùng một khái niệm nhiều tên: *Project / Dự án*, *segment / đoạn*, *Glossary / Từ điển / Thuật ngữ*.

### 2.3 Typography, spacing, màu
- `FontSize` gõ cứng: 12 (25+ chỗ), 15, 16, 17, 18, 20, 22 — không theo một thang cỡ chữ.
- 176 giá trị `Margin` khác nhau; padding nút tự đặt (7,3 / 8,2 / 8,4 / 11,6…).
- Màu: tốt — mọi màu đi qua brush key (`DynamicResource`), có kiểm WCAG trong Colors.*.xaml. Giữ nguyên cơ chế này.

### 2.4 Control không đồng nhất
- Ba kiểu "chọn một trong nhiều" khác nhau: RadioButton `ModeButton` (Biên tập/Soát lỗi/Đọc), Button thường (Chương/Đoạn ở mục lục), TabItem (Inspector).
- Tiêu đề vùng: nơi dùng chữ HOA 12 SemiBold, nơi dùng `FieldLabel`, nơi dùng FontSize 16/18 tự đặt.
- Nút đóng "×" và "«" là ký tự, không phải icon Fluent.

### 2.5 Trạng thái, bàn phím, co giãn
- Loading/empty/error: có (EditorProcessingStateText, StatusText live region, banner hàng đợi) nhưng mỗi nơi tự dựng; không có thành phần InfoBar chung.
- Disabled: Opacity 0.45 chung — đạt. `StateButton` ẩn lệnh không hợp lệ — giữ.
- Focus ring 2px FocusBrush — đạt. Phím tắt Ctrl+1/2, Ctrl+B, Ctrl+Shift+I, Ctrl+S, Alt+PageUp/Down — giữ.
- Responsive: mục lục tự thu < 1320 DIP, Inspector < 1100 DIP, MinWidth 900×520 — giữ, kiểm lại sau thay đổi.
- DPI: không có `app.manifest`; WPF .NET mặc định Per-Monitor-V2 aware — kiểm bằng render 144/192 DPI.

## 3. Kiến trúc thông tin đề xuất (theo workflow người dịch)

Workflow: **Nhập → Khảo sát nhân vật/xưng hô → Dịch → Soát → Đọc → Xuất bản**.

```
┌ Menu: Tệp · Nhập · Dự án · Xuất bản · Hiển thị ─────────────────────────────────────────────┐
├──────┬────────────────────┬───────────────────────────────────────────────┬───────────────────┤
│ Rail │ Mục lục            │ Tiêu đề chương · trạng thái   [Lệnh chính][⋯] │ Tra cứu           │
│ Dịch │ [Chương | Đoạn]    │ [Biên tập | Soát lỗi | Đọc]                   │ [Từ điển|Ngữ cảnh │
│ Nhân │ tìm · lọc          │                                               │  |Nhân vật]       │
│ vật  │ danh sách chương   │   Vùng làm việc theo chế độ                   │                   │
│ Xuất │                    │                                               │                   │
│ bản  │ ← Trước · Sau →    │                                               │                   │
│ ⚙    │                    │                                               │                   │
├──────┴────────────────────┴───────────────────────────────────────────────┴───────────────────┤
│ Trạng thái · ▰▰▱ Hàng đợi [Tạm dừng][Chi tiết]  · Đã tự lưu · GPU · Sắc Viện · đường dẫn       │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

Menu (mỗi lệnh có **một chỗ chính** trên giao diện; menu là chỗ phụ, đầy đủ, có phím tắt):

| Menu | Lệnh |
|---|---|
| **Tệp** | Dự án mới, Mở dự án, Dự án gần đây, Thư viện dự án · Chương nháp mới, Mở tiến độ cũ · Lưu (Ctrl+S) · Đóng dự án |
| **Nhập** (mới, gom lại) | Từ tệp TXT…, Từ website…, Tự động dịch từ link… |
| **Dự án** | Thuộc tính, Quản lý chương, Hàng đợi dịch, Mở thư mục |
| **Xuất bản** (đổi từ "Workspace") | Xuất bản…, Tài khoản Sắc Viện…, Truyện trên Sắc Viện… |
| **Hiển thị** | Mục lục, Bảng tra cứu, bố cục, giao diện sáng/tối |

Không xóa chức năng nào: mọi lệnh hiện có vẫn còn, chỉ thêm đường vào bị thiếu (TXT trong menu) và đổi tên nhóm.

## 4. Thứ tự triển khai

| Bước | Nội dung | Kiểm chứng |
|---|---|---|
| 0 | Code-simplifier (A+C: Claude, B và D: Codex) | build + 1.469 test |
| 1 | Design system: `Themes/Tokens.xaml` (thang chữ, khoảng cách, bo góc, kích thước), style chữ chung, tài liệu quy tắc | render tất cả màn hình |
| 2 | WPF UI (lepoco) cho shell Fluent: theme Fluent đồng bộ với ThemeManager, thay ký tự bằng icon Fluent, InfoBar chung cho trạng thái | render dark/light, test |
| 3 | MainWindow: menu theo §3, thống nhất thuật ngữ, typography/spacing theo token, Chương/Đoạn dùng segmented control | test + ảnh trước/sau |
| 4 | Cửa sổ phụ: tiêu đề/phụ đề chuẩn, thanh nút chuẩn (chính bên phải, IsDefault/IsCancel), token | test + ảnh |
| 5 | Guard test chống phình: cấm `FontSize`/màu hex gõ cứng trong XAML ngoài `Themes/`, ratchet số lượng control dựng bằng C# | test |
| 6 | Kiểm co giãn 900×520, 1366×768, 1600×900; DPI 96/144/192; dark/light; trang so sánh trước/sau | ảnh |

Nguyên tắc an toàn: giữ mọi `x:Name`, handler, thứ tự item mà code-behind phụ thuộc (ví dụ `OnEditorActionsOpened` sao trạng thái 13 lệnh theo thứ tự),
và layout JSON trong LocalAppData.
