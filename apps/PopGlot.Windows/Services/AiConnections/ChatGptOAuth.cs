using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

internal static class ChatGptOAuth
{
    private const string Issuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccountLocks = new();
    private static SemaphoreSlim AccountLock(string id) => AccountLocks.GetOrAdd(id, _ => new(1, 1));
    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string text)
    { var value = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    private static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join('&', values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public static async Task<ChatGptAccount> SignInAsync(ChatGptAccount? selected, CancellationToken ct, Action<string>? openBrowser = null)
    {
        DirectHttp.RequirePolicy();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var hostId = ChatGptAccountStore.HostId();
        var state = RandomValue(); var nonce = RandomValue(); var verifier = RandomValue();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/auth/callback";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = selected?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = Resource, ["state"] = state, ["nonce"] = nonce,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        };
        if (selected is null) parameters["agent_name_hint"] = "PopGlot";
        // Avoid putting existing ID tokens into browser command-line arguments.
        var url = Issuer + "/api/accounts/authorize?" + Query(parameters);
        DirectHttp.RequirePolicy();
        if (openBrowser is not null) openBrowser(url);
        else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var callback = await ReceiveCallbackAsync(listener, port, state, deadline.Token).ConfigureAwait(false);
        if (callback.ContainsKey("error")) throw new InvalidOperationException("ChatGPT 授权未完成，原账号保持不变。");
        var clientId = callback.GetValueOrDefault("client_id") ?? selected?.ClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "dynamic_agent_client" || clientId.Length > 256 ||
            clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || selected is not null && clientId != selected.ClientId)
            throw new InvalidOperationException("官方返回的客户端注册无效，原账号保持不变。");
        if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("授权没有返回有效代码。");
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/api/accounts/oauth/token")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
                ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource }) };
        var tokens = await DirectHttp.JsonAsync(request, deadline.Token).ConfigureAwait(false);
        var credentials = ParseTokens(tokens, null);
        var identity = await ValidateIdTokenAsync(credentials.IdToken, clientId, nonce, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested(); DirectHttp.RequirePolicy();
        if (selected is null) return ChatGptAccountStore.SaveValidated(clientId, identity.Subject, identity.Label, credentials, null);
        var gate = AccountLock(selected.Id); await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { return ChatGptAccountStore.SaveValidated(clientId, identity.Subject, identity.Label, credentials, selected); }
        finally { gate.Release(); }
    }

    private static async Task<Dictionary<string, string>> ReceiveCallbackAsync(TcpListener listener, int port, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var socketDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); socketDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var stream = client.GetStream(); var bytes = new byte[16384]; int used = 0;
            try
            {
                while (used < bytes.Length)
                {
                    var count = await stream.ReadAsync(bytes.AsMemory(used), socketDeadline.Token).ConfigureAwait(false);
                    if (count == 0) break; used += count;
                    if (Encoding.ASCII.GetString(bytes, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var header = Encoding.ASCII.GetString(bytes, 0, used);
                var lines = header.Split("\r\n"); var first = lines[0].Split(' ');
                if (used >= bytes.Length || first.Length != 3 || first[0] != "GET" || !first[1].StartsWith("/auth/callback?", StringComparison.Ordinal) ||
                    !lines.Any(l => l.Equals($"Host: 127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)))
                { await RespondAsync(stream, false, socketDeadline.Token); continue; }
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                bool duplicate = false;
                foreach (var entry in first[1]["/auth/callback?".Length..].Split('&'))
                {
                    var pair = entry.Split('=', 2);
                    if (pair.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1].Replace('+', ' ')))) duplicate = true;
                }
                if (duplicate || !values.TryGetValue("state", out var actual) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(state)))
                { await RespondAsync(stream, false, socketDeadline.Token); continue; }
                await RespondAsync(stream, true, socketDeadline.Token); return values;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
    private static Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken ct)
    {
        var body = accepted ? "Authorization callback received. Return to PopGlot to check the result." : "Invalid authorization callback.";
        var data = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        return stream.WriteAsync(data, ct).AsTask();
    }
    internal static ChatGptCredentials ParseTokens(JsonElement result, ChatGptCredentials? previous)
    {
        string Read(string key) => result.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var scope = Read("scope");
        if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct") || !scope.Split(' ').Contains("resource.invoke"))
            throw new InvalidOperationException("登录成功，但没有授权 ChatGPT 计划直连，请重新授权所需权限。");
        if (!Read("token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("官方返回了不支持的授权类型。");
        var access = Read("access_token"); var refresh = Read("refresh_token"); var id = Read("id_token");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || (previous is null && string.IsNullOrWhiteSpace(id)))
            throw new InvalidOperationException("官方返回的登录凭据不完整。");
        if (access.Length + refresh.Length + id.Length > 65536) throw new InvalidOperationException("授权凭据超过上限。");
        if (access.Any(char.IsControl) || refresh.Any(char.IsControl) || id.Any(char.IsControl)) throw new InvalidOperationException("授权凭据格式无效。");
        if (!result.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds is <= 0 or > 86400)
            throw new InvalidOperationException("官方返回的授权有效期无效。");
        var earliest = result.TryGetProperty("earliest_refresh_at", out var early) && early.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch) : DateTimeOffset.UtcNow;
        return new(access, refresh, string.IsNullOrEmpty(id) ? previous!.IdToken : id, scope,
            DateTimeOffset.UtcNow.AddSeconds(seconds), earliest, Guid.NewGuid().ToString("N"));
    }
    internal static async Task<(string Subject, string Label)> ValidateIdTokenAsync(string token, string clientId, string? nonce, CancellationToken ct)
    {
        var parts = token.Split('.'); if (parts.Length != 3 || token.Length > 32768) throw new InvalidOperationException("ID token 无效。");
        using var header = JsonDocument.Parse(Decode(parts[0])); using var claims = JsonDocument.Parse(Decode(parts[1]));
        var h = header.RootElement; var c = claims.RootElement;
        if (h.GetProperty("alg").GetString() != "RS256") throw new InvalidOperationException("ID token 签名算法不受支持。");
        using var keysRequest = new HttpRequestMessage(HttpMethod.Get, Issuer + "/.well-known/jwks.json");
        var jwks = await DirectHttp.JsonAsync(keysRequest, ct).ConfigureAwait(false);
        var matches = jwks.GetProperty("keys").EnumerateArray().Where(k => k.GetProperty("kid").GetString() == h.GetProperty("kid").GetString()).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("kty").GetString() != "RSA") throw new InvalidOperationException("ID token 签名密钥无效。");
        var key = matches[0];
        if (key.TryGetProperty("use", out var use) && use.GetString() != "sig" || key.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")
            throw new InvalidOperationException("ID token 签名密钥用途无效。");
        using var rsa = RSA.Create(); rsa.ImportParameters(new() { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("ID token 签名验证失败。");
        var audience = c.GetProperty("aud"); var validAudience = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId :
            audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(v => v.GetString() == clientId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (c.GetProperty("iss").GetString() != Issuer || !validAudience || c.GetProperty("exp").GetInt64() <= now ||
            c.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 30 ||
            nonce is not null && (!c.TryGetProperty("nonce", out var actualNonce) || actualNonce.GetString() != nonce))
            throw new InvalidOperationException("ID token 的身份、有效期或登录随机值不匹配。");
        if (audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1 && (!c.TryGetProperty("azp", out var azp) || azp.GetString() != clientId))
            throw new InvalidOperationException("ID token 的授权客户端不匹配。");
        var subject = c.GetProperty("sub").GetString(); if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("ID token 缺少账号身份。");
        var label = c.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : "ChatGPT 账号";
        label = string.IsNullOrWhiteSpace(label) ? "ChatGPT 账号" : label;
        return (subject, label[..Math.Min(label.Length, 120)]);
    }
    public static async Task<string> AccessTokenAsync(string accountId, CancellationToken ct)
    {
        var gate = AccountLock(accountId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = ChatGptAccountStore.Get(accountId); var credentials = ChatGptAccountStore.Load(accountId);
            if (credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return credentials.AccessToken;
            if (credentials.EarliestRefreshAt > DateTimeOffset.UtcNow) throw new InvalidOperationException("尚未到官方允许的刷新时间，请稍后重试。");
            using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/api/accounts/oauth/token")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                ["refresh_token"] = credentials.RefreshToken, ["resource"] = Resource }) };
            var tokens = await DirectHttp.JsonAsync(request, ct).ConfigureAwait(false); var replacement = ParseTokens(tokens, credentials);
            if (replacement.IdToken != credentials.IdToken)
            {
                var identity = await ValidateIdTokenAsync(replacement.IdToken, account.ClientId, null, ct).ConfigureAwait(false);
                if (identity.Subject != account.Subject) throw new InvalidOperationException("刷新返回了其他账号，未替换原登录。");
            }
            ct.ThrowIfCancellationRequested();
            ChatGptAccountStore.ReplaceCredentials(account.Id, credentials.Revision, replacement); return replacement.AccessToken;
        }
        finally { gate.Release(); }
    }
    public static async Task SignOutAsync(string accountId, CancellationToken ct)
    {
        foreach (var profile in ProfileManager.Load().Profiles.Where(p => p.DirectConnection?.AccountId == accountId))
            CliTranslationExecutor.CancelProfile(profile.Id);
        var gate = AccountLock(accountId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = ChatGptAccountStore.Get(accountId); var credentials = ChatGptAccountStore.Load(accountId);
            using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/api/accounts/oauth/revoke")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = credentials.RefreshToken,
                ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }) };
            try
            {
                DirectHttp.RequirePolicy(); using var response = await DirectHttp.Client.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("本机已退出；官方会话撤销失败，可到 ChatGPT 中撤销授权。");
            }
            finally { ChatGptAccountStore.ClearCredentials(accountId); }
        }
        finally { gate.Release(); }
    }
}
