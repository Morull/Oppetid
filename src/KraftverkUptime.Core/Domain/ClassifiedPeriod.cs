namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Klassifisert tidsrom for én enhet. Én rad per sammenhengende state-endring.
/// <see cref="Confidence"/> er [0.0, 1.0] og reflekterer hvor sikker klassifiseringen er –
/// proxy-heuristikk fra settlement alene gir typisk 0.5–0.7, SCADA + CMMS gir 0.95+.
/// <see cref="Sources"/> lister hvilke kilder som bidro ("Settlement", "Scada", "Hydro", "Cmms").
/// Fused analyzer kan kombinere flere kilder og overstyre tidligere klassifisering
/// hvis den nye confidence er høyere.
/// </summary>
public sealed record ClassifiedPeriod(
    string AssetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    UnitState State,
    string CauseCode,
    double Confidence,
    IReadOnlyList<string> Sources,
    DataQualityState Quality)
{
    /// <summary>Varighet av perioden (ToUtc - FromUtc).</summary>
    public TimeSpan Duration => ToUtc - FromUtc;
}
