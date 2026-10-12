using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

internal sealed record ChatGptAccount(string Id, string ClientId, string Subject, string Label)
{
    public string DisplayName => $"{Label} · {Id[..8]}";
    public override string ToString() => DisplayName;
}
internal sealed record ChatGptCredentials(string AccessToken, string RefreshToken, string IdToken,
    string Scope, DateTimeOffset ExpiresAt, DateTimeOffset EarliestRefreshAt, string Revision);
internal sealed class DirectConnectionSettings
{
    public string AccountId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public bool Enabled { get; set; }
    public DirectConnectionSettings Clone() => new() { AccountId = AccountId, ModelId = ModelId, Enabled = Enabled };
}

/// <summary>Only PopGlot-owned registrations. No reads of another tool's token files.</summary>
internal static class ChatGptAccountStore
{
    private static readonly object Gate = new();
    private static string DirectoryPath => Path.Combine(StoragePaths.CoreConfigDirectory, "chatgpt-accounts");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PopGlot.ChatGPT.v1");
    private static string PathFor(string id, string extension)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("账号记录无效，请重新登录。");
        return Path.Combine(DirectoryPath, id + extension);
    }
    public static string HostId()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "host.json");
            if (File.Exists(path))
            {
                var value = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
                if (value?.StartsWith("urn:uuid:", StringComparison.Ordinal) == true && Guid.TryParse(value[9..], out _)) return value;
                throw new InvalidOperationException("登录主机标识损坏，请恢复配置后重试。");
            }
            var host = "urn:uuid:" + Guid.NewGuid();
            AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(host)); return host;
        }
    }
    public static IReadOnlyList<ChatGptAccount> List()
    {
        lock (Gate)
        {
            if (!Directory.Exists(DirectoryPath)) return [];
            var accounts = new List<ChatGptAccount>();
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.account.json").Take(100))
            {
                try
                {
                    if (new FileInfo(path).Length > 16384) continue;
                    var account = JsonSerializer.Deserialize<ChatGptAccount>(File.ReadAllBytes(path));
                    if (account is not null && Guid.TryParseExact(account.Id, "N", out _)) accounts.Add(account);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return accounts;
        }
    }
    public static ChatGptAccount Get(string id) => List().FirstOrDefault(a => a.Id == id)
        ?? throw new InvalidOperationException("账号记录不存在，请重新登录。");
    public static bool HasCredentials(string id)
    { try { return File.Exists(PathFor(id, ".credentials")); } catch (InvalidOperationException) { return false; } }
    public static ChatGptCredentials Load(string id)
    {
        lock (Gate)
        {
            try
            {
                var path = PathFor(id, ".credentials");
                if (!File.Exists(path) || new FileInfo(path).Length > 131072) throw new InvalidOperationException("请先登录 ChatGPT。");
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try { return JsonSerializer.Deserialize<ChatGptCredentials>(plain) ?? throw new InvalidOperationException("登录记录无效。"); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("无法读取此 Windows 用户的登录，请重新登录 ChatGPT。"); }
        }
    }
    public static ChatGptAccount SaveValidated(string clientId, string subject, string label, ChatGptCredentials credentials, ChatGptAccount? selected)
    {
        lock (Gate)
        {
            if (selected is not null && (selected.ClientId != clientId || selected.Subject != subject))
                throw new InvalidOperationException("返回的账号与所选账号不同，未替换原登录。");
            var account = selected ?? List().FirstOrDefault(a => a.ClientId == clientId && a.Subject == subject)
                ?? new ChatGptAccount(Guid.NewGuid().ToString("N"), clientId, subject, label);
            Directory.CreateDirectory(DirectoryPath);
            WriteCredentials(account.Id, credentials);
            AtomicWrite(PathFor(account.Id, ".account.json"), JsonSerializer.SerializeToUtf8Bytes(account));
            return account;
        }
    }
    public static void ReplaceCredentials(string id, string expectedRevision, ChatGptCredentials replacement)
    {
        lock (Gate)
        {
            if (Load(id).Revision != expectedRevision) throw new InvalidOperationException("登录已变化，请重试请求。");
            WriteCredentials(id, replacement);
        }
    }
    public static void ClearCredentials(string id)
    { lock (Gate) { var path = PathFor(id, ".credentials"); if (File.Exists(path)) File.Delete(path); } }
    private static void WriteCredentials(string id, ChatGptCredentials credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { AtomicWrite(PathFor(id, ".credentials"), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
