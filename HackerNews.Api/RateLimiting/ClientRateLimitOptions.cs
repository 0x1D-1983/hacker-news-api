using System.ComponentModel.DataAnnotations;

namespace HackerNews.Api.RateLimiting;

/// <summary>Token bucket applied per client IP address to the public story endpoints.</summary>
public sealed class ClientRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Burst size: requests a client can make back to back.</summary>
    [Range(1, 10_000)]
    public int TokenLimit { get; set; } = 30;

    [Range(1, 10_000)]
    public int TokensPerPeriod { get; set; } = 30;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromMinutes(1);
}
