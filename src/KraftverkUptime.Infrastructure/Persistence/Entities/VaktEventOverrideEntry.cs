using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.vakt_event_overrides</c>. Lar drifts-leder manuelt
/// overstyre om en vakt-hendelse skal regnes som "hadde overløp" eller
/// "hadde ikke overløp", uavhengig av hva SCADA-overflow-tagene viste.
///
/// Bakgrunn (2026-05-05): SCADA-overflow-sensoren kan ha kortvarige glitches
/// som gir feilaktige "reddete timer" i Vakt-ROI. Drifts-leder må kunne
/// rette opp uten å manipulere rå sensor-data.
///
/// Identifikasjon: (PlantId, EventStartUtc) — én rad per vakt-hendelse.
/// Ved redigering av annoteringer som flytter event-grensene blir override
/// automatisk forkastet (ny EventStartUtc → ingen override-match).
/// </summary>
public sealed class VaktEventOverrideEntry
{
    public string PlantId { get; set; } = string.Empty;
    public DateTimeOffset EventStartUtc { get; set; }

    /// <summary>
    /// Klassifisering: "Auto" (default — ikke override), "HaddeOverlop" (tving
    /// overflow-komponent på), "IkkeOverlop" (sett produksjons-komponent til 0).
    /// Lagres som streng for migrasjons-vennlighet.
    /// </summary>
    public string Classification { get; set; } = "Auto";

    public string? Comment { get; set; }
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? SetBy { get; set; }
    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Manuell overstyring av når hendelsen FAKTISK var over. Brukes når SCADA-/
    /// operlog-EndUtc er feil (sensor-glitch eller forsinket alarm-clear) og
    /// drifts-leder vet det riktige slutttidspunktet. Anvendes oppstrøms for
    /// <c>VaktRoiCalculator</c>: <c>DowntimeEvent.EndUtc</c> byttes ut før ROI-
    /// beregningen, så <c>VaktRoiCalculator</c> selv forblir en ren funksjon av
    /// events. Null = ingen varighetsoverstyring (default).
    ///
    /// Valideres til å være etter <see cref="EventStartUtc"/>.
    /// </summary>
    public DateTimeOffset? ActualEndOverrideUtc { get; set; }

    /// <summary>
    /// Manuell overstyring av om vakta rykket ut for denne hendelsen.
    /// Spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md (2026-05-22): U2-PlanDeviation-
    /// hendelser uten operlog-match teller IKKE som vakt-utrykning i Auto-modus —
    /// drifts-leder kan overstyre per hendelse. Default <see cref="GuardResponseOverride.Auto"/>
    /// (= EffectiveGuardResponse-logikken bestemmer). Lagres som smallint, 0=Auto, 1=Yes, 2=No.
    /// </summary>
    public GuardResponseOverride GuardResponseOverride { get; set; } = GuardResponseOverride.Auto;
}
