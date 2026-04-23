namespace KraftverkUptime.Modules.Settlement.Quality;

/// <summary>
/// Statistikk for én kolonne i den parsede tidsserien.
/// </summary>
public sealed record ColumnStatistics(
    string ColumnName,
    string DataType,
    int NullCount,
    int NonNullCount,
    double? Min,
    double? Max,
    double? Mean,
    double? Sum);

/// <summary>
/// Datakvalitetsrapport per import. Persisteres som blob via <c>IFileStorage</c>
/// med en lettvektig indeksrad i <c>core.import_quality_index</c> for å gi UI
/// rask oppslag uten å måtte laste blob.
///
/// Summen <c>HoursAccepted + HoursFlagged + HoursRejected</c> kan være lik eller større
/// enn <see cref="HoursReceived"/>: manglende timer fylles inn som
/// <c>InformationUnavailable</c> og teller mot <c>HoursFlagged</c>.
/// </summary>
public sealed record DataQualityReport
{
    public required string PlantName { get; init; }
    public required int HoursExpected { get; init; }
    public required int HoursReceived { get; init; }
    public required int HoursAccepted { get; init; }
    public required int HoursFlagged { get; init; }
    public required int HoursRejected { get; init; }
    public required IReadOnlyDictionary<string, ColumnStatistics> ColumnStats { get; init; }
    public required IReadOnlyList<ValidationIssue> Issues { get; init; }
}
