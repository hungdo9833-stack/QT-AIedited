using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Does by key presses what a person would do in the Remote Desktop window QT Studio just opened: open PowerShell
/// as administrator (Win+R, "powershell", Ctrl+Shift+Enter) and paste the setup command that is on the clipboard.
/// Windows' own security prompts (the "unknown publisher" box and the UAC consent) are never answered for the user;
/// the automation waits until the user has answered them. Only the Remote Desktop window of the file QT Studio
/// created is ever touched (found by its title, since mstsc may hand the file to another process), and nothing is
/// typed unless that window has the keyboard focus.
/// </summary>
internal static class RemoteDesktopAutomation
{
    private const ushort VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkLwin = 0x5B;
    private const uint InputKeyboard = 1, KeyUp = 0x0002, KeyExtended = 0x0001;

    /// <summary>Returns null when every key was sent, otherwise a short reason the automation stopped.</summary>
    public static async Task<string?> RunAsync(string fileToken, Action<string> status, CancellationToken cancellationToken)
    {
        // mstsc replaces its window once the connection is up, so the window is looked up again by title every time.
        DateTime until = DateTime.UtcNow.AddSeconds(180);
        DateTime lastSeen = DateTime.UtcNow;
        DateTime? ready = null;
        status("Đang chờ Remote Desktop kết nối…");
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Window> windows = TopLevelWindows();
            Window? main = FindMain(windows, fileToken);
            if (main is null)
            {
                // Windows asks about an unsigned .rdp file and about wrong credentials; those stay with the user.
                if (windows.Any(w => w.Visible && w.ProcessName == "mstsc" && w.ClassName == "#32770"))
                    status("Windows đang hỏi về kết nối này: hãy trả lời trong hộp thoại Remote Desktop (Connect)");
                else if (DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(20))
                    return "Cửa sổ Remote Desktop đã đóng.";
                await Task.Delay(500, cancellationToken);
                continue;
            }
            lastSeen = DateTime.UtcNow;
            // The desktop is drawn only after sign-in; until then the window is black.
            if (Focus(main.Value.Handle, main.Value.ProcessId) && ShowsDesktop(main.Value.Handle))
            {
                ready ??= DateTime.UtcNow;
                int left = 4 - (int)(DateTime.UtcNow - ready.Value).TotalSeconds;
                status($"Đã kết nối. Sắp tự thao tác trong cửa sổ Remote Desktop ({Math.Max(left, 0)}s) · đừng chạm chuột hoặc bàn phím");
                if (left <= 0) break;
            }
            else
            {
                ready = null;
                status("Đang chờ màn hình máy thuê hiện ra (nếu Windows hỏi mật khẩu thì hãy nhập)…");
            }
            await Task.Delay(1000, cancellationToken);
        }
        if (ready is null) return "Không thấy màn hình máy thuê hiện ra trong 3 phút.";

        status("Đang tự mở PowerShell quản trị và dán lệnh cài đặt · đừng chạm chuột hoặc bàn phím");
        await Chord(fileToken, cancellationToken, VkLwin, 'R');                    // Run box
        await Task.Delay(1500, cancellationToken);
        foreach (char letter in "powershell") await Chord(fileToken, cancellationToken, letter);
        await Task.Delay(400, cancellationToken);
        await Chord(fileToken, cancellationToken, VkControl, VkShift, VkReturn);   // run as administrator
        // Windows may now show its UAC consent prompt inside the window; the user answers it. Wait for that.
        for (int second = 12; second > 0; second--)
        {
            status($"Nếu Windows hỏi quyền (UAC) trong cửa sổ Remote Desktop, hãy bấm Yes. Sẽ dán lệnh sau {second}s · đừng chạm bàn phím");
            await Task.Delay(1000, cancellationToken);
        }
        await Chord(fileToken, cancellationToken, VkControl, 'V');                 // paste the setup command
        await Task.Delay(2500, cancellationToken);
        await Chord(fileToken, cancellationToken, VkReturn);
        return null;
    }

    private static Window? FindMain(List<Window> windows, string fileToken) =>
        windows.FirstOrDefault(w => w.Visible && w.ClassName == "TscShellContainerClass" &&
                                    w.Title.Contains(fileToken, StringComparison.Ordinal));

    /// <summary>True when the window shows something other than black, which means the remote desktop is drawn.</summary>
    private static bool ShowsDesktop(IntPtr handle)
    {
        if (!GetWindowRect(handle, out Rect rect)) return false;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 200 || height < 150) return false;
        int x = rect.Left + width / 10, y = rect.Top + height / 5, w = width * 8 / 10, h = height * 6 / 10;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(w, h);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            int bright = 0, samples = 0;
            for (int row = 0; row < 16; row++)
                for (int column = 0; column < 24; column++)
                {
                    System.Drawing.Color pixel = bitmap.GetPixel(column * (w - 1) / 23, row * (h - 1) / 15);
                    samples++;
                    if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 48) bright++;
                }
            return bright * 100 / samples >= 25;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            return true; // The screen cannot be read here; do not hold the automation back because of that.
        }
    }

    /// <summary>Closes the Remote Desktop window of the given file, if it is still open.</summary>
    public static void Close(string fileToken)
    {
        foreach (Window window in TopLevelWindows().Where(w => w.ClassName == "TscShellContainerClass" && w.Title.Contains(fileToken, StringComparison.Ordinal)))
        {
            try { using Process process = Process.GetProcessById((int)window.ProcessId); process.Kill(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task Chord(string fileToken, CancellationToken cancellationToken, params ushort[] keys) =>
        await Press(fileToken, cancellationToken, keys);

    private static async Task Chord(string fileToken, CancellationToken cancellationToken, ushort first, char letter) =>
        await Press(fileToken, cancellationToken, first, (ushort)char.ToUpperInvariant(letter));

    private static async Task Chord(string fileToken, CancellationToken cancellationToken, char letter) =>
        await Press(fileToken, cancellationToken, (ushort)char.ToUpperInvariant(letter));

    private static async Task Press(string fileToken, CancellationToken cancellationToken, params ushort[] keys)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Window main = FindMain(TopLevelWindows(), fileToken)
            ?? throw new InvalidOperationException("Cửa sổ Remote Desktop đã đóng nên QT Studio dừng gõ phím.");
        if (!IsForeground(main.ProcessId) && !Focus(main.Handle, main.ProcessId))
            throw new InvalidOperationException("Cửa sổ Remote Desktop mất tiêu điểm nên QT Studio dừng gõ phím.");
        foreach (ushort key in keys) { Send(key, up: false); await Task.Delay(40, cancellationToken); }
        foreach (ushort key in Enumerable.Reverse(keys)) { Send(key, up: true); await Task.Delay(40, cancellationToken); }
        await Task.Delay(120, cancellationToken);
    }

    private static void Send(ushort virtualKey, bool up)
    {
        uint flags = (up ? KeyUp : 0) | (virtualKey == VkLwin ? KeyExtended : 0);
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new() { Keyboard = new() { VirtualKey = virtualKey, ScanCode = (ushort)MapVirtualKey(virtualKey, 0), Flags = flags } }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Windows từ chối gửi phím.");
    }

    private static bool Focus(IntPtr window, uint pid)
    {
        ShowWindow(window, 9); // Restore if minimized.
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();
        // Attaching to the foreground thread is the documented way to be allowed to take the focus.
        if (foregroundThread != thisThread) AttachThreadInput(thisThread, foregroundThread, true);
        try { SetForegroundWindow(window); }
        finally { if (foregroundThread != thisThread) AttachThreadInput(thisThread, foregroundThread, false); }
        for (int attempt = 0; attempt < 10 && !IsForeground(pid); attempt++) Thread.Sleep(100);
        return IsForeground(pid);
    }

    private static bool IsForeground(uint pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        return owner == pid;
    }

    private readonly record struct Window(IntPtr Handle, string ClassName, string Title, uint ProcessId, bool Visible)
    {
        public string ProcessName
        {
            get
            {
                try { using Process process = Process.GetProcessById((int)ProcessId); return process.ProcessName; }
                catch (ArgumentException) { return ""; }
            }
        }
    }

    private static List<Window> TopLevelWindows()
    {
        var found = new List<Window>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            found.Add(new(handle, TextOf(handle, className: true), TextOf(handle, className: false), owner, IsWindowVisible(handle)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string TextOf(IntPtr handle, bool className)
    {
        var text = new StringBuilder(512);
        if (className) GetClassName(handle, text, text.Capacity); else GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr Extra; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
