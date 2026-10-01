using System.Collections.Immutable;

namespace HackerNews.Api.Stories;

public sealed record Story(
    string Title,
    string? Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);

/// <summary>All best stories ranked by descending score, as of <see cref="RefreshedAt"/>.</summary>
public sealed record BestStoriesSnapshot(ImmutableArray<Story> Stories, DateTimeOffset RefreshedAt);
