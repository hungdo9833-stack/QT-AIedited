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
   - Nhập file `.rdp` (`RdpConnectionFile`) để lấy host, cổng RDP, user. Mật khẩu trong file không được đọc.
   - QT Studio sao chép vào clipboard một dòng lệnh (`WindowsPortMux.BuildBootstrapCommand`, nén gzip, ~9 KB),
     mở `mstsc` bằng file RDP; người dùng dán vào PowerShell (Run as administrator) trên máy thuê.
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
Chưa chạy trọn vẹn trên một máy Windows thuê thật: cài OpenSSH, đổi cổng RDP và restart `TermService`, tải/giải nén Ollama,
tác vụ SYSTEM, truy cập GPU từ session 0.
