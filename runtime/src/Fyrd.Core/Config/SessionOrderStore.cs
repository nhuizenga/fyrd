using System.Text.Json;

namespace Fyrd.Core.Config;

/// <summary>The owner's manual session-list order: session ids (flat, in display order) and persona groups (in display order).</summary>
public sealed record SessionOrder(IReadOnlyList<string> Sessions, IReadOnlyList<string> Groups)
{
    public const int MaxEntries = 2000, MaxLength = 200;
    public static SessionOrder Empty { get; } = new([], []);

    /// <summary>Drops blanks, over-long entries and duplicates, and caps the lists. Returns null when a list is oversized.</summary>
    public static SessionOrder? Normalize(IReadOnlyList<string>? sessions, IReadOnlyList<string>? groups)
    {
        if ((sessions?.Count ?? 0) > MaxEntries || (groups?.Count ?? 0) > MaxEntries) return null;
        static List<string> Clean(IEnumerable<string>? xs) => (xs ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= MaxLength).Distinct(StringComparer.Ordinal).ToList();
        return new SessionOrder(Clean(sessions), Clean(groups));
    }
}

public sealed class SessionOrderStore(FyrdPaths paths)
{
    private string File_ => Path.Combine(paths.Root, "session-order.json");
    private readonly object gate = new();

    public SessionOrder Load()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(File_)) return SessionOrder.Empty;
                var o = JsonSerializer.Deserialize<SessionOrder>(File.ReadAllText(File_), Json.Options);
                return SessionOrder.Normalize(o?.Sessions, o?.Groups) ?? SessionOrder.Empty;
            }
            catch (Exception e) when (e is JsonException or IOException) { return SessionOrder.Empty; }
        }
    }

    public void Save(SessionOrder order)
    {
        lock (gate) AtomicFile.Write(File_, JsonSerializer.Serialize(order, Json.Options));
    }
}
