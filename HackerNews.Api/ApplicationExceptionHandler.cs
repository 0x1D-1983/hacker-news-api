using HackerNews.Api.HackerNews;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HackerNews.Api;

internal sealed class ApplicationExceptionHandler(ILogger<ApplicationExceptionHandler> logger) : IExceptionHandler
{
    private const string GenericErrorDetail = "An error occurred while processing your request.";
    private const string UpstreamErrorDetail = "Hacker News is currently unavailable. Please retry later.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
            return false;

        var (status, title, detail) = exception switch
        {
            HackerNewsUnavailableException
                => (StatusCodes.Status503ServiceUnavailable, "Upstream unavailable", UpstreamErrorDetail),
            _ => (StatusCodes.Status500InternalServerError, "Server error", GenericErrorDetail)
        };

        logger.LogError(exception, "{Title}: {Detail}", title, exception.Message);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        }, cancellationToken);

        return true;
    }
}
