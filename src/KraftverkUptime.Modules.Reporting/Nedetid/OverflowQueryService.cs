using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Standard-implementasjon av <see cref="IOverflowQueryService"/>.
///
/// Kaskade-modell (Spec KASKADE-DAMMER): kun overløp på terminal-dam telles.
/// Drifts-leders korreksjon: "Overløpet er kun viktig for siste dam før
/// kraftverket". Øvre dammer i kaskaden kan flomme over uten at det koster
/// produksjon — vannet renner ned til neste dam og kan fanges der. Først når
/// den siste dam i kaskaden flommer er vannet definitivt tapt.
///
/// Strategi:
///   1. Slå opp terminal-dam (IsTurbineIntake=true) via <see cref="IDamRepository"/>
///   2. Hent OverflowFlow-tags som er knyttet til den dammen
///   3. Hent samples og returner timer over støyterskel
///
/// Hvis anlegget ikke har terminal-dam (dataintegritets-feil — backfill skal
/// garantere én pr anlegg): returner <c>DataAvailable = false</c>.
/// </summary>
public sealed class OverflowQueryService : IOverflowQueryService
{
    /// <summary>
    /// Terskel for å skille reell overløps-vannføring fra floating-point støy
    /// i SCADA-eksporten. 0.001 m³/s = 1 l/s, godt under enhver reell
    /// minstevannføring og tar høyde for sensorrydding rundt null.
    /// </summary>
    public const double OverflowThresholdM3PerS = 0.001;

    private readonly ISignalMapRepository _signalMaps;
    private readonly IScadaSampleRepository _samples;
    private readonly IDamRepository _dams;
    private readonly ILogger<OverflowQueryService> _log;

    public OverflowQueryService(
        ISignalMapRepository signalMaps,
        IScadaSampleRepository samples,
        IDamRepository dams,
        ILogger<OverflowQueryService> log)
    {
        _signalMaps = signalMaps ?? throw new ArgumentNullException(nameof(signalMaps));
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        _dams = dams ?? throw new ArgumentNullException(nameof(dams));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<OverflowDataset> GetOverflowDatasetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        // 1) Finn terminal-dam (siste før turbin). Beskytter mot multi-dam-anlegg
        // der vi ellers ville plukket opp overløp på øvre dammer som ikke koster
        // produksjon.
        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId}: ingen dam med IsTurbineIntake=true. " +
                "Sjekk at backfill er kjørt og at PlantAdmin-konfig er konsistent.",
                plantId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        // 2) Hent OverflowFlow-tags som hører til terminal-dam.
        var overflowTags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        if (overflowTags.Count == 0)
        {
            _log.LogDebug(
                "Overflow-query for {PlantId}/{DamId}: ingen OverflowFlow-tag på terminal-dam.",
                plantId, terminalDam.DamId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        // 3) Hent samples for alle terminal-dam-overflow-tags. Vanligvis bare én
        // tag per dam, men listen gir robusthet hvis et anlegg har redundante
        // målere på samme magasin.
        var signalIds = overflowTags.Select(t => t.SignalId).ToArray();
        var samples = await _samples
            .ListAsync(plantId, signalIds, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        var hours = new HashSet<DateTimeOffset>();
        foreach (var s in samples)
        {
            if (!s.Value.HasValue) continue;
            if (s.Value.Value <= OverflowThresholdM3PerS) continue;
            hours.Add(TruncateToHour(s.TimeUtc));
        }

        // Data anses som tilgjengelig så lenge minst ett sample finnes i
        // perioden — selv hvis verdien er null/0 (= "kjent ingen overløp").
        // Tom samples-liste = SCADA-importen dekker ikke perioden → flagges
        // som missing slik at drifts-leder ser at ROI ikke er beregnet.
        var dataAvailable = samples.Count > 0;

        _log.LogDebug(
            "Overflow-query for {PlantId}/{DamId} [{From},{To}): {HourCount} timer med overløp av {SampleCount} samples (data tilgjengelig: {Available}).",
            plantId, terminalDam.DamId, fromUtc, toUtc, hours.Count, samples.Count, dataAvailable);

        return new OverflowDataset(hours, dataAvailable);
    }

    public async Task<bool> HasOverflowTagAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null) return false;
        var tags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        return tags.Count > 0;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
