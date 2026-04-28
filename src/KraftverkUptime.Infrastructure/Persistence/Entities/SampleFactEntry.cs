namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.sample_facts</c>. Lagres som hypertable når
/// TimescaleDB er aktivert; ellers som vanlig tabell. Volumet skaleres
/// med 20 anlegg × 39 tags × 24 timer × 30 dager ≈ 560 000 rader/mnd
/// (~30 MB). Helt overkommelig på vanlig Postgres for de første åra.
///
/// Implementerer ikke IOwnedEntity her fordi tenant-filter på samples
/// er for dyrt; tilgangs-kontroll skjer på plant-nivå før spørring sendes.
/// </summary>
public sealed class SampleFactEntry
{
    public string AssetId { get; set; } = string.Empty;
    public string SignalId { get; set; } = string.Empty;
    public DateTimeOffset TimeUtc { get; set; }
    public double? Value { get; set; }
    public short Quality { get; set; }
}
