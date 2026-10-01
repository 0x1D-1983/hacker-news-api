using System.ComponentModel.DataAnnotations;

namespace HackerNews.Api.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; set; } = true;

    /// <summary>OTLP gRPC endpoint (Tempo, Jaeger, Aspire dashboard, or any OTLP collector).</summary>
    [Required]
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
}
