using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal sealed partial class CredentialStore : ICredentialVault
{
    public const string DefaultTargetName = "PopGlot/OpenAICompatibleApiKey";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaxKeyCharacters = 2048;

    public static CredentialStore Instance { get; } = new();

    public static void SaveApiKey(string apiKey, string targetName = DefaultTargetName)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DeleteApiKey(targetName);
            return;
        }
        if (apiKey.Length > MaxKeyCharacters)
        {
            throw new ArgumentException($"API Key 不能超过 {MaxKeyCharacters} 个字符。", nameof(apiKey));
        }

        var secretBytes = Encoding.Unicode.GetBytes(apiKey);
        var secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安全保存 API Key。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            for (var index = 0; index < secretBytes.Length; index++)
            {
                Marshal.WriteByte(secretPointer, index, 0);
            }
            Marshal.FreeCoTaskMem(secretPointer);
        }
    }

    public static bool HasApiKey(string targetName = DefaultTargetName)
    {
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPointer))
        {
            const int ErrorNotFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }
            throw new Win32Exception(error, "无法读取 API Key 状态。");
        }
        CredFree(credentialPointer);
        return true;
    }

    public static string? LoadApiKey(string targetName = DefaultTargetName)
    {
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPointer))
        {
            const int ErrorNotFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, "无法读取 API Key。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
            {
                return null;
            }
            if (credential.CredentialBlobSize > MaxKeyCharacters * sizeof(char))
            {
                throw new InvalidOperationException("凭据管理器中的 API Key 超过安全大小上限。");
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                return Encoding.Unicode.GetString(secretBytes).TrimEnd('\0');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void DeleteApiKey(string targetName = DefaultTargetName)
    {
        if (!CredDelete(targetName, CredTypeGeneric, 0))
        {
            const int ErrorNotFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法删除已保存的 API Key。");
            }
        }
    }

    // ICredentialVault implementation
    bool ICredentialVault.HasCredential(string target) => HasApiKey(target);
    string? ICredentialVault.LoadCredential(string target) => LoadApiKey(target);
    void ICredentialVault.SaveCredential(string secret, string target) => SaveApiKey(secret, target);
    void ICredentialVault.DeleteCredential(string target) => DeleteApiKey(target);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    #pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
    #pragma warning restore SYSLIB1054
}
