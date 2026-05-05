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
}
