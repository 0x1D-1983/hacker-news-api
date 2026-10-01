using System.Collections.Immutable;
using System.Diagnostics;
using HackerNews.Api.HackerNews;
using HackerNews.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>Builds a complete, ranked <see cref="BestStoriesSnapshot"/> from Hacker News.</summary>
public sealed class BestStoriesLoader(
    HackerNewsClient client,
    IOptions<HackerNewsOptions> options,
    HackerNewsMetrics metrics,
    TimeProvider timeProvider,
    ILogger<BestStoriesLoader> logger)
{
    public async Task<BestStoriesSnapshot> LoadAsync(CancellationToken cancellationToken)
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
                "Loaded best stories: {StoryCount} stories from {IdCount} ids ({SkippedCount} skipped) in {ElapsedMs:F0} ms",
                stories.Length,
                ids.Count,
                skipped,
                elapsed.TotalMilliseconds);

            return new BestStoriesSnapshot(stories, timeProvider.GetUtcNow());
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
