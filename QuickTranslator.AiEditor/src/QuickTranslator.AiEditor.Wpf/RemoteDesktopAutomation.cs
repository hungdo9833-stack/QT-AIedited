using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Does by key presses what a person would do in the Remote Desktop window QT Studio just opened: open PowerShell
/// as administrator (Win+R, "powershell", Ctrl+Shift+Enter) and paste the setup command that is on the clipboard.
/// Windows' own security prompts (the "unknown publisher" box and the UAC consent) are never answered for the user;
/// the automation waits until the user has answered them. Only the mstsc window that QT Studio started is ever
/// touched, and nothing is typed unless that window has the keyboard focus.
/// </summary>
internal static class RemoteDesktopAutomation
{
    private const ushort VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkLwin = 0x5B;
    private const uint InputKeyboard = 1, KeyUp = 0x0002, KeyExtended = 0x0001;

    /// <summary>Returns null when every key was sent, otherwise a short reason the automation stopped.</summary>
    public static async Task<string?> RunAsync(Process remoteDesktop, Action<string> status, CancellationToken cancellationToken)
    {
        uint pid = (uint)remoteDesktop.Id;
        IntPtr main = IntPtr.Zero;
        DateTime until = DateTime.UtcNow.AddSeconds(120);
        status("Đang chờ Remote Desktop kết nối…");
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remoteDesktop.HasExited) return "Cửa sổ Remote Desktop đã đóng.";
            List<Window> windows = WindowsOf(pid);
            // Windows asks about an unsigned .rdp file; that decision stays with the user.
            if (windows.Any(w => w.Visible && w.ClassName == "#32770"))
            {
                status("Windows hỏi về kết nối này: hãy bấm Connect trong hộp thoại Remote Desktop");
                await Task.Delay(500, cancellationToken);
                continue;
            }
            Window? shell = windows.FirstOrDefault(w => w.Visible && w.ClassName == "TscShellContainerClass");
            if (shell is { } found) { main = found.Handle; break; }
            await Task.Delay(500, cancellationToken);
        }
        if (main == IntPtr.Zero) return "Không thấy cửa sổ Remote Desktop kết nối.";

        // Give the remote desktop time to appear and settle after sign-in.
        for (int second = 14; second > 0; second--)
        {
            status($"Đã kết nối. Sắp tự thao tác trong cửa sổ Remote Desktop ({second}s) · đừng chạm chuột hoặc bàn phím");
            cancellationToken.ThrowIfCancellationRequested();
            if (remoteDesktop.HasExited) return "Cửa sổ Remote Desktop đã đóng.";
            await Task.Delay(1000, cancellationToken);
        }

        status("Đang tự mở PowerShell quản trị và dán lệnh cài đặt · đừng chạm chuột hoặc bàn phím");
        if (!Focus(main, pid)) return "Không đưa được cửa sổ Remote Desktop lên trước để gõ phím.";
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
        if (!Focus(main, pid)) return "Cửa sổ Remote Desktop không còn ở trước để dán lệnh.";
        await Chord(pid, cancellationToken, VkControl, 'V');                       // paste the setup command
        await Task.Delay(2500, cancellationToken);
        await Chord(pid, cancellationToken, VkReturn);
        return null;
    }

    private static async Task Chord(uint pid, CancellationToken cancellationToken, params ushort[] keys) =>
        await Chord(pid, cancellationToken, requireFocus: true, keys);

    private static async Task Chord(uint pid, CancellationToken cancellationToken, ushort first, char letter, bool requireFocus = true) =>
        await Chord(pid, cancellationToken, requireFocus, first, (ushort)char.ToUpperInvariant(letter));

    private static async Task Chord(uint pid, CancellationToken cancellationToken, char letter) =>
        await Chord(pid, cancellationToken, requireFocus: true, (ushort)char.ToUpperInvariant(letter));

    private static async Task Chord(uint pid, CancellationToken cancellationToken, bool requireFocus, params ushort[] keys)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requireFocus && !IsForeground(pid))
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

    private readonly record struct Window(IntPtr Handle, string ClassName, bool Visible);

    private static List<Window> WindowsOf(uint pid)
    {
        var found = new List<Window>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == pid) found.Add(new(handle, ClassOf(handle), IsWindowVisible(handle)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string ClassOf(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
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
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
