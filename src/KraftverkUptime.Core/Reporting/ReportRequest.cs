namespace KraftverkUptime.Core.Reporting;

/// <summary>
/// Eksplisitt, immutabel forespørsel for rapportgenerering.
/// Justering (a) fra Prompt 1 v1: erstatter tidligere åpen "..."-signatur.
/// </summary>
/// <param name="AssetId">Anleggs-/enhets-ID som rapporten gjelder for.</param>
/// <param name="FromUtc">Periodestart (UTC, inklusiv).</param>
/// <param name="ToUtc">Periodeslutt (UTC, eksklusiv).</param>
/// <param name="ReportKind">
/// Navn som identifiserer rapporttypen i modulkatalogen
/// (f.eks. "uptime.monthly", "uptime.weekly", "compliance.quarterly").
/// </param>
/// <param name="Options">
/// Rapporttype-spesifikke valg. Nøkler bør være prefikset med ReportKind
/// for å unngå kollisjon mellom moduler.
/// </param>
public sealed record ReportRequest(
    string AssetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string ReportKind,
    IReadOnlyDictionary<string, string>? Options = null);
