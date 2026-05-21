namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.sample_facts_fine</c> — fin-oppløsnings SCADA-
/// samples (15-min eller raskere). Speiler kolonnene i
/// <see cref="SampleFactEntry"/>, men lagrer i en EGEN tabell slik at den
/// hourly pipelinen (<c>sample_facts</c>) ikke overskrives.
///
/// Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md (2026-05-21): 15-min-eksporten
/// inneholder samme tags som 73-tag hourly-masteren — en delt tabell
/// ville overskrevet hverandre på <c>:00</c>-tidsstempler, og bryte
/// konsumenter som <c>OverflowQueryService</c>s ProductionStateProxy.
///
/// Format identisk med <c>SampleFactEntry</c> — primærnøkkel
/// (asset_id, signal_id, time_utc), nullable value, quality 0|1|2.
/// </summary>
public sealed class SampleFactFineEntry
{
    public string AssetId { get; set; } = string.Empty;
    public string SignalId { get; set; } = string.Empty;
    public DateTimeOffset TimeUtc { get; set; }
    public double? Value { get; set; }
    public short Quality { get; set; }
}
