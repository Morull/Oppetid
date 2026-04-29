using KraftverkUptime.Core.Modules;
using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Modules.Reporting;

/// <summary>
/// DI-registrering for rapportmodulen. Registrerer både builder og renderer
/// bak sine abstrakte kontrakter.
///
/// <see cref="IUptimePeriodProvider"/> må registreres separat i composition
/// root (typisk i Infrastructure eller Api) fordi den henter data fra
/// DB/blob – det er ikke Reporting-modulens ansvar.
/// </summary>
public sealed class ReportingModule : IPlatformModule
{
    public string Name => "Reporting";
    public int SchemaVersion => 1;

    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IReportBuilder<UptimeReport>, UptimeReportBuilder>();
        services.AddScoped<IReportRenderer, UptimeReportRenderer>();

        // Nedetids-analyse + Vakt-ROI (priortet 1 i 2026-04-28-overleveringen).
        services.AddScoped<INedetidQueryService, NedetidQueryService>();
        services.AddScoped<IOverflowQueryService, OverflowQueryService>();
        services.AddScoped<VaktRoiCalculator>();
    }
}
