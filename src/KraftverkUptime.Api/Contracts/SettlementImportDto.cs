namespace KraftverkUptime.Api.Contracts;

/// <summary>
/// API-kontrakt for en rad i liste-responsen fra
/// <c>GET /api/v1/plants/{plantId}/settlements</c>. Bevisst slank – kun felt
/// klienten trenger for å vise oversikt og navigere til detalj.
///
/// <c>ReportAvailable</c> sier om en <c>UptimeReport</c> er klar for oppslag via
/// <c>GET /.../settlements/{idempotencyKey}/report</c>. Hvis <c>false</c>, er
/// jobben antakelig fortsatt i kø eller klassifisering pågår.
/// </summary>
public sealed record SettlementImportDto(
    string PlantId,
    string IdempotencyKey,
    string PlantName,
    string SchemaVersion,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    int HourCount,
    int IssueCount,
    DateTimeOffset ImportedAtUtc,
    bool ReportAvailable);
