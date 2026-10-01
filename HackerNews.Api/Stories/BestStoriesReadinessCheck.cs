using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>Ready only when a snapshot exists and is younger than <see cref="HackerNewsOptions.CacheDuration"/>.</summary>
public sealed class BestStoriesReadinessCheck(
    BestStoriesCache cache,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public const string Name = "best-stories";
    public const string ReadyTag = "ready";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = cache.Current;
        if (snapshot is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("Best stories have not been loaded yet."));

        var age = timeProvider.GetUtcNow() - snapshot.RefreshedAt;
        var data = new Dictionary<string, object>
        {
            ["refreshedAt"] = snapshot.RefreshedAt,
            ["ageSeconds"] = (int)age.TotalSeconds,
            ["storyCount"] = snapshot.Stories.Length
        };

        var result = age <= options.Value.CacheDuration
            ? HealthCheckResult.Healthy("Best stories are fresh.", data)
            : HealthCheckResult.Unhealthy("Best stories are stale.", data: data);

        return Task.FromResult(result);
    }
}
