# QTStudio — Design system và quy tắc giao diện (bắt buộc)

Áp dụng cho mọi màn hình mới và mọi lần sửa giao diện trong `QuickTranslator.AiEditor.Wpf`.
Mục tiêu: một ngôn ngữ thiết kế (Fluent, Windows 11), không chắp vá, không để giao diện phình thêm.
Guard test `DesignSystemGuardTests` (Wpf.Tests) chặn vi phạm có thể kiểm bằng máy.

## 1. Nguồn sự thật

| Tệp | Chứa |
|---|---|
| `Themes/Colors.Dark.xaml`, `Colors.Light.xaml` | Màu (đã kiểm WCAG AA). Chỉ sửa màu ở đây. |
| `Themes/Brushes.xaml` | Brush key dùng chung (`TextBrush`, `MutedBrush`, `PanelBrush`, `PrimaryBrush`…). |
| `Themes/Tokens.xaml` | Cỡ chữ, khoảng cách, bo góc, chiều cao control, style chữ. |
| `Themes/Controls.xaml`, `App.xaml` | Style control chuẩn (Button, TextBox, ComboBox, Tab, DataGrid, Menu…). |
| `Themes/Fluent.xaml` | WPF UI (lepoco) 4.3: brush Fluent + **chỉ** InfoBar, ProgressRing, Card. |
| `Themes/ThemeManager.cs` | Đổi Sáng/Tối, map brush Fluent sang bảng màu, thanh tiêu đề theo theme. |

Không gộp `ControlsDictionary` toàn cục của WPF UI: mỗi loại control chỉ có một template (của `Themes/`).
Muốn dùng thêm một control WPF UI → thêm đúng dictionary của nó vào `Fluent.xaml` và map brush nó dùng trong `ThemeManager`.

## 2. Token

### Chữ (thang Fluent, rút gọn cho editor dày đặc)
| Style | Cỡ | Dùng cho |
|---|---|---|
| `CaptionText` | 12, Muted | Chú thích, trạng thái, metadata |
| `OverlineText` | 12 SemiBold, Muted, CHỮ HOA | Nhãn nhóm (rail, header panel) |
| `BodyText` / mặc định Window | 14 | Nội dung, nút, ô nhập |
| `BodyStrongText` | 14 SemiBold | Nhãn trường, tiêu đề thẻ |
| `SubtitleText` | 16 SemiBold | Tiêu đề vùng, tên chương đang mở |
| `TitleText` / `WindowTitle` | 20 SemiBold | Tiêu đề cửa sổ, tiêu đề trang |
| `DisplayText` | 28 SemiBold | Số liệu lớn (tiến độ hàng đợi) |

Văn bản truyện (ô nguyên văn/bản dịch, Reader) có cỡ riêng do người dùng chỉnh — không thuộc thang này.

### Khoảng cách (lưới 4 px)
`Space.XS` 4 · `Space.S` 8 · `Space.M` 12 · `Space.L` 16 · `Space.XL` 24; `Gap.Bottom.*`, `Gap.Right.*`, `Gap.Top.S`;
`Inset.Window` 16 (lề gốc mọi cửa sổ), `Inset.Panel` 12, `Inset.Bar` 12,8, `Inset.Button` 12,6, `Inset.ButtonCompact` 8,4.

### Hình khối
`Radius.Control` 4 (nút, ô nhập), `Radius.Overlay` 8 (popup, flyout, thẻ nổi);
`Size.ControlHeight` 32, `Size.ControlHeightCompact` 24; focus ring 2 px `FocusBrush` (style `FocusVisual`).

## 3. Control và mẫu dùng

| Nhu cầu | Dùng | Không dùng |
|---|---|---|
| Hành động chính của màn hình | **Một** `PrimaryButton` | Hai nút Primary cạnh nhau |
| Hành động phá hủy (xóa, hủy hàng đợi) | `DangerButton`, tách khỏi nút thường, có xác nhận | Nút thường màu đỏ tự đặt |
| Lệnh phụ nhiều | Menu **Thêm** / `CommandOverflowToggle` | Thêm hàng nút mới |
| Chọn 1 trong 2–4 chế độ | `ModeButton` (RadioButton, segmented) | Button thường đổi màu bằng code |
| Nhóm nội dung | `Card` (Border) hoặc `wpfui:Card` | Border tự vẽ màu/bo góc |
| Thông báo trong trang (lỗi/cảnh báo/thành công) | `wpfui:InfoBar` (Severity) | TextBlock đổi màu tự đặt |
| Đang tải > 1 giây | `wpfui:ProgressRing` hoặc ProgressBar + chữ "Đang …" | Treo UI không phản hồi |
| Icon | `ui:ButtonIcon.Glyph` / `IconText` (Segoe Fluent Icons) | Emoji, ký tự "×", "«", "▾" làm icon |
| Hộp thoại | `ThemedDialog` | `MessageBox.Show` |

Thanh nút cuối hộp thoại: căn phải, thứ tự **[Phụ] [Hủy] [Chính]**, nút chính `IsDefault`, nút hủy `IsCancel`.
Lệnh không hợp lệ ở trạng thái hiện tại: ẩn (`StateButton`) nếu là lệnh chạy/dừng; còn lại disabled + ToolTip lý do (`ToolTipService.ShowOnDisabled`).

## 4. Thuật ngữ giao diện (tiếng Việt thống nhất)

| Dùng | Không dùng |
|---|---|
| dự án | Project |
| đoạn | segment |
| bảng tra cứu (panel phải: Từ điển · Ngữ cảnh · Nhân vật) | Inspector |
| thuật ngữ | Glossary |
| hàng đợi dịch | queue (trong chữ hiển thị) |
| hồ sơ (GPU/nhân vật) | Profile |
| nguyên văn | source (chữ hiển thị) |

Tên riêng giữ nguyên: Hán Việt, VietPhrase, Gemini, GPU, Sắc Viện, TXT, EPUB, PDF.

## 5. Kiến trúc thông tin

Workflow: **Nhập → Khảo sát nhân vật/xưng hô → Dịch → Soát → Đọc → Xuất bản**.
- Rail trái: điểm đến chính (Dịch & Biên tập, Nhân vật & Từ điển, Xuất bản; Cài đặt ở dưới). Tối đa 5 mục.
- Menu: Tệp · Nhập · Dự án · Xuất bản · Hiển thị. Mọi lệnh đều có trong menu (chỗ phụ, kèm phím tắt).
- Mỗi lệnh chỉ có **một chỗ chính** trên giao diện. Thêm chỗ thứ hai phải có lý do ghi trong PR.
- Thanh trạng thái: thông báo · hàng đợi · tự lưu · nguồn dịch · Sắc Viện · đường dẫn. Không thêm mục mới vào đây.

## 6. Quy tắc bắt buộc cho tính năng mới

1. Không gõ số `FontSize`, màu hex, `CornerRadius` trong XAML ngoài `Themes/` — dùng token/style. (guard test)
2. Không tạo màu/brush trong C# (`new SolidColorBrush`, `Brushes.X`, `Color.FromRgb`) ngoài `Themes/` — dùng `SetResourceReference(…, "KeyBrush")`. (guard test)
3. UI tĩnh viết bằng XAML. Chỉ dựng bằng C# khi nội dung thật sự động; khi đó dùng style/token qua `SetResourceReference`.
4. Không thêm cửa sổ mới nếu có thể là một vùng trong workspace hoặc một `ThemedDialog`.
5. Không thêm mục rail, menu gốc hay mục thanh trạng thái mới mà không cập nhật tài liệu này.
6. Mỗi control tương tác: có nhãn đọc được (Content hoặc `AutomationProperties.Name`), đi tới được bằng Tab, có focus ring.
7. Mọi tác vụ > 1 giây: trạng thái đang chạy, có thể hủy nếu dài, kết quả/lỗi hiện tại chỗ (InfoBar/StatusText), không chặn UI.
8. Cửa sổ có `MinWidth`/`MinHeight`; bố cục dùng `*`/`Auto`, không cố định chiều rộng vùng nội dung chính.
9. Kiểm Sáng + Tối và 100/150/200% DPI bằng `M5.UiSmoke --capture … --theme … --dpi …` trước khi merge.
10. Giữ `x:Name`, handler và thứ tự mục mà code-behind/test phụ thuộc; đổi tên phải sửa cả code và test trong cùng commit.
