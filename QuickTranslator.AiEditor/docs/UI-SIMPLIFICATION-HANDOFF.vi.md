# QTStudio - Ban giao tinh gon UI (25/10/2026)

> Cập nhật sau tiếp nhận: đã build và kiểm thử Windows/WPF. Xem [kết quả sửa lỗi, test và các bước nghiệm thu còn lại](UI-SIMPLIFICATION-VALIDATION.vi.md). Nội dung bên dưới giữ làm lịch sử bàn giao ban đầu; PR vẫn Draft.

> Nhanh rieng: `feat/qtstudio-simplified-ui`. Base: `codex/abi3-adaptive-semantic-batching` tai `2b51cf04890f258be91a2c500a4855ebb7bfd9bb`. KHONG merge truoc khi Codex build va test tren Windows.

## Pham vi da thuc hien theo thu tu

1. **Dieu huong + hanh dong:** sidebar chinh con Dịch & Biên tập / Nhân vật & Từ điển / Xuất bản; Cài đặt o duoi. Quan ly chuong va trang thai hang doi van truy cap qua menu. Cac nut hanh dong phu trong Editor duoc dua vao menu **Them**, nhung cac Button x:Name cu van ton tai cho EditorActionPolicy va automation.
2. **Che do noi dung:** Biên tập / Soát lỗi / Đọc thanh ba radio trong chung thanh che do, tai EditorWorkspaceRoot. Logic Reader va Review cu van duoc su dung; hien ChapterModeBar ca khi CurrentWorkspace la Reader.
3. **Hang doi:** status mini bar nam tren status bar, hien khi Ready / Running / PauseRequested / Paused / Interrupted / Cancelling / CompletedWithErrors; co Pause/Resume va Chi tiet. **Chi tiet khong duoc tu Resume.** QueueWorkspace va ProjectTranslationQueueView khong bi loai bo.
4. **Nhan vat, tu dien, cai dat:** mo Nhan vat truoc, ngon ngu menu Viet de hieu hon; gop lua chon it dung trong Expander/overflow; ghi ro Gemini dung cho khao sat nhan vat, khong phai model dich chuong; giu nguyen x:Name, event handlers va persistence.
5. **Ra soat ban dau:** doi chieu markup XAML bang scanner (tag balanced, x:Name khong trung, ElementName co dich); cap nhat menu popup bang trang thai nut goc de tranh loi binding WPF Popup; them checklist. Chua chay dotnet build/test va chua mo giao dien WPF trong moi truong nay.

## Yau cau Codex: thuc hien truoc khi merge

Tren Windows co .NET 10 SDK:
```powershell
cd QuickTranslator.AiEditor
dotnet restore QuickTranslator.AiEditor.slnx
dotnet build QuickTranslator.AiEditor.slnx -c Release -warnaserror
dotnet test QuickTranslator.AiEditor.slnx -c Release --no-build
```

### Kiem thu thu cong uu tien (Windows/WPF)

- Mo/tạo/doi Project, dong Project, luu Ctrl+S, khoi dong lai; dam bao khong mat du lieu, khong thay doi schema.
- Giao dien hẹp 900 px, 1180 px, 1600 px va DPI 100%/125%/150%/200%. Menu khong tran, Inspector va muc luc khong che van ban.
- Chuyen **Biên tập / Soát lỗi / Đọc** bang radio, Ctrl+1/Ctrl+2, nut mo trong Editor; thanh che do khong mat khi vao Reader; che do Reader va Editor chi mot che do duoc chon.
- Dong/mở menu **Thêm** trong moi trang thai EditorActionPolicy; kiem tra Visibility, IsEnabled va 3 slot secondary dynamic, nhat la dung, huy, sua nguyen van, duyet doan.
- Muc luc: nhap TXT/web; menu Tác vụ chương cho phép đổi tên, nhân bản, xóa chi khi hop le; tuy chon filter/sort trong Expander.
- Hang doi: Ready -> Running -> PauseRequested -> Paused -> Resume -> Completed, Interrupted va CompletedWithErrors; progress mini bar hien tren ca Reader/Tai nguyen/Xuat ban. **Chi tiết không khởi động lại.** Thử hai GPU va chuyen Project khi dang chay.
- Nhan vat: tab mac dinh Nhan vat; sua ten/ngoi ke, tu dien, ngu canh, presets; khoa nhat quan/normalize; thu quy trinh Ledger, popup va hoan tac. Kiem tra nut Luu tai nguyen.
- Settings: AI dịch GPU vs Gemini khảo sát tach ro; Credential Manager key khong duoc bi mat; doi theme tu menu va Settings; kiem tra phim tat.
- Co layout JSON cu trong `%LOCALAPPDATA%\QuickTranslatorAiEditor\workspace-layout.json`: mo lai dung, khong thay doi schema; thu xoa layout va khoi dong moi.
- Kiem tra ket noi chuot/phim, menu overflow, flyout, Popup, menu item click/disabled, build Release voi nullable warnings as errors.

## Gioi han va diem can soat

- De giu khoang thay doi nho, mot so Button x:Name cu duoc an trong StackPanel va dung de cap nhat trang thai menu. Sau khi Codex test co the refactor thanh ViewModel/command source chinh thuc.
- `OnEditorActionsOpened` sao chep state cua 13 Button theo thu tu sang menu, nham tranh Popup ElementName lookup loi. Neu doi thu tu menu, phai cap nhat mapping.
- Hang doi mini bar doc tu `ProjectTranslationCoordinator.GetProgress()` va cap nhat qua `ProgressChanged`. Can test khi view queue detach/attach, khi dang hoan tat va khi Project bi dong.
- **Khong sua** Core, schema, encoder, payload translation, prompt, quy tac xung ho. Khong merge nhanh co tinh trang build/test chua duoc xac nhan.

## File chinh

- `src/QuickTranslator.AiEditor.Wpf/MainWindow.xaml`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.Shell.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.Reader.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.EditorUx.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.SettingsUi.cs`
- `src/QuickTranslator.AiEditor.Wpf/WorkspaceLayoutStore.cs`
