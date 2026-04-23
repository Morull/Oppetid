namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Normalisert måling eller hendelse fra en hvilken som helst datakilde.
/// Justering (b) fra Prompt 1 v1: Value forblir object? i domenet, men lagres
/// som value_numeric (double?) + value_json (jsonb) i Postgres. Mapping håndteres
/// i KraftverkUptime.Infrastructure, ikke i Core.
///
/// Samme type bærer:
///  – Settlement-MWh-målinger (Value = double).
///  – SCADA-alarmer (Value = string eller strukturert objekt).
///  – Hydrologiske målinger (Value = double).
///  – Arbeidsordre-hendelser (Value = strukturert objekt).
///
/// Samme type på tvers av kilder gjør at fused analyzer kan konsumere én
/// sammenflettet strøm uten type-dispatch.
/// </summary>
public sealed record AssetEvent(
    string AssetId,
    DateTimeOffset TimestampUtc,
    string Source,
    string Measurement,
    object? Value,
    DataQualityState Quality,
    string? Unit,
    IReadOnlyDictionary<string, string> Tags,
    int SchemaVersion);
