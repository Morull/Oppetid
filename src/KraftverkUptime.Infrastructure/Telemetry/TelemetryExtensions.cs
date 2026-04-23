using Azure.Monitor.OpenTelemetry.Exporter;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace KraftverkUptime.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// OpenTelemetry med baggage for orgId/plantId/userId/correlationId. Bruker OTLP og/eller App Insights.
    /// Console exporter aktiveres når ExportToConsole = true (default i dev).
    /// </summary>
    public static IServiceCollection AddKraftverkTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ObservabilityOptions.SectionName);
        services.AddOptions<ObservabilityOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var opts = section.Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: opts.ServiceName, serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString() ?? "0.1.0");

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(opts.ServiceName))
            .WithTracing(b =>
            {
                b.AddAspNetCoreInstrumentation(o =>
                {
                    o.RecordException = true;
                });
                b.AddHttpClientInstrumentation();
                b.AddSource("KraftverkUptime");

                if (opts.ExportToConsole)
                {
                    b.AddConsoleExporter();
                }
                if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
                {
                    b.AddOtlpExporter(o => o.Endpoint = new Uri(opts.OtlpEndpoint));
                }
                if (!string.IsNullOrWhiteSpace(opts.ApplicationInsightsConnectionString))
                {
                    b.AddAzureMonitorTraceExporter(o => o.ConnectionString = opts.ApplicationInsightsConnectionString);
                }
            })
            .WithMetrics(b =>
            {
                b.AddAspNetCoreInstrumentation();
                b.AddHttpClientInstrumentation();
                b.AddRuntimeInstrumentation();
                b.AddMeter("KraftverkUptime");

                if (opts.ExportToConsole)
                {
                    b.AddConsoleExporter();
                }
                if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
                {
                    b.AddOtlpExporter(o => o.Endpoint = new Uri(opts.OtlpEndpoint));
                }
                if (!string.IsNullOrWhiteSpace(opts.ApplicationInsightsConnectionString))
                {
                    b.AddAzureMonitorMetricExporter(o => o.ConnectionString = opts.ApplicationInsightsConnectionString);
                }
            });

        // Konfigurer OpenTelemetry-logging separat via options-pattern.
        // I 1.12 er ILoggingBuilder.AddOpenTelemetry(Action<OpenTelemetryLoggerOptions>)
        // tvetydig — bruk parameterløs variant og konfigurer via IServiceCollection.Configure.
        services.Configure<OpenTelemetryLoggerOptions>(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.ParseStateValues = true;

            if (opts.ExportToConsole)
            {
                o.AddConsoleExporter();
            }
            if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
            {
                o.AddOtlpExporter(e => e.Endpoint = new Uri(opts.OtlpEndpoint));
            }
            if (!string.IsNullOrWhiteSpace(opts.ApplicationInsightsConnectionString))
            {
                o.AddAzureMonitorLogExporter(e => e.ConnectionString = opts.ApplicationInsightsConnectionString);
            }
        });

        services.AddLogging(b => b.AddOpenTelemetry());

        return services;
    }
}
