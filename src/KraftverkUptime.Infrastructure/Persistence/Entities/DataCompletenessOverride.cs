namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.data_completeness_overrides</c>. Brukes når drifts-
/// leder manuelt har verifisert at en celle som ellers vises som PARTIAL/
/// OVERDUE faktisk er komplett — eks. SCADA alarmer hvor det ikke skjedde
/// noe, eller settlement hvor manglende timer skyldes legitim DST-overgang.
///
/// PK = (PlantId, SourceType, PeriodUtc). Én override per måned per (plant,
/// source). Sletting av rad fjerner overstyringen og cellen går tilbake til
/// automatisk beregnet status.
///
/// Effekten: matrise-bygg ser denne tabellen og forcer status = COMPLETE
/// for matchende celler, men beholder import-metadata (RowsImported,
/// FileName, Coverage etc.) i tooltip slik at brukeren ser både den
/// rå-beregnede dekningen og at den er manuelt overstyrt.
/// </summary>
public sealed class DataCompletenessOverride
{
    /// <summary>Plant-id som matcher <see cref="PlantRegistration.Id"/>.</summary>
    public string PlantId { get; set; } = string.Empty;

    /// <summary>"settlement", "scada", "operlog".</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Måneds-start UTC for cellen som overstyres (eks. 2026-04-01T00:00Z).
    /// Granularitet er måned siden cadence == "monthly" i v1.
    /// </summary>
    public DateTimeOffset PeriodUtc { get; set; }

    /// <summary>
    /// Begrunnelse fra drifts-leder. Frivillig, men sterkt anbefalt slik at
    /// fremtidige overlevering-er kan se hvorfor en celle er overstyrt.
    /// Eks: "Sjekket SCADA HMI manuelt 2026-05-04 — ingen alarmer i april,
    /// fredelig drift. Ingen kommunikasjons-feil."
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>UserId som satte override-en. "system" for automatiske; ellers Entra-id.</summary>
    public string OverriddenByUserId { get; set; } = "system";

    /// <summary>Når overstyringen ble registrert.</summary>
    public DateTimeOffset OverriddenAtUtc { get; set; }
}
