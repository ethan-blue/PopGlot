namespace PopGlot.Windows.Services;

internal enum LibrarySurface
{
    History,
    Vocabulary,
}

/// <summary>
/// In-memory undo for one library delete or clear. It is not written to disk,
/// so a restart cannot offer an undo that no longer exists.
/// </summary>
internal sealed class LibraryUndoTicket
{
    public required Guid Id { get; init; }
    public required LibrarySurface Surface { get; init; }
    public required DateTime ExpiresUtc { get; init; }
    public IReadOnlyList<TranslationHistoryEntry> HistoryItems { get; init; } = [];
    public IReadOnlyList<VocabularyWord> VocabularyItems { get; init; } = [];
}

internal static class LibraryUndoJournal
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(20);

    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, LibraryUndoTicket> Tickets = new();
    private const int MaxTickets = 16;

    public static void ResetForTests()
    {
        lock (Gate)
        {
            Tickets.Clear();
        }
    }

    public static LibraryUndoTicket? Current(DateTime utcNow)
    {
        lock (Gate)
        {
            PruneExpired(utcNow);
            return Tickets.Values.OrderByDescending(ticket => ticket.ExpiresUtc).FirstOrDefault();
        }
    }

    public static bool IsAvailable(Guid id, DateTime utcNow)
    {
        lock (Gate)
        {
            PruneExpired(utcNow);
            return Tickets.ContainsKey(id);
        }
    }

    public static LibraryUndoTicket OfferHistory(
        IReadOnlyList<TranslationHistoryEntry> items,
        DateTime utcNow)
    {
        var ticket = new LibraryUndoTicket
        {
            Id = Guid.NewGuid(),
            Surface = LibrarySurface.History,
            ExpiresUtc = utcNow + DefaultWindow,
            HistoryItems = items.ToArray(),
        };
        lock (Gate)
        {
            Add(ticket, utcNow);
        }

        return ticket;
    }

    public static LibraryUndoTicket OfferVocabulary(
        IReadOnlyList<VocabularyWord> items,
        DateTime utcNow)
    {
        var ticket = new LibraryUndoTicket
        {
            Id = Guid.NewGuid(),
            Surface = LibrarySurface.Vocabulary,
            ExpiresUtc = utcNow + DefaultWindow,
            VocabularyItems = items.ToArray(),
        };
        lock (Gate)
        {
            Add(ticket, utcNow);
        }

        return ticket;
    }

    /// <summary>Removes the ticket only when it is still the active one and unexpired.</summary>
    public static LibraryUndoTicket? Take(Guid id, DateTime utcNow)
    {
        lock (Gate)
        {
            PruneExpired(utcNow);
            if (!Tickets.Remove(id, out var ticket)) return null;
            return ticket;
        }
    }

    private static void Add(LibraryUndoTicket ticket, DateTime utcNow)
    {
        PruneExpired(utcNow);
        Tickets[ticket.Id] = ticket;
        while (Tickets.Count > MaxTickets)
        {
            var oldest = Tickets.Values.MinBy(item => item.ExpiresUtc)!;
            Tickets.Remove(oldest.Id);
        }
    }

    private static void PruneExpired(DateTime utcNow)
    {
        foreach (var id in Tickets.Where(pair => utcNow > pair.Value.ExpiresUtc).Select(pair => pair.Key).ToArray())
            Tickets.Remove(id);
    }
}
