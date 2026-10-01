using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace HackerNews.Api.Proxies;

public static class TrustedProxyExtensions
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrustedProxyOptions>()
            .Bind(configuration.GetSection(TrustedProxyOptions.SectionName))
            .Validate(
                options => options.Networks.TrueForAll(network => IPNetwork.TryParse(network, out _)),
                $"{TrustedProxyOptions.SectionName}:{nameof(TrustedProxyOptions.Networks)} must contain CIDR ranges such as 10.244.0.0/16.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<TrustedProxyOptions>>((forwarded, trusted) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                // Only the right-most X-Forwarded-For entry, which the trusted proxy appended itself;
                // anything to its left was supplied by the client and can be spoofed.
                forwarded.ForwardLimit = 1;

                foreach (var network in trusted.Value.Networks)
                    forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
            });

        return services;
    }
}
