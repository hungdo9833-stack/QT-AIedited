# Tự nhận diện và cài Ollama trên GPU thuê (Ubuntu và Windows)

Mục tiêu: người dùng chỉ nhập IP, cổng, user, mật khẩu (hoặc nhập file `.rdp` của nhà cho thuê Windows),
bấm **Tự nhận diện & cài tự động**. QT Studio phải tự biết máy là Ubuntu hay Windows, cài Ollama nếu thiếu,
tải model đã chọn và kiểm tra model trả lời được. Không bao giờ cài hoặc nâng cấp driver NVIDIA.

## Luồng

1. **Nhận diện cổng** (`RemoteHostProbe`): kết nối thô, không đăng nhập.
   SSH tự gửi banner; nếu cổng im lặng thì gửi lời chào SSH (để nhận ra bộ chia cổng), rồi gói X.224 của RDP, rồi `GET /`.
   Kết quả: `Ssh` (kèm cờ Windows nếu banner có `OpenSSH_for_Windows`), `Rdp`, `Http`, `Ollama`, `Unknown`, `Unreachable`.
2. **Cổng là SSH** → vào bước 4.
3. **Cổng là RDP (nhà cho thuê Windows thường chỉ chuyển tiếp đúng một cổng RDP)**:
   - Người dùng chỉ nhập địa chỉ (dán được cả `host:cổng`), tài khoản, mật khẩu; hoặc nhập file `.rdp` (`RdpConnectionFile`, mật khẩu trong file không được đọc).
   - QT Studio sao chép vào clipboard lệnh bootstrap (`WindowsPortMux.BuildBootstrapCommand`, gzip) rồi tự mở `mstsc` bằng một file `.rdp` tạm
     (`RdpLaunchFile`: mật khẩu là blob DPAPI của người dùng Windows hiện tại, file bị xóa sau 20 giây, `alternate shell` thay Explorer bằng
     một PowerShell nhỏ chờ clipboard rồi chạy lệnh; clipboard được chuyển qua kênh RDP). Loader tự nâng quyền (UAC) nếu phiên chưa là admin.
     Không còn bước dán tay; nếu sau 2 phút chưa có SSH (ví dụ phiên cũ đã tồn tại nên shell thay thế bị bỏ qua), giao diện hướng dẫn dán tay Ctrl+V.
   - Lệnh đó, theo thứ tự:
     1. Cài OpenSSH Server (Windows capability, dự phòng bằng zip Win32-OpenSSH), cấu hình `sshd` chỉ nghe `127.0.0.1:22222`.
     2. Biên dịch bộ chia cổng `qt-mux.exe` (C# 5, `Add-Type` của Windows PowerShell, không cần SDK).
     3. Chạy thử bộ chia cổng ở cổng 33998, kiểm tra cả đường SSH và đường RDP. Lỗi thì dừng, chưa đổi gì.
     4. Mở tường lửa cho cổng RDP công khai (`QTStudio-Mux`), rồi chạy **tác vụ SYSTEM tách rời** (`QTStudio Mux Switch`):
        dời RDP sang cổng nội bộ (33890 trở lên), restart `TermService`, đăng ký tác vụ khởi động `QTStudio Mux`
        (nghe cổng RDP cũ; byte đầu là `S` thì chuyển cho sshd, còn lại chuyển cho RDP), kiểm tra lại cả hai đường qua cổng công khai.
        Có lỗi thì tự hoàn cổng RDP về như cũ (`C:\ProgramData\QTStudio\logs\mux-switch.log`).
   - RDP ngắt vài giây rồi nối lại được. QT Studio dò SSH ở cổng RDP công khai (mỗi 5 giây, tối đa 15 phút) rồi tự làm tiếp.
   - Hệ quả: tài khoản quản trị đăng nhập được bằng SSH qua cổng công khai; nhật ký RDP thấy nguồn là 127.0.0.1.
4. **Kết nối SSH + xác nhận fingerprint** (luồng cũ), rồi `RemoteOllamaAutoSetup.RunAsync`:
   - Nhận diện: Linux nếu có `/etc/os-release`, Windows nếu `cmd /c ver` có “Windows”, còn lại là Unknown (dừng).
   - Đọc GPU NVIDIA, VRAM, ổ trống (cần ≥ 12 GB nếu còn phải tải model), Ollama, danh sách model.
   - Thiếu Ollama hoặc service chưa chạy:
     - Ubuntu: script chính thức + `systemctl enable --now ollama` (hỏi mật khẩu sudo khi cần).
     - Windows: tải `provision.ps1` (chia đoạn base64, 1500 byte/đoạn) lên `C:\ProgramData\QTStudio`, chạy bằng
       scheduled task SYSTEM để sống sót khi SSH rớt, đọc `status.json` mỗi 3 giây để hiện tiến độ.
       Script tải `ollama-windows-amd64.zip` (GitHub, có resume), giải nén vào `C:\ProgramData\QTStudio\ollama`,
       đăng ký task `QTStudio Ollama` (AtStartup, SYSTEM, chỉ lắng nghe `127.0.0.1:11434`, models ở
       `C:\ProgramData\QTStudio\models`), chờ API phản hồi. Nếu Ollama đã chạy sẵn thì bỏ qua.
   - Tải model qua SSH tunnel loopback (tiến độ byte thật), rồi kiểm tra lại `api/tags`.

## An toàn

- Chỉ các thao tác cố định trong `SshOperation`; chỉ tên model và đoạn base64 của script (regex chặt) vào dòng lệnh.
- Script gửi qua SSH là ASCII, dưới giới hạn 8191 ký tự của cmd.exe.
- Ollama chỉ bind loopback, không mở tường lửa. Truy cập luôn qua SSH tunnel.
- Clipboard chỉ chứa lệnh bootstrap, không chứa mật khẩu.
- Chống khóa máy: kiểm thử hai đường trước khi đổi cổng, đổi cổng bằng tác vụ tách rời, tự hoàn khi lỗi,
  tác vụ chia cổng tự chạy lại khi sập (99 lần, cách 1 phút) và tự chạy khi khởi động máy.
  Máy thuê theo giờ nên nếu vẫn hỏng thì nút “Xóa máy” rồi thuê lại.

## Kiểm chứng

Có test tự động (`WindowsGpuSetupTests`, `WindowsPortMuxTests`): mọi script qua parser PowerShell; bộ chia cổng được biên dịch thật
bằng Windows PowerShell và chạy với server giả (định tuyến SSH/RDP, luồng 300 KB hai chiều, 12 kết nối song song, probe nhận SSH sau bộ chia cổng);
các lệnh đọc GPU/đĩa/Ollama đã chạy trên Windows 11 cục bộ.
Đã chạy trọn vẹn trên một máy thuê thật (thuepcpro / ezycloudx, Windows 10 Pro 19045, RTX 3090, 2026-10-21):
nhận diện RDP, dán lệnh, OpenSSH + bộ chia cổng trên cổng RDP, QT Studio kết nối SSH, nhận diện Windows/GPU/VRAM/ổ đĩa,
Ollama chạy, model `translategemma:12b` sẵn sàng, báo “Đã sẵn sàng”.

Bài học từ lần thử đầu: chương trình tự biên dịch mà chạy trong phiên RDP của người dùng làm Windows Firewall hiện hộp thoại;
RDP ngắt khi chưa bấm Allow thì Windows tạo luật chặn theo chương trình, thắng mọi luật mở theo cổng, và khởi động lại không gỡ được.
Vì vậy lệnh phải tạo luật Allow theo chương trình (và xóa luật Block cũ) trước khi chương trình mở cổng, kèm cơ chế tự hoàn lại sau 12 phút.
Ngoài ra `cmd /c ver` không đủ để nhận ra Windows; thông báo lỗi kiểu cmd.exe (“is not recognized as an internal or external command”) cũng được coi là dấu hiệu Windows.

Đã dịch thành công một chương qua tunnel này (người dùng xác nhận). Chưa rõ lần chạy trên máy thật đã tải/giải nén Ollama từ đầu hay máy đã có sẵn Ollama.
