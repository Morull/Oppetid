using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Classification.Kpi;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Classification.Analyzers;

/// <summary>
/// Nivå 0-analyzer: konsumerer settlement alene og produserer klassifiserte
/// timerader + KPI-rapport. Tilsvarer Prompt 2 sin <c>UptimeAnalyzer.Settlement</c>.
///
/// Analyzeren er stateless og kan kjøres uten transaksjon mot DB. Persistering
/// av resultatene er caller's ansvar (typisk skjer det i en <c>IJobHandler</c>
/// som konsumerer <c>SettlementImportedEvent</c>).
///
/// Kontrakten er <c>IAnalyzer&lt;UptimePeriod, UptimeReport&gt;</c> fra Core slik at
/// senere implementasjoner (SCADA+Fused, hydro-beriket) kan swappes in med
/// samme signatur.
/// </summary>
public sealed class SettlementUptimeAnalyzer : IAnalyzer<UptimePeriod, UptimeReport>
{
    private readonly SettlementClassifier _classifier;
    private readonly UptimeKpiCalculator _kpiCalculator;
    private readonly DataQualityReportBuilder _qualityBuilder;
    private readonly ILogger<SettlementUptimeAnalyzer> _logger;

    public SettlementUptimeAnalyzer(
        SettlementClassifier classifier,
        UptimeKpiCalculator kpiCalculator,
        DataQualityReportBuilder qualityBuilder,
        ILogger<SettlementUptimeAnalyzer> logger)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _kpiCalculator = kpiCalculator ?? throw new ArgumentNullException(nameof(kpiCalculator));
        _qualityBuilder = qualityBuilder ?? throw new ArgumentNullException(nameof(qualityBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<UptimeReport> AnalyzeAsync(UptimePeriod input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        _logger.LogInformation(
            "Starter uptime-analyse for {Plant}, {Hours} timer",
            input.PlantConfig.PlantId, input.Settlement.Hourly.Count);

        // Beriker rådata med DqState før klassifisering – manglende timer blir
        // InformationUnavailable som klassifiseren behandler eksplisitt.
        var (_, enrichedHourly) = _qualityBuilder.Build(input.Settlement);

        var classified = _classifier.Classify(enrichedHourly, input.PlantConfig);
        var report = _kpiCalculator.Compute(classified, input.PlantConfig);

        _logger.LogInformation(
            "Analyse fullført: SH={SH}, AH={AH}, UH={UH}, FO={FO}, total={Mwh:F2} MWh",
            report.Kpis.FirstOrDefault(k => k.Name == "ServiceHours_SH")?.Value,
            report.Kpis.FirstOrDefault(k => k.Name == "AvailableHours_AH")?.Value,
            report.Kpis.FirstOrDefault(k => k.Name == "UnavailableHours_UH")?.Value,
            report.Kpis.FirstOrDefault(k => k.Name == "ForcedOutageHours_FOH")?.Value,
            report.Kpis.FirstOrDefault(k => k.Name == "TotalProduction_MWh")?.Value);

        return Task.FromResult(report);
    }
}
