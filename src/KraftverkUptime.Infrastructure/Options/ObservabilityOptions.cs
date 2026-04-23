using System.ComponentModel.DataAnnotations;

namespace KraftverkUptime.Infrastructure.Options;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    [Required]
    public string ServiceName { get; set; } = "kraftverkuptime";

    public string? ApplicationInsightsConnectionString { get; set; }

    /// <summary>OTLP endpoint for eksport. Null = ingen OTLP-eksportør (kun console).</summary>
    public string? OtlpEndpoint { get; set; }

    public bool ExportToConsole { get; set; } = true;
}
