using System.Runtime.CompilerServices;

namespace Fyrd.Core.CloudSessions;

public static class CloudSessionPaging
{
    /// <summary>Yields every saved event after <paramref name="after"/> (null = from the first), oldest first, one page at a time.</summary>
    public static async IAsyncEnumerable<SessionEvent> AllEventsAsync(
        this ICloudSessions cloud, string sessionId, string? after = null, int pageLimit = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await cloud.ListEventsAsync(sessionId, pageLimit, after, null, ct);
            foreach (var e in page.Events) yield return e;
            if (!page.HasMore || page.LastId is null || page.LastId == after) yield break;
            after = page.LastId;
        }
    }
}
