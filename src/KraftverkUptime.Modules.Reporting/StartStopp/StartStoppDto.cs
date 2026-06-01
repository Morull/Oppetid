namespace KraftverkUptime.Modules.Reporting.StartStopp;

/// <summary>
/// Start/stopp-syklus-KPI for ett anlegg og én rapport-periode. Returneres
/// sammen med Uptime-rapporten på Rapport-detalj-endepunktet. Spec
/// NESTE-CHAT-START-STOPP-KPI.md (2026-05-22).
///
/// Felt-betydning:
/// <list type="bullet">
///   <item><b>AntallStarter</b>: cold-starts i rapport-perioden. Alltid satt.</item>
///   <item><b>AntallStarterForrige</b>: tilsvarende periode-tilbake (forrige
///     måned/kvartal/år). Null hvis ingen sammenligningsperiode finnes.</item>
///   <item><b>EndringProsent</b>: relativ endring (1.0 = +100 %). Null hvis
///     forrige = 0 eller mangler.</item>
///   <item><b>BudsjettPerAar</b>: OEM-anbefaling. Null = ikke satt på anlegg.</item>
///   <item><b>BudsjettBruktAtd</b>: antall starter 1.jan-til-periode-slutt.
///     Null hvis BudsjettPerAar mangler eller settlement-data ikke dekker
///     hele ÅTD-spennet.</item>
///   <item><b>BudsjettBruktProsent</b>: 0-100, brukt til progressbar-farge.</item>
///   <item><b>KostnadPerSyklus</b>: NOK per syklus, fra anleggets admin.</item>
///   <item><b>KostnadTotalNok</b>: AntallStarter × KostnadPerSyklus. Null
///     hvis KostnadPerSyklus mangler.</item>
///   <item><b>Kilde</b>: fri-tekst kildehenvisning fra admin (vises i
///     info-popover hvis satt).</item>
/// </list>
/// </summary>
public sealed record StartStoppDto(
    int AntallStarter,
    int? AntallStarterForrige,
    double? EndringProsent,
    int? BudsjettPerAar,
    int? BudsjettBruktAtd,
    double? BudsjettBruktProsent,
    double? KostnadPerSyklus,
    double? KostnadTotalNok,
    string? Kilde);
