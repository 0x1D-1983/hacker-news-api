using System.ComponentModel.DataAnnotations;

namespace HackerNews.Api.HackerNews;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    [Required]
    public Uri BaseAddress { get; set; } = new("https://hacker-news.firebaseio.com/v0/");

    /// <summary>
    /// TTL of the best-stories snapshot: the API reports ready only while the snapshot is younger than this.
    /// The Hacker News API documents no TTL, so this is a trade-off between freshness and upstream load.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Time between successful rebuilds. Must be shorter than <see cref="CacheDuration"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(55);

    /// <summary>Time before retrying after a failed rebuild.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound on parallel item requests sent to Hacker News during a refresh.</summary>
    [Range(1, 32)]
    public int MaxConcurrentRequests { get; set; } = 8;
}
