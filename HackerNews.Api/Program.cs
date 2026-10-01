using HackerNews.Api;
using HackerNews.Api.RateLimiting;
using HackerNews.Api.Stories;
using HackerNews.Api.Telemetry;
using Scalar.AspNetCore;
using Serilog;

const string ServiceName = "HackerNews.Api";
const string HealthPath = "/health";
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
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle(ServiceName));
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapHealthChecks(HealthPath);
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

public partial class Program { }
