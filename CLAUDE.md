## Giao việc cho Codex

Claude là agent chính. Giao cho Codex qua `codex-run.ps1` (xem bên dưới, ưu tiên chạy nền; không dùng
/codex:rescue vì nó chạy bằng tài khoản khác) các task:
- Nhỏ, rõ ràng, độc lập với phần đang làm
- Cơ học: viết/sửa test, sửa lint/type error, đổi tên, format, boilerplate, migrate cú pháp
- Có cách kiểm chứng (test hoặc lint chạy được)

Claude tự làm: thiết kế/kiến trúc, debug phức tạp, thay đổi chạm nhiều file liên quan nhau,
và mọi việc mơ hồ.

Khi giao việc, prompt phải có: mục tiêu, file được phép sửa, cách kiểm chứng,
và những gì KHÔNG được động vào.
Sau khi Codex xong, luôn xem diff trước khi chấp nhận kết quả.
Không giao hai task cùng sửa một file cho hai bên cùng lúc.

### Hai workspace Codex (personal, business), quota riêng

Mục tiêu: tiết kiệm quota của Claude. Claude chỉ viết prompt giao việc, đọc kết quả và review diff;
Codex làm phần đọc code, sửa và chạy test.

- Mỗi workspace có CODEX_HOME riêng: `~/.codex-personal`, `~/.codex-business`.
  Kiểm tra đăng nhập: `~/.codex-dual/codex-run.ps1 -Workspace business -Status`.
- Chạy một task (nền):
  `powershell -File ~/.codex-dual/codex-run.ps1 -Workspace business -PromptFile <task.md> -Dir <worktree>`.
  Tin nhắn cuối của Codex được ghi vào `<task>.business.result.md`.
- Chỉ giao cho business (user chỉ định 2026-10-29). Khi business hết quota (lỗi usage/rate limit, exit code 1)
  thì hỏi user, không tự chuyển sang personal.
- Hai task song song chỉ khi độc lập và không sửa chung file. Mỗi task chạy trong một git worktree
  riêng (`git worktree add ../wt-<tên> -b codex/<tên>`), không chạy hai task trong cùng thư mục.
- Prompt viết vào file trong scratchpad, theo mẫu: mục tiêu, file được sửa, file KHÔNG được động vào,
  lệnh kiểm chứng (build + test), và trả về tóm tắt ngắn. Dặn Codex KHÔNG chạy git commit/add:
  sandbox của Codex luôn khóa `.git`, nên lệnh commit sẽ báo Permission denied.
- Chọn model (user chỉ định 2026-10-23): luôn truyền `-Effort high -Model gpt-6.1-sol`. Khi gặp lỗi
  "model is at capacity" thì lùi lần lượt sang `gpt-5.6-sol`, rồi `gpt-6-astra`. Config của workspace
  (`gpt-5.6-sol`, effort `medium`) chỉ là giá trị dự phòng. Task nào tốn quota bất thường thì báo user.
- Không dùng `--dangerously-bypass-approvals-and-sandbox`. Claude không bao giờ tự đăng nhập Codex.
- Review: `git -C <worktree> diff`, tự chạy test; đạt thì Claude commit lên nhánh `codex/<tên>` rồi merge.
- Sandbox của Codex không đọc được `%APPDATA%\NuGet\NuGet.Config`, nên `dotnet restore` thất bại. Claude chạy
  `dotnet restore <slnx>` trong worktree trước, rồi dặn Codex luôn build/test với `--no-restore`.
- Codex CLI phải được cài ngoài sandbox của Claude (npm global trong AppData thật), vì file Claude cài
  từ trong sandbox không hiện ra ở terminal thật. Config mỗi CODEX_HOME phải có
  `[windows] sandbox = "elevated"`; thiếu dòng này Codex chạy read-only và chặn mọi lệnh.

## Dọn bản build để ổ C không đầy

Mỗi bản publish QT Studio nặng ~150–260MB. Trước đây chúng bị giữ lại sau mỗi lần test và dồn tới hơn 15GB.

- Bản build chỉ để test/kiểm chứng: publish vào scratchpad hoặc thư mục tạm, kiểm tra xong thì xóa.
  Không publish vào `artifacts\` của repo.
- `QuickTranslator.AiEditor\artifacts\releases\` chỉ chứa bản giao cho user. Mỗi lần thêm bản mới,
  giữ 3 bản mới nhất, xóa các bản cũ hơn.
- Codex không đọc file này mà đọc `AGENTS.md` ở gốc repo (đã có hai luật trên, `--no-restore` và cấm git).
  Prompt giao việc không cần lặp lại các luật đó; chỉ ghi mục tiêu, file được sửa, file cấm và lệnh kiểm chứng.
