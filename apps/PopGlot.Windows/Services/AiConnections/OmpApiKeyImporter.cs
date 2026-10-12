using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>Explicit selected-provider API-key import only; OAuth rows are never fetched.</summary>
internal static class OmpApiKeyImporter
{
    private static readonly IReadOnlyDictionary<string, (string BaseUrl, string Environment)> Providers =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["openai"] = ("https://api.openai.com/v1", "OPENAI_API_KEY"),
            ["deepseek"] = ("https://api.deepseek.com/v1", "DEEPSEEK_API_KEY"),
            ["zhipu-coding-plan"] = ("https://open.bigmodel.cn/api/coding/paas/v4", "ZHIPU_API_KEY"),
        };
    public static bool Supports(string? provider) => provider is not null && Providers.ContainsKey(provider);
    public static ProviderProfile Import(string provider, string model, bool confirmed, string? testDatabase = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!confirmed) throw new InvalidOperationException("请先确认将所选提供方的 API Key 导入 PopGlot。");
        if (!Providers.TryGetValue(provider, out var mapping) || string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("此提供方不能直接导入 API Key，请保留 CLI 连接。");
        var agent = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        var database = testDatabase ?? Path.Combine(string.IsNullOrWhiteSpace(agent)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent") : agent, "agent.db");
        string? key = null;
        try
        {
            if (File.Exists(database))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString());
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT data FROM auth_credentials WHERE provider = $provider AND credential_type = 'api_key' AND disabled_cause IS NULL AND length(data) <= 16384 LIMIT 2";
                command.Parameters.AddWithValue("$provider", provider);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var data = reader.GetString(0); if (data.Length > 16384) throw new InvalidOperationException("API Key 记录超过上限。");
                    using var document = JsonDocument.Parse(data); key = document.RootElement.GetProperty("key").GetString();
                    if (reader.Read()) throw new InvalidOperationException("此提供方保存了多个 API Key，请手工添加指定 Key，避免使用错误账号。");
                }
            }
        }
        catch (Exception e) when (e is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法读取 OMP 的普通 API Key；可手工添加 API 引擎或保留 CLI 连接。"); }
        key ??= Environment.GetEnvironmentVariable(mapping.Environment);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 2400 || key.Any(char.IsControl))
            throw new InvalidOperationException("未找到可导入的普通 API Key。OAuth 登录和远程凭据代理不能通过此入口导入。");
        var id = "omp-api-" + Guid.NewGuid().ToString("N");
        var profile = new ProviderProfile { Id = id, Name = $"{provider} · {model} · API 直连", ApiBaseUrl = mapping.BaseUrl,
            TextEndpoint = "/chat/completions", TextModel = model, SupportsVision = false, VisionModel = "", CredentialRevision = 1, CredentialTarget = "PopGlot/provider/" + id };
        ct.ThrowIfCancellationRequested(); CredentialStore.SaveApiKey(key, profile.CredentialTarget);
        try { ct.ThrowIfCancellationRequested(); ProfileManager.SaveCliProfile(profile, null, true); }
        catch { CredentialStore.DeleteApiKey(profile.CredentialTarget); throw; }
        return profile;
    }
}
