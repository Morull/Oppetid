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
    /// Beregner snitt-ubalansetillegg for perioden — gjennomsnittlig RK-pris
    /// minus spotpris over alle timer der RK var dyrere enn spot. Brukes av
    /// Vakt-ROI v3 til å verdsette ubalanse-gebyret som vakten redder.
    ///
    /// Returnerer 0 hvis ingen timer har gyldig (RkPris &gt; Spotpris)-data —
    /// konservativt anslag som unngår å lage tall ut av ingenting.
    /// </summary>
    Task<double> GetAvgImbalancePremiumAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}
