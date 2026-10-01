using HackerNews.Api.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HackerNews.Api.Controllers;

/// <summary>Liveness and readiness probes. Not rate limited, traced, or request-logged.</summary>
[ApiController]
[Route(BasePath)]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class HealthController(HealthCheckService healthChecks) : ControllerBase
{
    public const string BasePath = "/health";

    /// <summary>Liveness: <c>200</c> whenever the process can serve requests.</summary>
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Live() => Ok(new HealthResponse(nameof(HealthStatus.Healthy), []));

    /// <summary>Readiness: <c>200</c> only when the best stories are loaded and younger than the cache TTL.</summary>
    [HttpGet("ready")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthResponse>> Ready(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            check => check.Tags.Contains(BestStoriesReadinessCheck.ReadyTag),
            cancellationToken);

        var response = HealthResponse.From(report);
        return report.Status == HealthStatus.Unhealthy
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, response)
            : Ok(response);
    }
}

public sealed record HealthResponse(string Status, IReadOnlyList<HealthCheckResponse> Checks)
{
    public static HealthResponse From(HealthReport report) => new(
        report.Status.ToString(),
        report.Entries
            .Select(entry => new HealthCheckResponse(
                entry.Key,
                entry.Value.Status.ToString(),
                entry.Value.Description,
                entry.Value.Data))
            .ToArray());
}

public sealed record HealthCheckResponse(
    string Name,
    string Status,
    string? Description,
    IReadOnlyDictionary<string, object> Data);
