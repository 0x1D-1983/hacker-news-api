using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HackerNews.Api.RateLimiting;
using HackerNews.Api.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HackerNews.Api.Controllers;

[ApiController]
[Route("api/stories")]
[Produces("application/json")]
[EnableRateLimiting(RateLimitingExtensions.PerClientPolicy)]
public sealed class StoriesController(BestStoriesCache bestStories) : ControllerBase
{
    private const int ClientCacheSeconds = 60;
    private const int WarmUpRetryAfterSeconds = 5;
    private const string NoStore = "no-store";

    /// <summary>Returns the best <paramref name="n"/> Hacker News stories in descending order of score.</summary>
    /// <param name="n">Number of stories to return, between 1 and 200.</param>
    [HttpGet("best")]
    [ResponseCache(Duration = ClientCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<IReadOnlyList<Story>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IReadOnlyList<Story>> GetBest(
        [FromQuery, Required, Range(1, BestStoriesCache.MaxStoryCount)] int? n)
    {
        if (bestStories.TryGetBestStories(n!.Value, out var stories))
            return Ok(stories);

        Response.Headers.CacheControl = NoStore;
        Response.Headers.RetryAfter = WarmUpRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Warming up",
            detail: "Best stories have not been loaded from Hacker News yet. Please retry shortly.");
    }
}
