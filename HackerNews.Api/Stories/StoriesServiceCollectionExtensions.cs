using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

public static class StoriesServiceCollectionExtensions
{
    public static IServiceCollection AddBestStories(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration.GetSection(HackerNewsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHybridCache();

        services.AddHttpClient<HackerNewsClient>((serviceProvider, httpClient) =>
            {
                httpClient.BaseAddress = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value.BaseAddress;
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<BestStoriesService>();

        return services;
    }
}
