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
/// Overflow-modus per anlegg (Spec OVERFLOW-PROXY):
/// <list type="bullet">
///   <item><b>NativeTag</b> (default): SCADA-tag med rolle OverflowFlow på
///     terminal-dam. Brukes når anlegget har egen overløpsmåler.</item>
///   <item><b>LevelProxy</b>: utledet fra terminal-damens UpstreamLevel mot HRV
///     + terskel (cm over HRV). Brukes for Ørsdalen som mangler egen tag.</item>
///   <item><b>ProductionStateProxy</b>: utledet fra GeneratorActivePower-historikk
///     (timer der produksjon var aktiv). Brukes for Stølskraft (drikkevann
///     uten magasin-telemetri) — hvis maskinen produserte når alarmen kom,
///     ville produksjon pågått videre uten vakt.</item>
/// </list>
///
/// Hvis anlegget ikke har terminal-dam (dataintegritets-feil — backfill skal
/// garantere én pr anlegg): returner <c>DataAvailable = false</c>.
/// </summary>
public sealed class OverflowQueryService : IOverflowQueryService
{
    /// <summary>
    /// Terskel for å skille reell overløps-vannføring fra sensor-støy.
    ///
    /// Hevet fra 0.001 til 0.5 m³/s 2026-05-05 etter konkret feilmåling på
    /// Stemmevatn 9. mars 2026 (4 timer "overløp" på 0.16-2.28 m³/s under
    /// trip-event ble klassifisert som ekte overløp og ga 12 408 NOK i
    /// reddet produksjon — drifts-leder bekreftet at det var sensorglitch).
    ///
    /// 0.5 m³/s = 500 l/s, godt over typisk minstevannføring og kalibrerings-
    /// drift, men under et reelt overløp som ville vart i flere timer.
    /// Hvis du opplever at REELLE overløp ikke teller (lite anlegg med lav
    /// max-flow), juster denne ned per-anlegg-baserte overrides senere.
    /// </summary>
    public const double OverflowThresholdM3PerS = 0.5;

    /// <summary>
    /// Terskel for å skille reell produksjon fra noise/idle i
    /// <see cref="OverflowMode.ProductionStateProxy"/>. 1 kW dekker både
    /// støy ved 0 og lave egenforbruks-verdier; under er anlegget reelt sett
    /// ikke i produksjon.
    /// </summary>
    public const double ProductionThresholdKw = 1.0;

    private readonly ISignalMapRepository _signalMaps;
    private readonly IScadaSampleRepository _samples;
    private readonly IDamRepository _dams;
    private readonly IPlantOverflowConfigProvider _overflowConfig;
    private readonly ILogger<OverflowQueryService> _log;

    public OverflowQueryService(
        ISignalMapRepository signalMaps,
        IScadaSampleRepository samples,
        IDamRepository dams,
        IPlantOverflowConfigProvider overflowConfig,
        ILogger<OverflowQueryService> log)
    {
        _signalMaps = signalMaps ?? throw new ArgumentNullException(nameof(signalMaps));
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        _dams = dams ?? throw new ArgumentNullException(nameof(dams));
        _overflowConfig = overflowConfig ?? throw new ArgumentNullException(nameof(overflowConfig));
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

        var mode = await _overflowConfig.GetOverflowModeAsync(plantId, ct).ConfigureAwait(false);

        return mode switch
        {
            OverflowMode.LevelProxy => await QueryLevelProxyAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false),
            OverflowMode.ProductionStateProxy => await QueryProductionStateProxyAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false),
            _ => await QueryNativeTagAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false),
        };
    }

    public async Task<bool> HasOverflowTagAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        // Proxy-modus regnes som "har overflow-detektering" selv om det ikke
        // er en native tag — UI bruker dette til å vise/skjule advarsler.
        var mode = await _overflowConfig.GetOverflowModeAsync(plantId, ct).ConfigureAwait(false);
        if (mode is OverflowMode.LevelProxy or OverflowMode.ProductionStateProxy)
        {
            return true;
        }

        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null) return false;
        var tags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        return tags.Count > 0;
    }

    // ─────────── Native overflow-tag (default-modus) ────────────────────────

    private async Task<OverflowDataset> QueryNativeTagAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId} (NativeTag): ingen dam med IsTurbineIntake=true. " +
                "Sjekk at backfill er kjørt og at PlantAdmin-konfig er konsistent.",
                plantId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        var overflowTags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct)
            .ConfigureAwait(false);
        if (overflowTags.Count == 0)
        {
            _log.LogDebug(
                "Overflow-query for {PlantId}/{DamId} (NativeTag): ingen OverflowFlow-tag på terminal-dam.",
                plantId, terminalDam.DamId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

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
        var dataAvailable = samples.Count > 0;

        _log.LogDebug(
            "Overflow-query (NativeTag) for {PlantId}/{DamId} [{From},{To}): {HourCount} timer av {SampleCount} samples (data: {Available}).",
            plantId, terminalDam.DamId, fromUtc, toUtc, hours.Count, samples.Count, dataAvailable);

        return new OverflowDataset(hours, dataAvailable);
    }

    // ─────────── Level-proxy (Ørsdalen): level - HRV > terskel ──────────────

    private async Task<OverflowDataset> QueryLevelProxyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId} (LevelProxy): ingen terminal-dam — kan ikke beregne proxy.",
                plantId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        if (terminalDam.HrvMoh is null || terminalDam.OverflowProxyThresholdCm is null)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId}/{DamId} (LevelProxy): HRV eller terskel ikke konfigurert "
                + "(HRV={Hrv}, threshold={Threshold} cm). Fyll inn via PlantAdmin.",
                plantId, terminalDam.DamId, terminalDam.HrvMoh, terminalDam.OverflowProxyThresholdCm);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        var levelTags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.UpstreamLevel, ct)
            .ConfigureAwait(false);
        if (levelTags.Count == 0)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId}/{DamId} (LevelProxy): ingen UpstreamLevel-tag — kan ikke beregne proxy.",
                plantId, terminalDam.DamId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        var signalIds = levelTags.Select(t => t.SignalId).ToArray();
        var samples = await _samples
            .ListAsync(plantId, signalIds, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        var hrv = terminalDam.HrvMoh.Value;
        var thresholdMeters = terminalDam.OverflowProxyThresholdCm.Value / 100.0;

        var hours = new HashSet<DateTimeOffset>();
        foreach (var s in samples)
        {
            if (!s.Value.HasValue) continue;
            if (s.Value.Value - hrv <= thresholdMeters) continue;
            hours.Add(TruncateToHour(s.TimeUtc));
        }
        var dataAvailable = samples.Count > 0;

        _log.LogDebug(
            "Overflow-query (LevelProxy) for {PlantId}/{DamId} [{From},{To}): HRV={Hrv} moh, terskel={Cm} cm → "
            + "{HourCount} overløps-timer av {SampleCount} samples (data: {Available}).",
            plantId, terminalDam.DamId, fromUtc, toUtc, hrv, terminalDam.OverflowProxyThresholdCm.Value,
            hours.Count, samples.Count, dataAvailable);

        return new OverflowDataset(hours, dataAvailable);
    }

    // ─────────── Production-state-proxy (Stølskraft): GEN_P > 0 ─────────────

    private async Task<OverflowDataset> QueryProductionStateProxyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var prodTags = await _signalMaps
            .GetByPlantDamAndRoleAsync(plantId, damId: null, SignalRole.GeneratorActivePower, ct)
            .ConfigureAwait(false);
        if (prodTags.Count == 0)
        {
            _log.LogWarning(
                "Overflow-query for {PlantId} (ProductionStateProxy): ingen GeneratorActivePower-tag — "
                + "kan ikke beregne proxy.",
                plantId);
            return new OverflowDataset(new HashSet<DateTimeOffset>(), DataAvailable: false);
        }

        var signalIds = prodTags.Select(t => t.SignalId).ToArray();
        var samples = await _samples
            .ListAsync(plantId, signalIds, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        var hours = new HashSet<DateTimeOffset>();
        foreach (var s in samples)
        {
            if (!s.Value.HasValue) continue;
            if (s.Value.Value <= ProductionThresholdKw) continue;
            hours.Add(TruncateToHour(s.TimeUtc));
        }
        var dataAvailable = samples.Count > 0;

        _log.LogDebug(
            "Overflow-query (ProductionStateProxy) for {PlantId} [{From},{To}): {HourCount} produserende timer "
            + "av {SampleCount} samples (data: {Available}).",
            plantId, fromUtc, toUtc, hours.Count, samples.Count, dataAvailable);

        return new OverflowDataset(hours, dataAvailable);
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
