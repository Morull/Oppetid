namespace KraftverkUptime.Modules.Settlement.Quality;

/// <summary>
/// Alvorlighetsgrad for datakvalitetsavvik. Samsvarer med Python-PoC-en
/// og kan mappes én-til-én i rapportene.
/// </summary>
public enum IssueSeverity
{
    /// <summary>Informasjon – ingen handling kreves, men nevnes i rapport.</summary>
    Info,

    /// <summary>Advarsel – data er brukbar, men med redusert tillit.</summary>
    Warning,

    /// <summary>Feil – rader er kastet eller kan ikke behandles videre.</summary>
    Error
}

/// <summary>
/// Ett avvik funnet under parsing eller kvalitetskontroll.
///
/// Konvensjon for <see cref="Code"/>: UPPERSNAKE med prefiks som forteller hvilket
/// parsedomene avviket stammer fra (<c>SUMMARY_*</c>, <c>HOURLY_*</c>, <c>SCHEMA_*</c>,
/// <c>DST_*</c>). Dette gir stabile koder for alarmering og analyse.
/// </summary>
public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Code,
    string Message,
    int? AffectedRows = null);
