using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Modules;
using KraftverkUptime.Modules.Classification.Analyzers;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Classification.Kpi;
using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Modules.Classification;

/// <summary>
/// DI-registrering for klassifiserings- og KPI-modulen. Dekker både
/// <see cref="SettlementUptimeAnalyzer"/> (Nivå 0) og
/// <see cref="FusedUptimeAnalyzer"/> (Nivå 3+, stub).
///
/// Oppgradering til fused analyzer er én linje i
/// <see cref="RegisterServices"/>: bytt ut <c>SettlementUptimeAnalyzer</c>
/// med <c>FusedUptimeAnalyzer</c>. Consumers refererer
/// <c>IAnalyzer&lt;UptimePeriod, UptimeReport&gt;</c> og ser ikke byttet.
/// </summary>
public sealed class ClassificationModule : IPlatformModule
{
    public string Name => "Classification";
    public int SchemaVersion => 1;

    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SettlementClassifier>();
        services.AddSingleton<UptimeKpiCalculator>();

        // Nivå 0-analyzer. Ved oppgradering til Nivå 3 byttes denne linjen:
        //   services.AddScoped<IAnalyzer<UptimePeriod, UptimeReport>, FusedUptimeAnalyzer>();
        services.AddScoped<IAnalyzer<UptimePeriod, UptimeReport>, SettlementUptimeAnalyzer>();

        // Fused-analyzeren registreres også som egen type slik at senere moduler kan
        // ta avhengighet på den uten å bytte DI-bindingen over.
        services.AddScoped<FusedUptimeAnalyzer>();
    }
}
