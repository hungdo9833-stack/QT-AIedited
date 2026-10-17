using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Stores the single Gemini API key in Windows Credential Manager for the current
/// Windows user. This secret never enters Project, prompt presets, or repository files.
/// </summary>
internal static class GeminiApiKeyVault
{
    private const string Target = "QTStudio.GeminiSurvey.ApiKey.v1";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBytes = 5120;

    public static void Save(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        string key = apiKey.Trim();
        if (key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException("Gemini API key không được chứa ký tự xuống dòng.");

        byte[] secret = Encoding.Unicode.GetBytes(key);
        if (secret.Length > MaximumCredentialBytes)
        {
            Array.Clear(secret);
            throw new ArgumentException("Gemini API key vượt giới hạn lưu trữ Windows.");
        }

        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(secret.Length);
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể lưu Gemini API key vào Windows Credential Manager.");
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                Marshal.Copy(new byte[secret.Length], 0, blob, secret.Length);
                Marshal.FreeHGlobal(blob);
            }
            Array.Clear(secret);
        }
    }

    public static string? Read()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out IntPtr handle))
        {
            int code = Marshal.GetLastWin32Error();
            if (code == ErrorNotFound) return null;
            throw new Win32Exception(code, "Không thể đọc Gemini API key từ Windows Credential Manager.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;
            if (credential.CredentialBlobSize > MaximumCredentialBytes ||
                credential.CredentialBlobSize % 2 != 0)
                throw new InvalidDataException("Gemini API key được lưu có định dạng không hợp lệ.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(handle); }
    }

    public static void Delete()
    {
        if (!CredDelete(Target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() is int code && code != ErrorNotFound)
            throw new Win32Exception(code, "Không thể xóa Gemini API key khỏi Windows Credential Manager.");
    }

    // The environment variable remains a compatibility fallback for existing scripts.
    // An explicitly saved app key always wins.
    public static string ResolveForSurvey() =>
        Read() ?? Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
}
