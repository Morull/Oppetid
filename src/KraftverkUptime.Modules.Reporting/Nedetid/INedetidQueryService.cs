using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Felles spørrings-tjeneste for nedetids-events. Brukes av både
/// <c>/api/v1/plants/{plantId}/nedetid</c> og
/// <c>/api/v1/plants/{plantId}/vakt-roi</c> slik at UI-en og Vakt-ROI-modellen
/// jobber mot identisk event-grunnlag.
///
/// Kontrakten er bevisst data-tjeneste-orientert (ikke "byggrapport"):
/// returnerer en flat liste events for perioden + plant. Caller kan
/// aggregere/projisere som de vil.
/// </summary>
public interface INedetidQueryService
{
    /// <summary>
    /// Henter alle nedetids-events for et anlegg som overlapper med
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).
    ///
    /// Implementasjonen henter klassifiserte timer fra alle settlement-imports
    /// som overlapper perioden, slår sammen påfølgende nedetidstimer til events,
    /// og beriker med operlog-events der det finnes overlapp i tid.
    /// </summary>
    Task<IReadOnlyList<DowntimeEvent>> ListEventsAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>
    /// Beregner SIGNERT forventet ubalanse-merkost for perioden:
    /// <c>avg(RkPris − Spotpris)</c> over ALLE timer med gyldig prisgrunnlag
    /// (énprismodell siden nov. 2021). Brukes av Vakt-ROI til å verdsette
    /// ubalanse-gebyret vakten redder. Verdien kan være NEGATIV når ubalanse i
    /// snitt var billigere enn spot (typisk i NO2).
    ///
    /// Tidligere telte denne kun timer der RK &gt; spot (toprislogikk), noe som
    /// ga et systematisk overestimat — se FAGVURDERING-KPI-BEREGNINGER #1 /
    /// SPEC-UBALANSE-ENPRIS-FIX.
    ///
    /// Returnerer 0 hvis ingen timer har gyldig pris-data.
    /// </summary>
    Task<double> GetAvgImbalancePremiumAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>
    /// Henter SIGNERT ubalansepremie <c>(RkPris − Spotpris)</c> i NOK/MWh per
    /// UTC-time (gulv-kvantisert til hel time). Vinduet utvides 3 dager forbi
    /// <paramref name="toUtc"/> — samme counterfactual-buffer som plan- og
    /// overløps-tjenestene — slik at hendelser sent i perioden får faktiske
    /// per-time-premier for hele counterfactual-vinduet når data finnes.
    /// Brukes av Vakt-ROI (Variant 2, SPEC-VAKT-ROI-UBALANSE-FULLPERIODE-OG-
    /// VISNING): et periodesnitt er strukturelt negativt i NO2 og maskerer at
    /// knapphetstimer (RK ≫ spot) gir sterkt positiv ubalanse-redning.
    /// Timer uten gyldig pris-data utelates (kalkulatoren faller tilbake på
    /// periodesnittet for slike). Ved overlappende importer vinner nyeste.
    /// </summary>
    Task<IReadOnlyDictionary<DateTimeOffset, double>> GetImbalancePremiumByHourAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>
    /// Henter Hydrogrid-plan (<c>ProduksjonplanMwh</c>) per UTC-time fra alle
    /// settlement-imports som overlapper en utvidet variant av [from, to). For
    /// timer der settlement-data mangler innenfor counterfactual-vinduet, fyller
    /// tjenesten inn proxy-verdier fra nærmeste samme ukedag/time bakover i tid
    /// (maks 4 uker tilbake) og rapporterer proxy-timene i <c>ProxyHours</c>.
    /// Brukes av Vakt-ROI til å verdsette reddet produksjon basert på faktisk
    /// plan istedenfor en flat <c>installert × kapasitetsfaktor</c>.
    /// </summary>
    Task<PlanByHourResult> GetProduksjonplanByHourAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

/// <summary>
/// Resultat fra <see cref="INedetidQueryService.GetProduksjonplanByHourAsync"/>.
/// <see cref="PlanByHour"/> dekker <c>[fromUtc, toUtc + counterfactual-buffer)</c>
/// inkludert proxy-utfylte timer; <see cref="ProxyHours"/> markerer hvilke som
/// er proxy slik at <see cref="VaktRoiResultat.PlanDataPartial"/> kan settes.
/// </summary>
public sealed record PlanByHourResult(
    IReadOnlyDictionary<DateTimeOffset, double> PlanByHour,
    IReadOnlySet<DateTimeOffset> ProxyHours);
