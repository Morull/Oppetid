using KraftverkUptime.Core.DataSources;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Modules;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Parsing;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Modules.Settlement;

/// <summary>
/// DI-registrering for Settlement-modulen. Composition root legger til modulen via
/// <c>services.AddPlatformModules(new SettlementModule(), ...)</c>. Oppgradering av modulen
/// krever kun å bytte ut konkret type i denne metoden – én linje.
///
/// Regelen: ingen annen modul refererer ExcelSettlementParser direkte. Alle
/// bruker <see cref="ISettlementParser"/> via DI.
/// </summary>
public sealed class SettlementModule : IPlatformModule
{
    public string Name => "Settlement";
    public int SchemaVersion => 1;

    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Schema registry (singleton – er stateless).
        services.AddSingleton<ISettlementSchemaRegistry, SettlementSchemaRegistry>();

        // Parser – én konkret type registreres på tre kontrakter.
        services.AddScoped<ExcelSettlementParser>();
        services.AddScoped<ISettlementParser>(sp => sp.GetRequiredService<ExcelSettlementParser>());
        services.AddScoped<ISettlementDataSource>(sp => sp.GetRequiredService<ExcelSettlementParser>());

        // Data quality.
        services.AddScoped<DataQualityReportBuilder>();

        // Jobbkonsument.
        services.AddScoped<IJobHandler<ParseSettlementJob>, ParseSettlementJobHandler>();
    }
}
