using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HackerNews.Api.Telemetry;

public static class TelemetryExtensions
{
    private const string HealthPath = "/health";

    private const string RateLimitingMeter = "Microsoft.AspNetCore.RateLimiting";

    public static IServiceCollection AddHackerNewsTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        services.AddSingleton<HackerNewsMetrics>();
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        if (!options.Enabled)
            return services;

        var otlpEndpoint = new Uri(options.OtlpEndpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing => tracing
                .AddSource(HackerNewsMetrics.SourceName)
                .AddAspNetCoreInstrumentation(asp =>
                {
                    asp.RecordException = true;
                    asp.Filter = context => !context.Request.Path.StartsWithSegments(HealthPath);
                })
                .AddHttpClientInstrumentation(http => http.RecordException = true)
                .AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(HackerNewsMetrics.SourceName, RateLimitingMeter)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpoint));

        return services;
    }
}
