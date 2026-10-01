using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.RateLimiting;

public static class RateLimitingExtensions
{
    public const string PerClientPolicy = "per-client";

    private const string UnknownClient = "unknown";

    public static IServiceCollection AddClientRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClientRateLimitOptions>()
            .Bind(configuration.GetSection(ClientRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;

            limiter.AddPolicy(PerClientPolicy, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<ClientRateLimitOptions>>().Value;
                var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;

                return RateLimitPartition.GetTokenBucketLimiter(clientKey, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.TokenLimit,
                    TokensPerPeriod = options.TokensPerPeriod,
                    ReplenishmentPeriod = options.ReplenishmentPeriod,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingExtensions))
            .LogWarning(
                "Rate limit exceeded for {ClientAddress} on {Path}",
                httpContext.Connection.RemoteIpAddress,
                httpContext.Request.Path);

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Rate limit exceeded. Retry after the period in the Retry-After header."
        }, cancellationToken);
    }
}
