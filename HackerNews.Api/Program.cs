using HackerNews.Api;
using HackerNews.Api.RateLimiting;
using HackerNews.Api.Stories;
using HackerNews.Api.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

const string ServiceName = "HackerNews.Api";
const string LivenessPath = "/health";
const string ReadinessPath = "/health/ready";
var requestTimeout = TimeSpan.FromSeconds(30);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));
builder.Services.AddHackerNewsTelemetry(builder.Configuration, ServiceName);

builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddClientRateLimiting(builder.Configuration);
builder.Services.AddRequestTimeouts(timeouts => timeouts.DefaultPolicy = new() { Timeout = requestTimeout });
builder.Services.AddHealthChecks();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddBestStories(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging(options => options.GetLevel = GetRequestLogLevel);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle(ServiceName));
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = _ => false })
   .DisableRateLimiting();

app.MapHealthChecks(ReadinessPath, new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(BestStoriesReadinessCheck.ReadyTag),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
}).DisableRateLimiting();

app.MapControllers();

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{ServiceName} terminated unexpectedly", ServiceName);
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Health probes run every few seconds; logging them would drown out real traffic.
static LogEventLevel GetRequestLogLevel(HttpContext context, double elapsedMs, Exception? exception)
{
    if (context.Request.Path.StartsWithSegments(LivenessPath))
        return LogEventLevel.Verbose;

    return exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
        ? LogEventLevel.Error
        : LogEventLevel.Information;
}

public partial class Program { }
