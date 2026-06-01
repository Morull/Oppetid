namespace KraftverkUptime.Modules.Reporting.StartStopp;

/// <summary>
/// Bygger <see cref="StartStoppDto"/> for et anlegg over en rapport-periode.
/// Brukes av Rapport-detalj-endepunktet for å ledsage UptimeReport med
/// start/stopp-KPI-en. Spec NESTE-CHAT-START-STOPP-KPI.md (2026-05-22).
///
/// Innebærer tre tellinger:
/// <list type="bullet">
///   <item>Antall starter i periode [from, to).</item>
///   <item>Antall starter i tilsvarende periode like før — for trend-pil.</item>
///   <item>Antall starter ÅTD (1.jan til to) — for budsjett-progressbar.</item>
/// </list>
/// Implementasjonen kombinerer settlement-imports og blob-lagrede
/// UptimeReports for å plukke MwhElhub-time-serien.
/// </summary>
public interface IStartStoppQueryService
{
    /// <summary>
    /// Returnerer null hvis anlegget ikke finnes eller hvis det ikke er
    /// nok data til å beregne AntallStarter for hovedperioden. Resten av
    /// DTO-feltene er null hvis kildedata mangler — de skjuler tilsvarende
    /// UI-elementer.
    /// </summary>
    Task<StartStoppDto?> BuildAsync(
        string plantId,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken ct);
}
