namespace HackerNews.Api.HackerNews;

/// <summary>Subset of the Hacker News item payload this API needs. Unknown fields are ignored.</summary>
public sealed record HackerNewsItem(
    long Id,
    string? Type,
    string? By,
    long Time,
    string? Title,
    string? Url,
    int Score,
    int Descendants,
    bool Deleted,
    bool Dead);
