namespace KraftverkUptime.Modules.Reporting.Portefolje;

/// <summary>
/// Henter KPI-er på tvers av alle anlegg for en gitt periode. Brukes av
/// /portefolje-dashboardet (Steg 6 i veikartet) til å vise alle anlegg
/// side om side. Selve KPI-beregningen gjenbruker
/// <c>UptimeKpiCalculator</c> via per-plant uptime-rapporter.
///
/// Anlegg-uavhengig: tjenesten itererer over <c>core.plants</c> og inkluderer
/// alle anlegg som har en settlement-import som dekker perioden.
/// Anlegg uten data utelates fra responsen.
/// </summary>
public interface IPortfolioQueryService
{
    Task<PortfolioKpiResponse> GetKpisAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <summary>
/// Aggregert respons for /portefolje-endepunktet. Én rad per anlegg, hver med
/// et utvalg pre-formaterte KPI-er som UI'et viser direkte.
/// </summary>
public sealed record PortfolioKpiResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlantCount,
    IReadOnlyList<PortfolioPlantKpi> Plants);

/// <summary>
/// Én anleggs-rad i portefølje-tabellen. <see cref="Kpis"/> er en flat
/// dictionary av KPI-navn til verdi for å støtte vilkårlig KPI-katalog
/// uten å endre kontrakten når nye KPI-er legges til.
/// </summary>
public sealed record PortfolioPlantKpi(
    string PlantId,
    string PlantName,
    double InstalledCapacityMw,
    int HourCount,
    IReadOnlyDictionary<string, double?> Kpis);
