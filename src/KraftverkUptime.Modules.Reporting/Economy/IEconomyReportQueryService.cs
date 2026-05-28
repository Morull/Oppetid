namespace KraftverkUptime.Modules.Reporting.Economy;

/// <summary>
/// Aggregator-tjeneste som bygger <see cref="EconomyReportDto"/> ved å
/// kombinere tall fra eksisterende tjenester (Settlement, KAIA, Capture rate,
/// Vakt-ROI, Nedetid). Tjenesten kjører to passeringer — én for valgt
/// periode, én for forrige periode (avledet av
/// <see cref="PreviousPeriodCalculator"/>) — og setter sammen DTO-en med
/// trend-prosenter ferdig beregnet.
///
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22), del 4.
/// </summary>
public interface IEconomyReportQueryService
{
    /// <summary>
    /// Bygger økonomi-rapport for valgt utvalg anlegg og periode. Aggregering
    /// over flere anlegg følger reglene i spec del 3:
    /// <list type="bullet">
    ///   <item>NOK-beløp summeres på tvers.</item>
    ///   <item>Capture rate MWh-vektes (Σ realisert NOK / Σ MWh).</item>
    ///   <item>Vakt-kost-andel summeres som GWh-andel × samlet portefølje-vaktkost.</item>
    /// </list>
    /// </summary>
    /// <param name="plantIds">Anleggs-ID-er som rapporten dekker. Tomt sett → kaller bør validere og returnere 400.</param>
    /// <param name="fromUtc">Periode-start (UTC, inklusiv).</param>
    /// <param name="toUtc">Periode-slutt (UTC, eksklusiv).</param>
    /// <param name="kind">Periode-typen som bestemmer hvordan forrige periode beregnes.</param>
    /// <param name="ct">Avbrytelses-token.</param>
    Task<EconomyReportDto> GetAsync(
        IReadOnlyList<string> plantIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        PeriodKind kind,
        CancellationToken ct = default);
}
