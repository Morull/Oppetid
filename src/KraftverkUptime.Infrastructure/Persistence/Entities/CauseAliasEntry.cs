namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.cause_aliases</c>. Lar drifts-leder gi en intern
/// cause-kode (eks. <c>operlog:nodstopp</c>) en brukervennlig visnings-tekst
/// (eks. "Nødstopp utløst"). Når en hendelse rendres i UI (Nedetid, Rapport,
/// Vakt-ROI) slår vi opp i denne tabellen via <c>CauseFormatter</c>; fall-back
/// til den interne koden hvis ingen alias finnes.
///
/// Per-org slik at to organisasjoner kan ha forskjellige formuleringer for
/// samme kode. Idempotente system-defaults seedes ved oppstart med
/// <c>OwnerOrgId = "dev-org"</c>.
/// </summary>
public sealed class CauseAliasEntry
{
    /// <summary>Intern cause-kode (PK), eks. "operlog:nodstopp".</summary>
    public string CauseCode { get; set; } = string.Empty;

    public string OwnerOrgId { get; set; } = string.Empty;

    /// <summary>Brukervennlig tekst, eks. "Nødstopp utløst".</summary>
    public string DisplayText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
