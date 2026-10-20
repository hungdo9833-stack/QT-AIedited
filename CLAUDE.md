## Giao việc cho Codex

Claude là agent chính. Dùng /codex:rescue (ưu tiên chạy nền) cho các task:
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
  Kiểm tra đăng nhập: `~/.codex-dual/codex-run.ps1 -Workspace personal -Status`.
- Chạy một task (nền):
  `powershell -File ~/.codex-dual/codex-run.ps1 -Workspace personal -PromptFile <task.md> -Dir <worktree>`.
  Tin nhắn cuối của Codex được ghi vào `<task>.personal.result.md`.
- Mặc định giao cho personal; khi personal hết quota (lỗi usage/rate limit) thì chuyển sang business.
- Hai task song song chỉ khi độc lập và không sửa chung file. Mỗi task chạy trong một git worktree
  riêng (`git worktree add ../wt-<tên> -b codex/<tên>`), không chạy hai task trong cùng thư mục.
- Prompt viết vào file trong scratchpad, theo mẫu: mục tiêu, file được sửa, file KHÔNG được động vào,
  lệnh kiểm chứng (build + test), và trả về tóm tắt ngắn. Dặn Codex KHÔNG chạy git commit/add:
  sandbox của Codex luôn khóa `.git`, nên lệnh commit sẽ báo Permission denied.
- Không dùng `--dangerously-bypass-approvals-and-sandbox`. Claude không bao giờ tự đăng nhập Codex.
- Review: `git -C <worktree> diff`, tự chạy test; đạt thì Claude commit lên nhánh `codex/<tên>` rồi merge.
- Codex CLI phải được cài ngoài sandbox của Claude (npm global trong AppData thật), vì file Claude cài
  từ trong sandbox không hiện ra ở terminal thật. Config mỗi CODEX_HOME phải có
  `[windows] sandbox = "elevated"`; thiếu dòng này Codex chạy read-only và chặn mọi lệnh.
