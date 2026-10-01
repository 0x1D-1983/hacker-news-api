using System.Diagnostics.CodeAnalysis;
using HackerNews.Api.Telemetry;

namespace HackerNews.Api.Stories;

/// <summary>
/// Holds the latest good <see cref="BestStoriesSnapshot"/>. Requests only read memory; the snapshot
/// is swapped atomically by <see cref="BestStoriesRefreshWorker"/> after a successful rebuild.
/// </summary>
public sealed class BestStoriesCache(HackerNewsMetrics metrics)
{
    /// <summary>The Hacker News <c>beststories</c> endpoint returns at most this many ids.</summary>
    public const int MaxStoryCount = 200;

    private BestStoriesSnapshot? _current;

    /// <summary>The latest snapshot, or <c>null</c> until the first load succeeds.</summary>
    public BestStoriesSnapshot? Current => Volatile.Read(ref _current);

    public void Replace(BestStoriesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <returns><c>false</c> when no snapshot has been loaded yet.</returns>
    public bool TryGetBestStories(int count, [NotNullWhen(true)] out IReadOnlyList<Story>? stories)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        metrics.StoriesRequested();

        var snapshot = Current;
        if (snapshot is null)
        {
            stories = null;
            return false;
        }

        var all = snapshot.Stories;
        stories = all.Length <= count ? all : all[..count];
        return true;
    }
}
