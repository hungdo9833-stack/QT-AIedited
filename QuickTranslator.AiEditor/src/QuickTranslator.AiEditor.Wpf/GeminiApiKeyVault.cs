using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Wpf;

internal interface IGeminiCredentialStore
{
    string? Read(string target);
    void Write(string target, string value);
    void Delete(string target);
}

/// <summary>Stores the shared ordered Gemini key list only in Windows Credential Manager.</summary>
internal static class GeminiApiKeyVault
{
    internal const string ListTarget = "QTStudio.Gemini.ApiKeys.v2";
    internal const string LegacyTarget = "QTStudio.GeminiSurvey.ApiKey.v1";
    private static readonly IGeminiCredentialStore NativeStore = new WindowsCredentialStore();

    public static IReadOnlyList<string> ReadAll() => ReadAll(NativeStore);

    internal static IReadOnlyList<string> ReadAll(IGeminiCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        string? stored = store.Read(ListTarget);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                string[] values = JsonSerializer.Deserialize<string[]>(stored) ?? [];
                return Normalize(values);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Danh sách Gemini API key đã lưu có định dạng không hợp lệ.", ex);
            }
        }

        string? legacy = store.Read(LegacyTarget);
        if (string.IsNullOrWhiteSpace(legacy)) return [];
        string[] migrated = Normalize([legacy]);
        SaveAll(store, migrated);
        store.Delete(LegacyTarget);
        return migrated;
    }

    public static void SaveAll(IEnumerable<string> apiKeys) => SaveAll(NativeStore, apiKeys);

    internal static void SaveAll(IGeminiCredentialStore store, IEnumerable<string> apiKeys)
    {
        ArgumentNullException.ThrowIfNull(store);
        string[] keys = Normalize(apiKeys);
        if (keys.Length == 0)
        {
            store.Delete(ListTarget);
            return;
        }
        store.Write(ListTarget, JsonSerializer.Serialize(keys));
    }

    public static void Save(string apiKey) => SaveAll(ReadAll().Append(apiKey));

    public static void RemoveAt(int index) => RemoveAt(NativeStore, index);

    internal static void RemoveAt(IGeminiCredentialStore store, int index)
    {
        List<string> keys = ReadAll(store).ToList();
        if ((uint)index >= (uint)keys.Count) throw new ArgumentOutOfRangeException(nameof(index));
        keys.RemoveAt(index);
        SaveAll(store, keys);
    }

    public static string? Read() => ReadAll().FirstOrDefault();

    public static void Delete()
    {
        NativeStore.Delete(ListTarget);
        NativeStore.Delete(LegacyTarget);
    }

    public static string ResolveForSurvey() =>
        Read() ?? Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "";

    public static string Mask(string apiKey)
    {
        string key = apiKey?.Trim() ?? "";
        return key.Length >= 8 ? key[..4] + "…" + key[^4..] : "••••••••";
    }

    private static string[] Normalize(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string? value in values)
        {
            string key = value?.Trim() ?? "";
            if (key.Length == 0) continue;
            if (key.Contains('\r') || key.Contains('\n'))
                throw new ArgumentException("Mỗi Gemini API key phải nằm trên một dòng riêng.", nameof(values));
            if (seen.Add(key)) result.Add(key);
        }
        return result.ToArray();
    }

    private sealed class WindowsCredentialStore : IGeminiCredentialStore
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
                throw new ArgumentException("Danh sách Gemini API key vượt giới hạn lưu trữ Windows.");
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
                        "Không thể lưu Gemini API key vào Windows Credential Manager.");
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
                throw new Win32Exception(code, "Không thể đọc Gemini API key từ Windows Credential Manager.");
            }
            try
            {
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(handle);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
                if (credential.CredentialBlobSize > MaximumCredentialBytes || credential.CredentialBlobSize % 2 != 0)
                    throw new InvalidDataException("Gemini API key được lưu có định dạng không hợp lệ.");
                return Marshal.PtrToStringUni(credential.CredentialBlob,
                    checked((int)credential.CredentialBlobSize / 2));
            }
            finally { CredFree(handle); }
        }

        public void Delete(string target)
        {
            if (!CredDelete(target, CredTypeGeneric, 0) &&
                Marshal.GetLastWin32Error() is int code && code != ErrorNotFound)
                throw new Win32Exception(code, "Không thể xóa Gemini API key khỏi Windows Credential Manager.");
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
}
