using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HackerNews.Api.HackerNews;
using HackerNews.Api.Stories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.Api.Tests;

public sealed class BestStoriesApiTests
{
    private const string BestStoriesPath = "/v0/beststories.json";

    [Fact]
    public async Task Returns_requested_number_of_stories_in_descending_score_order()
    {
        var handler = new FakeHackerNewsHandler()
            .WithBestStories(1, 2, 3)
            .WithItem(1, Item(1, score: 50))
            .WithItem(2, Item(2, score: 300))
            .WithItem(3, Item(3, score: 120));
        await using var factory = CreateFactory(handler);

        var stories = await factory.CreateClient().GetFromJsonAsync<Story[]>("/api/stories/best?n=2");

        Assert.NotNull(stories);
        Assert.Equal([300, 120], stories.Select(story => story.Score));
    }

    [Fact]
    public async Task Maps_hacker_news_fields_to_the_documented_shape()
    {
        var handler = new FakeHackerNewsHandler()
            .WithBestStories(21233041)
            .WithItem(21233041, """
                {"by":"ismaildonmez","descendants":572,"id":21233041,"score":1716,"time":1570887781,
                 "title":"A uBlock Origin update was rejected from the Chrome Web Store","type":"story",
                 "url":"https://github.com/uBlockOrigin/uBlock-issues/issues/745"}
                """);
        await using var factory = CreateFactory(handler);

        var json = await factory.CreateClient().GetStringAsync("/api/stories/best?n=1");

        var story = JsonDocument.Parse(json).RootElement[0];
        Assert.Equal("A uBlock Origin update was rejected from the Chrome Web Store", story.GetProperty("title").GetString());
        Assert.Equal("https://github.com/uBlockOrigin/uBlock-issues/issues/745", story.GetProperty("uri").GetString());
        Assert.Equal("ismaildonmez", story.GetProperty("postedBy").GetString());
        Assert.Equal("2019-10-12T13:43:01+00:00", story.GetProperty("time").GetString());
        Assert.Equal(1716, story.GetProperty("score").GetInt32());
        Assert.Equal(572, story.GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task Skips_missing_deleted_and_dead_items()
    {
        var handler = new FakeHackerNewsHandler()
            .WithBestStories(1, 2, 3, 4)
            .WithItem(1, Item(1, score: 10))
            .WithItem(2, """{"id":2,"deleted":true}""")
            .WithItem(3, """{"id":3,"dead":true,"score":99}""");
        await using var factory = CreateFactory(handler);

        var stories = await factory.CreateClient().GetFromJsonAsync<Story[]>("/api/stories/best?n=10");

        Assert.NotNull(stories);
        Assert.Equal([10], stories.Select(story => story.Score));
    }

    [Fact]
    public async Task Serves_repeat_requests_from_cache_without_calling_hacker_news_again()
    {
        var handler = new FakeHackerNewsHandler()
            .WithBestStories(1, 2)
            .WithItem(1, Item(1, score: 1))
            .WithItem(2, Item(2, score: 2));
        await using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(n => client.GetAsync($"/api/stories/best?n={n % 2 + 1}")));

        Assert.Equal(1, handler.Hits(BestStoriesPath));
        Assert.Equal(1, handler.Hits("/v0/item/1.json"));
    }

    [Theory]
    [InlineData("/api/stories/best")]
    [InlineData("/api/stories/best?n=0")]
    [InlineData("/api/stories/best?n=201")]
    [InlineData("/api/stories/best?n=abc")]
    public async Task Rejects_invalid_n(string url)
    {
        await using var factory = CreateFactory(new FakeHackerNewsHandler().WithBestStories());

        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_503_when_hacker_news_returns_an_unreadable_response()
    {
        var handler = new FakeHackerNewsHandler().With(BestStoriesPath, "not json");
        await using var factory = CreateFactory(handler);

        var response = await factory.CreateClient().GetAsync("/api/stories/best?n=1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Throttles_clients_that_exceed_the_rate_limit()
    {
        await using var factory = CreateFactory(
            new FakeHackerNewsHandler().WithBestStories(),
            ("RateLimiting:TokenLimit", "2"));
        var client = factory.CreateClient();

        await client.GetAsync("/api/stories/best?n=1");
        await client.GetAsync("/api/stories/best?n=1");
        var throttled = await client.GetAsync("/api/stories/best?n=1");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        FakeHackerNewsHandler handler,
        params (string Key, string Value)[] settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Telemetry:Enabled", "false");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            builder.ConfigureTestServices(services => services
                .AddHttpClient<HackerNewsClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler));
        });

    private static string Item(long id, int score)
        => $$"""{"id":{{id}},"type":"story","by":"user{{id}}","time":1570887781,"title":"Story {{id}}","url":"https://example.com/{{id}}","score":{{score}},"descendants":0}""";
}
