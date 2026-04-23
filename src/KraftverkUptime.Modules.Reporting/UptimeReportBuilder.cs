using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Reporting;

/// <summary>
/// Fasade-builder som konsumerer en forhåndsklassifisert <see cref="UptimeReport"/>
/// og returnerer den uendret (report-modellen er allerede format-agnostisk).
///
/// I senere faser kan builder'en legge til rapporttype-spesifikke beriking
/// (f.eks. fleet-benchmark-sammenligning, Pareto-analyse over topp-5
/// FO-hendelser) uten at rendereren trenger endres.
///
/// Forventer at caller har kjørt analyzeren først og gir UptimeReport-objektet
/// som <c>ReportRequest.Options["uptime.report"]</c>. Dette separerer Analyse
/// (idempotent, cachebar) fra Rapport-bygg (kan inkludere live data).
/// </summary>
public sealed class UptimeReportBuilder : IReportBuilder<UptimeReport>
{
    private readonly IAnalyzer<UptimePeriod, UptimeReport> _analyzer;
    private readonly IUptimePeriodProvider _periodProvider;

    public UptimeReportBuilder(
        IAnalyzer<UptimePeriod, UptimeReport> analyzer,
        IUptimePeriodProvider periodProvider)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _periodProvider = periodProvider ?? throw new ArgumentNullException(nameof(periodProvider));
    }

    public async Task<UptimeReport> BuildAsync(ReportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ReportKind, UptimeReportKinds.Monthly, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.ReportKind, UptimeReportKinds.Custom, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"UptimeReportBuilder støtter ikke ReportKind '{request.ReportKind}'. " +
                $"Støttede: '{UptimeReportKinds.Monthly}', '{UptimeReportKinds.Custom}'.");
        }

        var period = await _periodProvider.GetAsync(
            request.AssetId, request.FromUtc, request.ToUtc, ct).ConfigureAwait(false);
        return await _analyzer.AnalyzeAsync(period, ct).ConfigureAwait(false);
    }
}

/// <summary>Konstanter for <see cref="ReportRequest.ReportKind"/>.</summary>
public static class UptimeReportKinds
{
    /// <summary>Månedsrapport – standard kadence.</summary>
    public const string Monthly = "uptime.monthly";

    /// <summary>Tilpasset periode – f.eks. for ad-hoc-analyse.</summary>
    public const string Custom = "uptime.custom";
}

/// <summary>
/// Kobling mellom rapport-modul og det faktiske datagrunnlaget. Implementeres
/// i composition root (typisk Infrastructure eller en dedikert modul) og
/// slår opp parsed settlement + plantConfig for forespurt periode.
///
/// Denne abstraksjonen isolerer Reporting-modulen fra hvor dataene kommer
/// fra (DB, blob, live parse av opplastet fil).
/// </summary>
public interface IUptimePeriodProvider
{
    Task<UptimePeriod> GetAsync(
        string assetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}
