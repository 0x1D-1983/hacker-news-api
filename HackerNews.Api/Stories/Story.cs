using System.Collections.Immutable;
using System.ComponentModel;

namespace HackerNews.Api.Stories;

[ImmutableObject(true)]
public sealed record Story(
    string Title,
    string? Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);

/// <summary>
/// All best stories ranked by descending score. Marked immutable so <c>HybridCache</c>
/// can hand out the same instance instead of deserializing a copy on every hit.
/// </summary>
[ImmutableObject(true)]
public sealed record BestStoriesSnapshot(ImmutableArray<Story> Stories);
