using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PopGlot.Windows.Services;

internal enum SessionOrigin
{
    TranslationPanel, // 划词/主浮窗
    QuickSearch,      // 极速查词
    ScreenshotOcr,    // 截图 OCR
    ScreenshotVision, // 截图视觉翻译
    Workbench,        // 主工作台
}

/// <summary>
/// Immutable snapshot of a translation session held in the in-memory session store (N01).
/// Per V2 & G05 rules, this record strictly holds text and metadata — NEVER raw image bytes
/// or GDI+/WPF bitmap handles.
/// </summary>
internal sealed record StoredSession(
    string SessionId,
    SessionOrigin Origin,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    string? EngineProfileId,
    string? EngineName,
    TranslationSessionState State,
    string? ResultText,
    string? ExplanationText,
    bool IsPartial,
    DateTime CreatedUtc,
    DateTime LastAccessedUtc,
    int TextByteCount,
    // Identity-only prompt template provenance (no instruction body). Optional
    // with defaults so every existing positional construction stays compatible.
    string? PromptTemplateId = null,
    string? PromptTemplateName = null,
    ulong? PromptTemplateRevision = null)
{
    public static StoredSession Create(
        string? sessionId,
        SessionOrigin origin,
        string sourceText,
        string? sourceLang,
        string? targetLang,
        string? engineProfileId,
        string? engineName,
        TranslationSessionState state,
        string? resultText,
        string? explanationText,
        bool isPartial = false,
        DateTime? createdUtc = null,
        string? promptTemplateId = null,
        string? promptTemplateName = null,
        ulong? promptTemplateRevision = null)
    {
        var id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        var now = DateTime.UtcNow;
        var src = sourceText ?? string.Empty;
        var res = resultText ?? string.Empty;
        var exp = explanationText ?? string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(src) +
                        Encoding.UTF8.GetByteCount(res) +
                        Encoding.UTF8.GetByteCount(exp);

        return new StoredSession(
            SessionId: id,
            Origin: origin,
            SourceText: src,
            SourceLanguage: sourceLang ?? "auto",
            TargetLanguage: targetLang ?? "zh-CN",
            EngineProfileId: engineProfileId,
            EngineName: engineName,
            State: state,
            ResultText: resultText,
            ExplanationText: explanationText,
            IsPartial: isPartial,
            CreatedUtc: createdUtc ?? now,
            LastAccessedUtc: now,
            TextByteCount: byteCount,
            PromptTemplateId: promptTemplateId,
            PromptTemplateName: promptTemplateName,
            PromptTemplateRevision: promptTemplateRevision
        );
    }

    /// <summary>Brief one-line summary for display in menus and list items.</summary>
    public string Summary(int maxLength = 35)
    {
        var originLabel = Origin switch
        {
            SessionOrigin.TranslationPanel => "划词",
            SessionOrigin.QuickSearch => "查词",
            SessionOrigin.ScreenshotOcr => "OCR",
            SessionOrigin.ScreenshotVision => "截图",
            SessionOrigin.Workbench => "工作台",
            _ => "翻译"
        };
        var cleanSource = SourceText.Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleanSource.Length > maxLength)
        {
            cleanSource = cleanSource[..maxLength] + "…";
        }
        var time = CreatedUtc.ToLocalTime().ToString("HH:mm:ss");
        return $"[{originLabel}] {time} - {cleanSource}";
    }
}

internal static class SessionStoreOptions
{
    public const int MaxSessionCount = 5;
    public const int MaxTotalTextBytes = 2 * 1024 * 1024; // 2 MiB
    public static readonly TimeSpan EvictionTtl = TimeSpan.FromMinutes(30);
}

internal interface ISessionStore
{
    bool TryStore(StoredSession session, out string? rejectionReason);
    StoredSession? PeekRecent();
    StoredSession? PopRecent();
    StoredSession? Get(string sessionId);
    IReadOnlyList<StoredSession> GetAll();
    void Clear();
    (int Count, int TotalBytes) GetUsageMetrics();
}

/// <summary>
/// In-memory LRU session store (N01).
/// Guarantees:
///  - Hard cap of 5 sessions.
///  - Hard cap of 2 MiB total text bytes.
///  - 30-minute idle TTL automatic eviction.
///  - Zero image references (text & metadata only).
///  - Zero disk persistence (cleared on process exit).
/// </summary>
internal sealed class SessionStore : ISessionStore
{
    private readonly object _sync = new();
    private readonly LinkedList<StoredSession> _list = new();
    private readonly Dictionary<string, LinkedListNode<StoredSession>> _map = new(StringComparer.Ordinal);
    private int _totalBytes;

    /// <summary>Clock hook for deterministic testing of TTL eviction.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public bool TryStore(StoredSession session, out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            PruneExpiredUnderLock();

            if (session.TextByteCount > SessionStoreOptions.MaxTotalTextBytes)
            {
                rejectionReason = $"会话文本大小 ({session.TextByteCount} 字节) 超过最大限制 (2 MiB)。";
                return false;
            }

            // If a session with this ID already exists, remove it first to re-insert at the head
            if (_map.TryGetValue(session.SessionId, out var existingNode))
            {
                _totalBytes -= existingNode.Value.TextByteCount;
                _list.Remove(existingNode);
                _map.Remove(session.SessionId);
            }

            // Evict oldest (tail) until within count and byte budgets
            while (_list.Count >= SessionStoreOptions.MaxSessionCount ||
                   (_totalBytes + session.TextByteCount > SessionStoreOptions.MaxTotalTextBytes && _list.Count > 0))
            {
                var tail = _list.Last!;
                _totalBytes -= tail.Value.TextByteCount;
                _map.Remove(tail.Value.SessionId);
                _list.RemoveLast();
            }

            // Touch last accessed
            var entry = session with { LastAccessedUtc = UtcNow() };
            var node = _list.AddFirst(entry);
            _map[entry.SessionId] = node;
            _totalBytes += entry.TextByteCount;

            rejectionReason = null;
            return true;
        }
    }

    public StoredSession? PeekRecent()
    {
        lock (_sync)
        {
            PruneExpiredUnderLock();
            return _list.First?.Value;
        }
    }

    public StoredSession? PopRecent()
    {
        lock (_sync)
        {
            PruneExpiredUnderLock();
            if (_list.First is null) return null;

            var node = _list.First;
            _totalBytes -= node.Value.TextByteCount;
            _map.Remove(node.Value.SessionId);
            _list.RemoveFirst();
            return node.Value;
        }
    }

    public StoredSession? Get(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        lock (_sync)
        {
            PruneExpiredUnderLock();
            if (!_map.TryGetValue(sessionId, out var node))
            {
                return null;
            }

            // Update access time and promote to head (LRU)
            var updated = node.Value with { LastAccessedUtc = UtcNow() };
            _list.Remove(node);
            var freshNode = _list.AddFirst(updated);
            _map[sessionId] = freshNode;
            return updated;
        }
    }

    public IReadOnlyList<StoredSession> GetAll()
    {
        lock (_sync)
        {
            PruneExpiredUnderLock();
            return _list.ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _list.Clear();
            _map.Clear();
            _totalBytes = 0;
        }
    }

    public (int Count, int TotalBytes) GetUsageMetrics()
    {
        lock (_sync)
        {
            PruneExpiredUnderLock();
            return (_list.Count, _totalBytes);
        }
    }

    private void PruneExpiredUnderLock()
    {
        var now = UtcNow();
        var current = _list.Last;
        while (current is not null)
        {
            var prev = current.Previous;
            if (now - current.Value.LastAccessedUtc > SessionStoreOptions.EvictionTtl)
            {
                _totalBytes -= current.Value.TextByteCount;
                _map.Remove(current.Value.SessionId);
                _list.Remove(current);
            }
            current = prev;
        }
    }
}
