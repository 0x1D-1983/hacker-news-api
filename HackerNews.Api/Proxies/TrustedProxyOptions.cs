namespace HackerNews.Api.Proxies;

/// <summary>
/// Networks (CIDR) of reverse proxies whose <c>X-Forwarded-For</c> header is trusted. Empty by default,
/// so the TCP remote address is used. Loopback is always trusted.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxies";

    public List<string> Networks { get; set; } = [];
}
