using System.Net.Http.Json;

namespace HackerNews.Api.HackerNews;

/// <summary>
/// Typed client for the Hacker News Firebase API. Retries, timeouts, and circuit breaking
/// are applied by the resilience handler registered with this client.
/// </summary>
public sealed class HackerNewsClient(HttpClient httpClient)
{
    private const string BestStoriesPath = "beststories.json";
    private const string ItemPathFormat = "item/{0}.json";

    public async Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await GetAsync<long[]>(BestStoriesPath, cancellationToken);
        return ids ?? [];
    }

    /// <returns>The item, or <c>null</c> when Hacker News has no item with that id.</returns>
    public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        => GetAsync<HackerNewsItem>(string.Format(ItemPathFormat, id), cancellationToken);

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HackerNewsUnavailableException($"Hacker News request to '{path}' failed.", ex);
        }
    }
}
