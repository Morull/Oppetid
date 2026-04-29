using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Standard-implementasjon av <see cref="IOverflowQueryService"/>. Slår opp
/// signal_id for <see cref="SignalRole.OverflowFlow"/> via
/// <see cref="ISignalMapRepository"/>, henter samples fra
/// <see cref="IScadaSampleRepository"/> og returnerer settet av timer der
/// vannføringen var over støy-terskelen.
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
    private readonly ILogger<OverflowQueryService> _log;

    public OverflowQueryService(
        ISignalMapRepository signalMaps,
        IScadaSampleRepository samples,
        ILogger<OverflowQueryService> log)
    {
        _signalMaps = signalMaps ?? throw new ArgumentNullException(nameof(signalMaps));
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
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

        var signalId = await _signalMaps
            .GetSignalIdForRoleAsync(plantId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        if (signalId is null)
        {
            _log.LogDebug(
                "Overflow-query for {PlantId}: ingen OverflowFlow-tag konfigurert.",
                plantId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        var samples = await _samples
            .ListAsync(plantId, new[] { signalId }, fromUtc, toUtc, ct)
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
            "Overflow-query for {PlantId} [{From},{To}): {HourCount} timer med overløp av {SampleCount} samples (data tilgjengelig: {Available}).",
            plantId, fromUtc, toUtc, hours.Count, samples.Count, dataAvailable);

        return new OverflowDataset(hours, dataAvailable);
    }

    public async Task<bool> HasOverflowTagAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        var signalId = await _signalMaps
            .GetSignalIdForRoleAsync(plantId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        return signalId is not null;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
