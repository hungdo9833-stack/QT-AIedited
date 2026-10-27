using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>Windows Credential Manager (generic credentials) for secrets that must never touch Project files.</summary>
/// <param name="label">Secret name used in error messages.</param>
/// <param name="oversizeLabel">Name used when the value exceeds the Windows size limit.</param>
internal sealed class WindowsCredentialStore(string label, string? oversizeLabel = null) : IGeminiCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBytes = 5120;

    public void Write(string target, string value)
    {
        byte[] secret = Encoding.Unicode.GetBytes(value);
        if (secret.Length > MaximumCredentialBytes)
        {
            Array.Clear(secret);
            throw new ArgumentException($"{oversizeLabel ?? label} vượt giới hạn lưu trữ Windows.");
        }
        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(secret.Length);
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric, TargetName = target,
                CredentialBlobSize = (uint)secret.Length, CredentialBlob = blob,
                Persist = CredPersistLocalMachine, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Không thể lưu {label} vào Windows Credential Manager.");
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

    public string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out IntPtr handle))
        {
            int code = Marshal.GetLastWin32Error();
            if (code == ErrorNotFound) return null;
            throw new Win32Exception(code, $"Không thể đọc {label} từ Windows Credential Manager.");
        }
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            if (credential.CredentialBlobSize > MaximumCredentialBytes || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidDataException($"{label} được lưu có định dạng không hợp lệ.");
            return Marshal.PtrToStringUni(credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(handle); }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0) &&
            Marshal.GetLastWin32Error() is int code && code != ErrorNotFound)
            throw new Win32Exception(code, $"Không thể xóa {label} khỏi Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob;
        public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")] private static extern void CredFree(IntPtr credential);
}
