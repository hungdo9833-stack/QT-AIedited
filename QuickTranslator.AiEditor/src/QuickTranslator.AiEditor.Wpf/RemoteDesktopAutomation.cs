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
        Window? main = null;
        DateTime until = DateTime.UtcNow.AddSeconds(120);
        status("Đang chờ Remote Desktop kết nối…");
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Window> windows = TopLevelWindows();
            main = windows.FirstOrDefault(w => w.Visible && w.ClassName == "TscShellContainerClass" && w.Title.Contains(fileToken, StringComparison.Ordinal));
            if (main is not null) break;
            // Windows asks about an unsigned .rdp file; that decision stays with the user.
            if (windows.Any(w => w.Visible && w.ClassName == "#32770" && w.ProcessName == "mstsc"))
                status("Windows hỏi về kết nối này: hãy bấm Connect trong hộp thoại Remote Desktop");
            await Task.Delay(500, cancellationToken);
        }
        if (main is null) return "Không thấy cửa sổ Remote Desktop kết nối.";
        uint pid = main.Value.ProcessId;
        IntPtr handle = main.Value.Handle;

        // Give the remote desktop time to appear and settle after sign-in.
        for (int second = 14; second > 0; second--)
        {
            status($"Đã kết nối. Sắp tự thao tác trong cửa sổ Remote Desktop ({second}s) · đừng chạm chuột hoặc bàn phím");
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpen(handle)) return "Cửa sổ Remote Desktop đã đóng.";
            await Task.Delay(1000, cancellationToken);
        }

        status("Đang tự mở PowerShell quản trị và dán lệnh cài đặt · đừng chạm chuột hoặc bàn phím");
        if (!Focus(handle, pid)) return "Không đưa được cửa sổ Remote Desktop lên trước để gõ phím.";
        await Chord(pid, cancellationToken, VkLwin, 'R');                         // Run box
        await Task.Delay(1500, cancellationToken);
        foreach (char letter in "powershell") await Chord(pid, cancellationToken, letter);
        await Task.Delay(400, cancellationToken);
        await Chord(pid, cancellationToken, VkControl, VkShift, VkReturn);         // run as administrator
        // Windows may now show its UAC consent prompt inside the window; the user answers it. Wait for that.
        for (int second = 12; second > 0; second--)
        {
            status($"Nếu Windows hỏi quyền (UAC) trong cửa sổ Remote Desktop, hãy bấm Yes. Sẽ dán lệnh sau {second}s · đừng chạm bàn phím");
            await Task.Delay(1000, cancellationToken);
        }
        if (!Focus(handle, pid)) return "Cửa sổ Remote Desktop không còn ở trước để dán lệnh.";
        await Chord(pid, cancellationToken, VkControl, 'V');                       // paste the setup command
        await Task.Delay(2500, cancellationToken);
        await Chord(pid, cancellationToken, VkReturn);
        return null;
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

    private static async Task Chord(uint pid, CancellationToken cancellationToken, params ushort[] keys) =>
        await Press(pid, cancellationToken, keys);

    private static async Task Chord(uint pid, CancellationToken cancellationToken, ushort first, char letter) =>
        await Press(pid, cancellationToken, first, (ushort)char.ToUpperInvariant(letter));

    private static async Task Chord(uint pid, CancellationToken cancellationToken, char letter) =>
        await Press(pid, cancellationToken, (ushort)char.ToUpperInvariant(letter));

    private static async Task Press(uint pid, CancellationToken cancellationToken, params ushort[] keys)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForeground(pid))
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

    private static bool IsOpen(IntPtr handle) => IsWindow(handle);

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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
