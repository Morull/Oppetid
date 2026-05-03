using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Reporting.DataQuality;

/// <summary>
/// Eksponerer datakvalitets-tilstand fra settlement-importen til Web-laget.
/// SPEC-MVP-HARDENING tiltak C: <c>DqState</c> finnes i datalaget men er
/// usynlig i UI. Service-en gir aggregert oversikt per anlegg per periode.
///
/// Brukes av:
///   - Portefølje-side (kolonne "Datakvalitet %")
///   - Anleggs-side (detaljert breakdown-kort)
///   - Nedetid/Produksjon (filter-toggle "Skjul timer med dårlig kvalitet")
/// </summary>
public interface IDataQualityQueryService
{
    /// <summary>
    /// Henter datakvalitets-summary for et anlegg i et periodevindu.
    /// Returnerer null hvis det ikke finnes import-data for perioden — caller
    /// bør tolke det som "ikke vurdert" snarere enn "100 % god".
    /// </summary>
    Task<DataQualitySummary?> GetSummaryAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>
    /// Bulk-versjon: henter summary for alle anlegg i porteføljen i én operasjon
    /// (bruks av portefølje-kortet for å unngå N+1).
    /// </summary>
    Task<IReadOnlyList<DataQualitySummary>> GetSummariesForAllPlantsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

/// <summary>
/// Aggregert kvalitets-status for et anlegg i en periode. Tallene summerer
/// til <see cref="TotalHours"/>: ManglerImport + Good + Warning + Bad +
/// Missing = TotalHours.
///
/// Begrepsmessig:
///   - <b>Good</b> — verdi til stede og innen forventet område
///   - <b>Warning</b> — verdi til stede, men flagget av builder (Uncertain/Substituted)
///   - <b>Bad</b> — verdi avvist eller karantenisert (Quarantined/Rejected)
///   - <b>Missing</b> — manglende time i settlement-input (InformationUnavailable)
///   - <b>ManglerImport</b> — det finnes ingen settlement-import som dekker den timen
/// </summary>
public sealed record DataQualitySummary(
    string PlantId,
    string PlantName,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalHours,
    int GoodHours,
    int WarningHours,
    int BadHours,
    int MissingHours,
    int ManglerImportHours,
    double GoodPct,                                  // GoodHours / TotalHours
    double DekningPct,                               // (Total − ManglerImport) / Total
    IReadOnlyList<DataQualityIssue> TopIssues);

/// <summary>
/// Et enkelt-issue (én time som ikke er Good). Brukes til å vise bruker en
/// liste med "her er hva som mangler / er rart" på anleggs-siden.
/// </summary>
public sealed record DataQualityIssue(
    DateTimeOffset TimeUtc,
    DataQualityState State,
    string Reason);
