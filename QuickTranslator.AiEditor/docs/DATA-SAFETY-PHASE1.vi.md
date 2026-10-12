# M0 & Data Safety Phase 1 — nghiệm thu ngày 12/10/2026

Phạm vi: M0 và F01–F04 theo báo cáo kiểm toán 12/10/2026 được người dùng duyệt. Branch phát triển **codex/m0-data-safety-phase1**, tách từ **codex/project-workspace** tại **965cf6832f6c9831eb4a8da5527fd3f2aa5199c5**. Không merge/push; không sửa hoặc di chuyển Project thật. Không thiết kế lại UI, không Book Builder, không queue nhiều chương.

## 1. Baseline M0 và môi trường

| Hạng mục | Kết quả |
|---|---|
| Windows | x64, 10.0.26100 |
| Ban đầu | Core/Desktop Runtime 10.0.12; không tìm thấy SDK/MSBuild trong PATH |
| SDK được người dùng cho phép | Microsoft .NET SDK **10.0.401**, MSBuild **18.9.11**, riêng tại .tools/dotnet |
| Cache/CLI home | .tools/nuget và .tools/cli; được ignore; gọi SDK bằng đường dẫn riêng |
| Restore solution bàn giao | --locked-mode thành công |
| Build Release bàn giao | **0 lỗi, 0 cảnh báo** |
| Tests bàn giao chạy lại | **193 đạt, 0 lỗi, 0 bỏ qua** |
| Ollama kiểm tra chỉ đọc | GET localhost:11434/api/tags phản hồi; có translategemma:12b, size > 0, không có remote_host/remote_model. Chưa gửi POST /api/chat hay nội dung chương. |
| Bằng chứng | artifacts/m0/environment.txt, restore.txt, build.txt, tests.txt, baseline.trx |

Installer ZIP không sửa PATH hệ thống, Registry hay runtime hiện có. Nguồn: [Microsoft dotnet-install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script).

**Thay đổi ngoài dự kiến được công bố:** lần CLI đầu tự tạo chứng chỉ HTTPS phát triển ASP.NET của tài khoản Windows, chưa trust. Các lần sau đặt DOTNET_GENERATE_ASPNET_CERTIFICATE=false. Không tự xóa chứng chỉ tài khoản. SDK vẫn chỉ ở workspace.

Git chưa có identity nên các checkpoint dùng tác giả **Codex <codex@localhost>** theo từng lệnh; không đặt identity/global config của người dùng.

## 2. Nguyên nhân đã xác minh và cách sửa

### F01 — Project resource ownership

Ba regression tests WPF trước sửa đều thất bại: Attach/load snapshot cũ thay hồ sơ/glossary/context mới; chỉnh tài nguyên chưa lưu bị thay khi chuyển chương.

[ProjectResourceStore](../src/QuickTranslator.AiEditor.Core/Projects/ProjectResourceStore.cs) sở hữu một bundle nguyên tử **Settings/resources.json**: Character Profiles, Glossary, Context và revision hiện hành. Có checksum, backup, guard revision và kiểm tra ProjectId. Chỉ đọc các tệp tài nguyên cấp project cũ khi bundle chưa tồn tại; không tự nâng snapshot chapter thành tài nguyên project.

WPF không lấy snapshot chương để ghi ngược vào project. Chỉnh tài nguyên được commit trước khi chuyển context; các thao tác dịch/quét dùng tài nguyên hiện hành. Chapter giữ snapshot dùng khi dịch, còn attempts/prompt/draft/approval/normalization history vẫn được bảo tồn. Mỗi attempt mới ghi revision tài nguyên đã dùng để truy vết.

**Tương thích dữ liệu:** không tăng SchemaVersion .qtproj (1) hoặc .qtaie.json (3), không chạy migration toàn bộ chapter. Bundle project mới là phần cần thiết để lưu tài nguyên nhất quán. Thêm trường nullable ProjectResourceRevision vào attempt; attempt cũ nhận null. Hash legacy v1 bỏ qua trường mới đúng như các trường bổ sung khác; bộ tests migration/fixture cũ vẫn đạt. Các tệp tài nguyên legacy giữ nguyên bytes, không tự xóa.

### F02 — Rename chapter consistency

Hai regression tests trước sửa thất bại: service đang mở vẫn giữ tên cũ sau rename, và file được rename chưa có draft pending. Nguyên nhân là repository rename bản trên đĩa, rồi LoadProjectChapter trả sớm vì cùng ID.

[TranslationService.RenameAsync](../src/QuickTranslator.AiEditor.Core/Translation/TranslationService.Editing.cs) đổi metadata trên document sống dưới cùng khóa trạng thái/save gate với autosave. Lưu cả draft pending, cập nhật summary manifest và TitleInput; không reload/ghi một chapter đĩa cũ lên service đang mở.

Test có save cũ đang bị chặn xác nhận rename đợi gate và bản lưu cuối giữ tên mới cùng draft. Tên không hợp lệ không mutation.

### F03 — Orphan chapters

Ba regression tests trước sửa thất bại: tạo chương qua Glossary không có ProjectId/manifest; editor chấp nhận chương chưa đăng ký; lỗi write manifest để lại entry trong bộ nhớ trỏ tới file đã dọn.

Tạo chương qua Glossary lưu đủ preset/glossary/context rồi gọi RegisterChapterIfNeededAsync trước AttachChapter. Nhập TXT cũng dùng helper này; import legacy/duplicate vẫn đăng ký qua ProjectRepository.

[RegisteredChapterStore](../src/QuickTranslator.AiEditor.Core/Projects/RegisteredChapterStore.cs) bảo vệ mọi lần editor service ghi: chapter phải đang active trong manifest, ProjectId/số chương/path phải khớp. AttachChapter kiểm tra đăng ký trước khi thay service. Khi Add thất bại ở write manifest, repository phục hồi manifest trong bộ nhớ và dọn file mới.

Không quét/xóa/sửa orphan cũ. Không triển khai journal crash/move/delete/restore của F07 trong lượt này.

### F04 — Safe project/chapter switching

Trước sửa, bộ năm tests có **4 thất bại, 1 đạt**. Những lỗi xác minh: nguyên văn chưa chia không được hỏi lưu; mở Project đích lỗi làm mất service hiện tại; chuyển chương không chờ request riêng; hai lần chuyển có thể chạy chồng nhau và xung đột revision. Trường hợp save thất bại đã giữ draft và chặn chuyển.

[WorkspaceLifetime](../src/QuickTranslator.AiEditor.Wpf/MainWindow.WorkspaceLifetime.cs) là lifecycle chung cho thay context:

1. Lấy quyền thao tác trên dispatcher; từ chối thao tác chồng nhau.
2. Nếu có nguyên văn chưa chia, hỏi lưu thành chương. Không đồng ý thì giữ input/context và không chuyển; đồng ý thì đăng ký/lưu qua cơ chế F03.
3. Pause queue sau request hiện tại; chờ cả request riêng và lifecycle callback/UI của request.
4. Lưu document cũ, tài nguyên, preset và manifest. Lỗi ghi phải chặn chuyển.
5. Đọc/validate manifest, assets, model/segmentation settings và chapter đích trước khi thay context.
6. Commit context đích sau khi kiểm tra thành công; không detach context cũ trước bước này.

Các đường mở/tạo/đóng/khôi phục Project, mở tiến độ, chương mới, chuyển trước/sau/sidebar, nhập/nhân bản/xóa chương, nhập legacy và điều hướng từ Batch được nối vào lifecycle này. Các menu chỉnh tài nguyên và import glossary có khóa async để không áp dụng kết quả đọc cũ sang Project khác.

Callback dịch giữ reference service/task/token nguồn; không dùng service mới để tìm segment của request cũ. Callback queue không tự giải phóng khóa của thao tác switch. Callback tải Reader/preset khởi động kiểm tra store còn hiện hành. Reader chỉ thêm guard vòng đời, không viết lại phần hiển thị; TranslateGemma/VietPhrase không bị viết lại.

Test mở Project đầu tiên đã bắt được lỗi trong quá trình tích hợp mới: cố lưu một form nhân vật standalone chưa dùng và tên rỗng. Đã sửa trước checkpoint cuối; không coi lỗi này là lỗi baseline bàn giao.

## 3. Tests trước/sau và Release build

| Mốc | Trước sửa | Sau sửa toàn suite |
|---|---|---|
| Baseline | — | 193 Core đạt |
| M0 harness | — | 193 Core + 1 WPF đạt |
| F01 | 3 WPF thất bại đúng dữ liệu | **197 Core + 4 WPF = 201 đạt** |
| F02 | 2 WPF thất bại đúng tên/draft | **199 Core + 6 WPF = 205 đạt** |
| F03 | 1 Core + 2 WPF thất bại | **200 Core + 8 WPF = 208 đạt** |
| F04 | 4 lỗi + 1 trường hợp vốn đạt | Tests bổ sung cho lifecycle production, queue checkpoint, giữ nguyên văn và Project đích hỏng |
| Truy vết/validate đích bổ sung | 1 Core thiếu revision + 1 WPF đổi sang settings không hợp lệ thất bại | Đã sửa và chạy lại |
| Nghiệm thu cuối | — | **201 Core + 19 WPF = 220 đạt, 0 lỗi, 0 bỏ qua; Release 0 lỗi/0 cảnh báo** |

Bằng chứng red/green ở artifacts/data-safety: f01-red-verified, f01-complete, f02-red, f02-green, f03-red, f03-complete, f04-red, resource-trace-and-target-red, first-open-red, final và final-reviewed. Lượt cuối final-reviewed chạy sau khi nối Batch preview với tài nguyên hiện hành; 201 Core và 19 WPF vẫn đạt. TRX của từng assembly có tên riêng, không ghi đè nhau.

Trong quá trình phát triển có một lỗi truyền params array của test harness (đã sửa và chạy lại red trước sửa F01), một lỗi compiler thiếu using ở F03 và một warning field chưa dùng ở bản tích hợp F04 đầu. Đã sửa; log các lần chưa đạt được giữ lại. Không che test/compile thất bại và không gọi run filter không có testcase là một test đã đạt.

Core tests dùng dữ liệu tổng hợp/HTTP giả lập. WPF tests tạo controls và gọi chính luồng production trên STA Dispatcher, **không Show/Startup**, không đọc Project/LocalAppData thật. Test roots ở %TEMP% với prefix/GUID riêng và cleanup chỉ trong root đó.

## 4. File đã thay đổi

Mọi mã chức năng nằm trong module AI Editor. Ngoài module chỉ thêm ignore .tools ở .gitignore gốc.

| Nhóm | Files (tương đối từ QuickTranslator.AiEditor) |
|---|---|
| Ownership/store | src/QuickTranslator.AiEditor.Core/Projects/ProjectResourceStore.cs, RegisteredChapterStore.cs, ProjectModels.cs, ProjectRepository.cs |
| Domain/config/trace | src/QuickTranslator.AiEditor.Core/Translation/TranslationService.cs, TranslationService.Editing.cs, TranslationService.Pronouns.cs, TranslationService.TextCleaning.cs; Models/ChapterDocument.cs; Persistence/ChapterFileStore.cs, DocumentJson.cs |
| WPF lifecycle/wiring | src/QuickTranslator.AiEditor.Wpf/MainWindow.WorkspaceLifetime.cs, MainWindow.Projects.cs, MainWindow.Chapter.cs, MainWindow.xaml.cs, MainWindow.ContextMenus.cs, MainWindow.Characters.cs, MainWindow.Presets.cs, MainWindow.SettingsUi.cs, MainWindow.TextCleaning.cs, MainWindow.Reader.cs |
| WPF test seams | src/QuickTranslator.AiEditor.Wpf/AssemblyInfo.cs, WorkspaceConfirmation.cs |
| Core regression tests | tests/QuickTranslator.AiEditor.Tests/ProjectResourceTests.cs, ChapterMetadataTests.cs, ChapterRegistrationTests.cs, ReviewSampleTests.cs |
| WPF regression tests | tests/QuickTranslator.AiEditor.Wpf.Tests/WorkspaceFixture.cs, ProjectOwnershipTests.cs, RenameConsistencyTests.cs, ChapterRegistrationTests.cs, SafeSwitchingTests.cs; csproj và lockfile |
| Build/docs | QuickTranslator.AiEditor.slnx, scripts/Validate-DataSafety.ps1, docs/DATA-SAFETY-PHASE1.vi.md, README.vi.md, .gitignore |

Không đổi MainWindow.xaml/App.xaml hoặc bố cục, TraditionalTranslator, OllamaClient, ReaderDocument/Reader compositor, Book Builder hoặc queue nhiều chương.

## 5. Mẫu dữ liệu và kiểm thử thủ công còn lại

Một Project tổng hợp để xem lại nằm tại **artifacts/data-safety/review-sample** (ngoài Git): hai chương, một character/glossary/context, một attempt giả lập, draft, approval, draft history và metadata đã rename. Test đọc lại xác nhận các dữ liệu đó còn nguyên. Đây không phải truyện thật; không gửi Ollama thật khi tạo mẫu.

Chỉ mở bản sao/mẫu khi nghiệm thu. Gói build ở src/QuickTranslator.AiEditor.Wpf/bin/Release/net10.0-windows. Khi chạy EXE, truyền đường dẫn .qtproj mẫu rõ ràng để không tự mở Project recent của tài khoản.

| Kiểm tra cần thao tác Windows/model thật | Trạng thái |
|---|---|
| Kéo splitter/selection highlight, font/DPI/IME/clipboard và focus bằng mắt | Chưa xác minh; dùng WINDOWS-CHECKLIST.vi.md |
| Sidebar/switcher/menu, prompt lưu nguyên văn, dialog mở TXT/legacy/ZIP | Logic có STA tests và build; native dialog/chuột cần thử |
| Dịch riêng rồi chuyển chương/Project; queue pause rồi đổi; request lỗi/hủy | Có tests transport giả lập; cần Ollama thật |
| Dịch vùng chọn đang chạy, xem modal kết quả rồi chuyển | Lifecycle chờ callback; cần kiểm tra modal thực |
| Nhập hồ sơ tên mới, đổi chương A→B→A, đóng/mở lại | Có regression ownership; cần quy trình thực trên bản sao |
| Rename lúc gõ liên tục/autosave/queue | Có save-gate tests; cần thao tác gõ thực |
| Ngắt Internet, chất lượng/template TranslateGemma, GPU/VRAM | Chưa xác minh |
| Disk full, kill process/mất điện; khóa/move/replace Windows trên dữ liệu tổng hợp | Chỉ có fault injection/IO tests, chưa thử sự cố hệ thống thật |

## 6. Rủi ro còn lại và tương thích

- F05–F14 chưa được giao nên chưa sửa, ngoại trừ phần Add rollback cần cho F03 và error boundary cần cho switch. Batch apply/undo atomicity, scope batch theo manifest, canonical Reader/TXT, retention, token budget và hiệu năng lớn vẫn thuộc đợt tiếp.
- Bundle resources ghi nguyên tử nhưng chưa có journal giao dịch cho toàn project. Crash giữa chapter/manifest hoặc delete/restore/import nhiều tệp vẫn cần nhóm F07/F09.
- Guard revision/khóa session không thay thế project lock giữa Windows sessions hoặc công cụ ngoài. Không mở cùng project để sửa đồng thời bằng hai phiên/bản app.
- Bản ứng dụng bàn giao không hiểu resources.json. Không dùng bản cũ sửa một project đã được bản này lưu; nếu cần rollback ứng dụng, phục hồi **toàn thư mục project trước nâng cấp** trên thư mục mới. Giữ cả bundle, legacy files và chapter snapshots.
- Project cũ có các snapshot/tệp tài nguyên mâu thuẫn cần người dùng xác nhận nguồn đúng; ứng dụng không tự lấy một snapshot chapter để hạ tài nguyên project.
- Recovery của reader/manifest/settings/character legacy chưa đồng nhất (F08). Resource bundle mới có backup guard riêng, nhưng không suy ra mọi store đã an toàn.
- Autosave giảm cửa sổ mất dữ liệu, không bảo đảm chống mọi mất điện/hỏng ổ. ZIP/.bak cùng ổ không bảo vệ mất toàn ổ; giữ bản sao độc lập trước thao tác với dữ liệu quan trọng.
- Không tự sửa/xóa history, approvals, drafts, checkpoints, từ điển hoặc orphan cũ. Không mở Project thật để kiểm thử.

## 7. Checkpoints và chạy lại

| Checkpoint | Commit |
|---|---|
| M0 baseline/harness | feb8d3057bd82f40350708be0665e405750d9349 |
| F01 ownership | 9a65878cecc80391586116c90d2ae842041d7812 |
| F02 rename | b84d38818f6612b8c143ef1b7e5e99ddba1d080c |
| F03 registration | 479a9b05acfb2475558571e3f381650141a8effe |
| F04 và nghiệm thu cuối | Commit cuối được ghi trong câu trả lời bàn giao; git log trên branch phát triển cho hash chính xác |

Trên Windows với .NET 10 SDK, từ repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File QuickTranslator.AiEditor/scripts/Validate-DataSafety.ps1 -Stage final
```

Bypass chỉ áp dụng cho process chạy script, không đổi execution policy của máy. Script ưu tiên SDK riêng nếu có, restore locked, build Release và chạy cả hai test projects; exit lỗi giữ nguyên. Logs/TRX và dữ liệu thử đều được ignore.

Branch bàn giao vẫn ở commit 965cf683…; branch phát triển chưa merge. Chờ người dùng kiểm tra kết quả trước khi triển khai nhóm lỗi tiếp theo.
