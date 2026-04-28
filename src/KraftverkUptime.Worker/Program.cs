using KraftverkUptime.Core.Modules;
using KraftverkUptime.Infrastructure;
using KraftverkUptime.Infrastructure.KeyVault;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "KRAFTVERK_");
builder.Configuration.AddKraftverkKeyVault(builder.Configuration);

builder.Services.AddKraftverkTelemetry(builder.Configuration);
builder.Services.AddKraftverkInfrastructure(builder.Configuration);
builder.Services.AddPlatformModules(
    new KraftverkUptime.Modules.Settlement.SettlementModule(),
    new KraftverkUptime.Modules.Classification.ClassificationModule(),
    new KraftverkUptime.Modules.Reporting.ReportingModule(),
    new KraftverkUptime.Modules.Annotations.AnnotationsModule(),
    new KraftverkUptime.Modules.Scada.ScadaModule());

builder.Services.AddKraftverkJobLoop();

// Minimal helse-endpoint for Worker – Container Apps bruker tcp/http-probe
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

var host = builder.Build();

// Kjør migreringer i dev også for worker-host (tester kontrakten mot samme skjema)
if (builder.Environment.IsDevelopment())
{
    await DatabaseBootstrapper.ApplyMigrationsAsync(host.Services);
}

await host.RunAsync();
