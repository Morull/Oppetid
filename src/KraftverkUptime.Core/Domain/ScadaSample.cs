namespace KraftverkUptime.Core.Domain;

/// <summary>
/// En SCADA-måling for ett anlegg, ett signal, ett tidspunkt. Time-aggregerte
/// eksporter gir én rad per (asset, signal, time-of-hour). Lagres i
/// <c>core.sample_facts</c> som hypertable (TimescaleDB) eller vanlig
/// tabell (vanilla Postgres) — query-kontrakten er den samme.
///
/// <see cref="Quality"/>: 0 = good, 1 = uncertain, 2 = bad/missing.
/// Klassifikatoren bruker quality-flagget til å skille mellom ekte 0-verdier
/// (anlegget står) og manglende data (kommunikasjons-feil).
/// </summary>
public sealed record ScadaSample(
    string AssetId,
    string SignalId,
    DateTimeOffset TimeUtc,
    double? Value,
    short Quality);
