using HackerNews.Api.HackerNews;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

public static class StoriesServiceCollectionExtensions
{
    public static IServiceCollection AddBestStories(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration.GetSection(HackerNewsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.RefreshInterval < options.CacheDuration,
                $"{HackerNewsOptions.SectionName}:{nameof(HackerNewsOptions.RefreshInterval)} must be shorter than " +
                $"{HackerNewsOptions.SectionName}:{nameof(HackerNewsOptions.CacheDuration)}.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<HackerNewsClient>((serviceProvider, httpClient) =>
            {
                httpClient.BaseAddress = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value.BaseAddress;
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<BestStoriesLoader>();
        services.AddSingleton<BestStoriesCache>();
        services.AddSingleton<BestStoriesRefreshWorker>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BestStoriesRefreshWorker>());

        services.AddHealthChecks()
            .AddCheck<BestStoriesReadinessCheck>(BestStoriesReadinessCheck.Name, tags: [BestStoriesReadinessCheck.ReadyTag]);

        return services;
    }
}
