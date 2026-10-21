# Tự nhận diện và cài Ollama trên GPU thuê (Ubuntu và Windows)

Mục tiêu: người dùng chỉ nhập IP, cổng, user, mật khẩu (hoặc nhập file `.rdp` của nhà cho thuê Windows),
bấm **Tự nhận diện & cài tự động**. QT Studio phải tự biết máy là Ubuntu hay Windows, cài Ollama nếu thiếu,
tải model đã chọn và kiểm tra model trả lời được. Không bao giờ cài hoặc nâng cấp driver NVIDIA.

## Luồng

1. **Nhận diện cổng** (`RemoteHostProbe`): kết nối thô, không đăng nhập.
   SSH tự gửi banner; RDP trả lời gói X.224 Connection Request; HTTP trả lời `GET /`.
   Kết quả: `Ssh` (kèm cờ Windows nếu banner có `OpenSSH_for_Windows`), `Rdp`, `Http`, `Ollama`, `Unknown`, `Unreachable`.
2. **Cổng là SSH** → vào bước 4.
3. **Cổng là RDP (nhà cho thuê Windows thường chỉ đưa RDP)**:
   - Nhập file `.rdp` (`RdpConnectionFile`) để lấy host, cổng RDP, user. Mật khẩu trong file không được đọc.
   - QT Studio sao chép vào clipboard một dòng `powershell -EncodedCommand …` bật OpenSSH Server
     (`WindowsRemoteScripts.BuildOpenSshBootstrapCommand`), mở `mstsc` bằng file RDP, người dùng dán vào
     PowerShell (Run as administrator) trên máy thuê. QT Studio dò SSH mỗi 5 giây (tối đa 10 phút) rồi tự làm tiếp.
   - Điều kiện: nhà cung cấp phải cho truy cập cổng SSH (mặc định 22) từ Internet.
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
- Mọi script gửi lên máy Windows là ASCII, nằm dưới giới hạn 8191 ký tự của cmd.exe.
- Ollama chỉ bind loopback, không mở tường lửa. Truy cập luôn qua SSH tunnel.
- Clipboard chỉ chứa lệnh bật OpenSSH, không chứa mật khẩu.

## Chưa kiểm chứng trên máy thuê thật

Mọi script đã qua parser PowerShell và test giả lập (`WindowsGpuSetupTests`), các lệnh đọc GPU/đĩa/Ollama đã chạy thử trên Windows 11 cục bộ.
Chưa chạy trọn vẹn trên một máy Windows thuê thật (cần admin thật + quota mạng): tải/giải nén Ollama, task SYSTEM, truy cập GPU từ session 0.
