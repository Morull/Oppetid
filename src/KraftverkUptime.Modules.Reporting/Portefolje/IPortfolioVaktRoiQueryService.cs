namespace KraftverkUptime.Modules.Reporting.Portefolje;

/// <summary>
/// Aggregert Vakt-ROI på tvers av alle anlegg for én periode. Brukes av
/// portefølje-dashboardet på <c>/vakt-roi</c> (uten plantId).
///
/// Strategi: itererer over <c>core.plants</c>, kjører
/// <see cref="Nedetid.VaktRoiCalculator"/> per anlegg, og samler resultatene.
/// Caching: implementasjonen kan cache per-anleggs-resultater siden Vakt-ROI
/// er deterministisk for en gitt periode.
/// </summary>
public interface IPortfolioVaktRoiQueryService
{
    Task<PortfolioVaktRoiResponse> GetAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int topN,
        CancellationToken ct);
}

/// <summary>
/// Aggregert respons med totalsum, top-N hendelser og månedlig trend.
/// </summary>
public sealed record PortfolioVaktRoiResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlantCount,
    int PlantsWithData,
    double TotalReddetNok,
    int TotalReddbareEvents,
    int TotalEvents,
    IReadOnlyList<PortfolioVaktRoiPlantSummary> PerPlant,
    IReadOnlyList<PortfolioVaktRoiTopEvent> TopEvents,
    IReadOnlyList<PortfolioVaktRoiMonthlyPoint> MonthlyTrend);

/// <summary>Per-anleggs-rad: total reddet og antall events i perioden.</summary>
public sealed record PortfolioVaktRoiPlantSummary(
    string PlantId,
    string PlantName,
    double InstalledCapacityMw,
    double ReddetNok,
    int ReddbareEvents,
    int TotaleEvents);

/// <summary>Én topp-N event på tvers av porteføljen.</summary>
public sealed record PortfolioVaktRoiTopEvent(
    string PlantId,
    string PlantName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double VarighetTimer,
    string Kategori,
    string? CauseCode,
    double ReddetNok,
    double EkstraTimerSpart);

/// <summary>Måned-bucket for trend-graf.</summary>
public sealed record PortfolioVaktRoiMonthlyPoint(
    int Year,
    int Month,
    double TotalReddetNok,
    int ReddbareEvents);
