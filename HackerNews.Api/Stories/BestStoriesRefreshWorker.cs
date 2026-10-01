using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>
/// Loads the best stories at startup, then rebuilds them every
/// <see cref="HackerNewsOptions.RefreshInterval"/>. A failed rebuild leaves the previous snapshot
/// in place and is retried after <see cref="HackerNewsOptions.RetryInterval"/>.
/// </summary>
public sealed class BestStoriesRefreshWorker(
    BestStoriesLoader loader,
    BestStoriesCache cache,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    ILogger<BestStoriesRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var refreshed = await RefreshAsync(stoppingToken);
                var delay = refreshed ? options.Value.RefreshInterval : options.Value.RetryInterval;
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <returns><c>true</c> when the cache now holds a freshly loaded snapshot.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            cache.Replace(await loader.LoadAsync(cancellationToken));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var current = cache.Current;
            if (current is null)
            {
                logger.LogError(
                    ex,
                    "Initial best-stories load failed; retrying in {RetryInterval}",
                    options.Value.RetryInterval);
            }
            else
            {
                logger.LogError(
                    ex,
                    "Best-stories refresh failed; still serving the snapshot from {RefreshedAt}; retrying in {RetryInterval}",
                    current.RefreshedAt,
                    options.Value.RetryInterval);
            }

            return false;
        }
    }
}
