using System.Collections.Immutable;
using System.Diagnostics;
using HackerNews.Api.HackerNews;
using HackerNews.Api.Telemetry;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>
/// Serves the best stories ranked by score from a single cached snapshot, so Hacker News is
/// queried at most once per <see cref="HackerNewsOptions.CacheDuration"/> regardless of how many
/// callers there are or which <c>n</c> they ask for. <see cref="HybridCache"/> collapses
/// concurrent misses into one refresh.
/// </summary>
public sealed class BestStoriesService(
    HackerNewsClient client,
    HybridCache cache,
    IOptions<HackerNewsOptions> options,
    HackerNewsMetrics metrics,
    ILogger<BestStoriesService> logger)
{
    /// <summary>The Hacker News <c>beststories</c> endpoint returns at most this many ids.</summary>
    public const int MaxStoryCount = 200;

    private const string CacheKey = "hackernews:best-stories";

    public async Task<IReadOnlyList<Story>> GetBestStoriesAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        metrics.StoriesRequested();

        var cacheDuration = options.Value.CacheDuration;
        var snapshot = await cache.GetOrCreateAsync(
            CacheKey,
            this,
            static (service, token) => service.LoadSnapshotAsync(token),
            new HybridCacheEntryOptions { Expiration = cacheDuration, LocalCacheExpiration = cacheDuration },
            cancellationToken: cancellationToken);

        var stories = snapshot.Stories;
        return stories.Length <= count ? stories : stories[..count];
    }

    private async ValueTask<BestStoriesSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        using var activity = HackerNewsMetrics.ActivitySource.StartActivity("RefreshBestStories");
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var ids = await client.GetBestStoryIdsAsync(cancellationToken);
            var items = new HackerNewsItem?[ids.Count];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, ids.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Value.MaxConcurrentRequests,
                    CancellationToken = cancellationToken
                },
                async (index, token) => items[index] = await client.GetItemAsync(ids[index], token));

            // OrderByDescending is stable, so equal scores keep Hacker News' own best-story order.
            var stories = items
                .Where(IsLive)
                .Select(item => ToStory(item!))
                .OrderByDescending(story => story.Score)
                .ToImmutableArray();

            var skipped = ids.Count - stories.Length;
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            metrics.ItemsSkipped(skipped);
            metrics.CacheRefreshed(elapsed, succeeded: true);
            activity?.SetTag("hackernews.story_count", stories.Length);
            activity?.SetTag("hackernews.skipped_count", skipped);

            logger.LogInformation(
                "Refreshed best stories: {StoryCount} stories from {IdCount} ids ({SkippedCount} skipped) in {ElapsedMs:F0} ms",
                stories.Length,
                ids.Count,
                skipped,
                elapsed.TotalMilliseconds);

            return new BestStoriesSnapshot(stories);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.CacheRefreshed(Stopwatch.GetElapsedTime(startedAt), succeeded: false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    private static bool IsLive(HackerNewsItem? item) => item is { Deleted: false, Dead: false };

    private static Story ToStory(HackerNewsItem item) => new(
        Title: item.Title ?? string.Empty,
        Uri: string.IsNullOrWhiteSpace(item.Url) ? null : item.Url,
        PostedBy: item.By ?? string.Empty,
        Time: DateTimeOffset.FromUnixTimeSeconds(item.Time),
        Score: item.Score,
        CommentCount: item.Descendants);
}
