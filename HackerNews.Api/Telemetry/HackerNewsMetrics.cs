using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HackerNews.Api.Telemetry;

/// <summary>Custom activity source and instruments for the best-stories pipeline.</summary>
public sealed class HackerNewsMetrics
{
    public const string SourceName = "HackerNews.Api";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private readonly Counter<long> _storyRequests;
    private readonly Counter<long> _cacheRefreshes;
    private readonly Counter<long> _skippedItems;
    private readonly Histogram<double> _refreshDuration;

    public HackerNewsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(SourceName);

        _storyRequests = meter.CreateCounter<long>(
            "hackernews.best_stories.requests",
            unit: "{request}",
            description: "Best-stories requests served by this API.");
        _cacheRefreshes = meter.CreateCounter<long>(
            "hackernews.cache.refreshes",
            unit: "{refresh}",
            description: "Times the best-stories snapshot was rebuilt from Hacker News.");
        _skippedItems = meter.CreateCounter<long>(
            "hackernews.items.skipped",
            unit: "{item}",
            description: "Best-story ids skipped because the item was missing, deleted, or dead.");
        _refreshDuration = meter.CreateHistogram<double>(
            "hackernews.cache.refresh.duration",
            unit: "s",
            description: "Time taken to rebuild the best-stories snapshot.");
    }

    public void StoriesRequested() => _storyRequests.Add(1);

    public void ItemsSkipped(int count) => _skippedItems.Add(count);

    public void CacheRefreshed(TimeSpan elapsed, bool succeeded)
    {
        var outcome = new KeyValuePair<string, object?>("outcome", succeeded ? "success" : "failure");
        _cacheRefreshes.Add(1, outcome);
        _refreshDuration.Record(elapsed.TotalSeconds, outcome);
    }
}
