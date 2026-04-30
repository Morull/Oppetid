namespace KraftverkUptime.Modules.Reporting.CaptureRate;

/// <summary>
/// Henter capture rate for et anlegg over en periode. Orchestrerer:
///   1. Settlement-rader fra <see cref="Storage.IUptimeReportStore"/>/period-provider
///   2. Daglig spot-snitt fra <c>core.market_prices</c>
///   3. Hele anleggets historiske dag-serie for persentil-filteret
///   4. Kall <see cref="CaptureRateCalculator.Compute"/>
/// </summary>
public interface ICaptureRateQueryService
{
    /// <summary>Hovedendepunkt: aggregert CR for perioden.</summary>
    Task<CaptureRateCalculator.CaptureRateResult> GetForPlantAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>Månedlig serie for graf på /capture-rate-side.</summary>
    Task<IReadOnlyList<MonthlyCaptureRate>> GetMonthlySeriesAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>Daglig serie for scatter/histogram på /capture-rate-side.</summary>
    Task<IReadOnlyList<DailyCaptureRate>> GetDailySeriesAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

public sealed record MonthlyCaptureRate(
    int Year,
    int Month,
    CaptureRateCalculator.CaptureRateResult Result);

public sealed record DailyCaptureRate(
    DateOnly Date,
    double MwhDay,
    double SpotDayAvgNokMwh,
    double OppnaaddNokMwh,
    double RaCr,
    bool ErFiltrert);
