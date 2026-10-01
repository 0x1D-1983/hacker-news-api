using System.ComponentModel.DataAnnotations;
using HackerNews.Api.RateLimiting;
using HackerNews.Api.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HackerNews.Api.Controllers;

[ApiController]
[Route("api/stories")]
[Produces("application/json")]
[EnableRateLimiting(RateLimitingExtensions.PerClientPolicy)]
public sealed class StoriesController(BestStoriesService bestStories) : ControllerBase
{
    private const int ClientCacheSeconds = 60;

    /// <summary>Returns the best <paramref name="n"/> Hacker News stories in descending order of score.</summary>
    /// <param name="n">Number of stories to return, between 1 and 200.</param>
    /// <param name="cancellationToken">Aborted when the client disconnects.</param>
    [HttpGet("best")]
    [ResponseCache(Duration = ClientCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<IReadOnlyList<Story>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<Story>>> GetBest(
        [FromQuery, Required, Range(1, BestStoriesService.MaxStoryCount)] int? n,
        CancellationToken cancellationToken)
    {
        var stories = await bestStories.GetBestStoriesAsync(n!.Value, cancellationToken);
        return Ok(stories);
    }
}
