namespace PopGlot.Windows.Services;

/// <summary>
/// C09: in-process credential vault for measurement and isolated runs. When
/// the app is launched with a pinned data root (POPGLOT_DATA_ROOT) or in
/// startup-smoke mode, credentials must never reach the real Windows
/// Credential Manager — a data-directory override alone is not full
/// resource isolation.
/// </summary>
internal sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool HasCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { return _secrets.ContainsKey(target); }
    }

    public string? LoadCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { return _secrets.GetValueOrDefault(target); }
    }

    public void SaveCredential(string secret, string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { _secrets[target] = secret; }
    }

    public void DeleteCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { _secrets.Remove(target); }
    }
}
