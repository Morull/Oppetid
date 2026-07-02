using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Henter samples for terminal-dam og kjører <see cref="InflowOverflowEstimator"/>
/// for en gitt periode. Brukes som tilsig-basert alternativ til den direkte
/// SCADA-overflow-deteksjonen i <see cref="OverflowQueryService"/>.
///
/// Eksponert via API-endepunkt <c>GET /api/v1/plants/{plantId}/inflow-estimate</c>
/// slik at drifts-leder kan sammenligne modellens estimat mot faktisk SCADA-
/// overflow før vi ev. bytter Vakt-ROI til å bruke modellen primært.
/// </summary>
public sealed class InflowOverflowQueryService
{
    private const int LookbackHours = 24;

    private readonly KraftverkDbContext _db;
    private readonly ISignalMapRepository _signalMaps;
    private readonly IScadaSampleRepository _samples;
    private readonly IDamRepository _dams;
    private readonly ILogger<InflowOverflowQueryService> _log;

    public InflowOverflowQueryService(
        KraftverkDbContext db,
        ISignalMapRepository signalMaps,
        IScadaSampleRepository samples,
        IDamRepository dams,
        ILogger<InflowOverflowQueryService> log)
    {
        _db = db;
        _signalMaps = signalMaps;
        _samples = samples;
        _dams = dams;
        _log = log;
    }

    public async Task<InflowEstimateResponse> EstimateAsync(
        string plantId, DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        // 1) Finn terminal-dam.
        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null)
        {
            return new InflowEstimateResponse(
                PlantId: plantId, FromUtc: windowFrom, ToUtc: windowTo,
                EstimatedOverflowHours: 0, EstimatedInflowM3PerS: 0,
                FreeCapacityAtStartM3: 0, HoursToFull: 0,
                DataAvailable: false,
                Forklaring: $"Plant '{plantId}' har ingen terminal-dam (IsTurbineIntake=true).");
        }

        // 2) Finn signal-IDer per rolle (kun de vi trenger).
        var volumeSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.ReservoirVolume, ct).ConfigureAwait(false);
        var totalDamFlowSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.TotalDamFlow, ct).ConfigureAwait(false);
        var fillRateSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.ReservoirFillFactor, ct).ConfigureAwait(false);
        var turbineFlowSignal = await _signalMaps.GetSignalIdForRoleAsync(
            plantId, SignalRole.TurbineWaterFlow, ct).ConfigureAwait(false);

        if (volumeSignals.Count == 0 || totalDamFlowSignals.Count == 0)
        {
            return new InflowEstimateResponse(
                PlantId: plantId, FromUtc: windowFrom, ToUtc: windowTo,
                EstimatedOverflowHours: 0, EstimatedInflowM3PerS: 0,
                FreeCapacityAtStartM3: 0, HoursToFull: 0,
                DataAvailable: false,
                Forklaring: "Mangler ReservoirVolume- og/eller TotalDamFlow-tag på terminal-dam — kan ikke estimere tilsig.");
        }

        // 3) Hent samples for lookback-perioden + window.
        var lookbackFrom = windowFrom.AddHours(-LookbackHours);
        var allSignalIds = volumeSignals.Select(s => s.SignalId)
            .Concat(totalDamFlowSignals.Select(s => s.SignalId))
            .Concat(fillRateSignals.Select(s => s.SignalId))
            .Concat(turbineFlowSignal is null ? Array.Empty<string>() : new[] { turbineFlowSignal })
            .Distinct()
            .ToArray();

        var samples = await _samples
            .ListAsync(plantId, allSignalIds, lookbackFrom, windowTo, ct)
            .ConfigureAwait(false);

        // 4) Pivot til time-baserte rader.
        // ReservoirVolume kommer i Mill.m³ — konverter til m³.
        var volTagId = volumeSignals[0].SignalId;
        var totDamTagId = totalDamFlowSignals[0].SignalId;
        var fillTagId = fillRateSignals.Count > 0 ? fillRateSignals[0].SignalId : null;
        var turbTagId = turbineFlowSignal;

        var byHour = new Dictionary<DateTimeOffset, (double? Vol, double? TotDam, double? Turb, double? Fill)>();
        foreach (var s in samples)
        {
            var hour = TruncateToHour(s.TimeUtc);
            byHour.TryGetValue(hour, out var row);
            if (s.SignalId == volTagId && s.Value.HasValue)
                row = (Vol: s.Value.Value * 1_000_000, row.TotDam, row.Turb, row.Fill);
            else if (s.SignalId == totDamTagId)
                row = (row.Vol, TotDam: s.Value, row.Turb, row.Fill);
            else if (s.SignalId == turbTagId)
                row = (row.Vol, row.TotDam, Turb: s.Value, row.Fill);
            else if (fillTagId is not null && s.SignalId == fillTagId)
                row = (row.Vol, row.TotDam, row.Turb, Fill: s.Value);
            byHour[hour] = row;
        }

        var hourly = byHour
            .OrderBy(kv => kv.Key)
            .Select(kv => new InflowOverflowEstimator.HourlySample(
                HourUtc: kv.Key,
                VolumeM3: kv.Value.Vol,
                TotalDamFlowM3PerS: kv.Value.TotDam,
                TurbineFlowM3PerS: kv.Value.Turb))
            .ToList();

        var lookbackSamples = hourly.Where(h => h.HourUtc < windowFrom).ToList();

        // Fyllgrad ved vindu-start (% → ratio). Plukk siste sample før windowFrom.
        var fillAtStart = byHour
            .Where(kv => kv.Key < windowFrom && kv.Value.Fill.HasValue)
            .OrderByDescending(kv => kv.Key)
            .Select(kv => kv.Value.Fill!.Value / 100.0)
            .Cast<double?>()
            .FirstOrDefault();

        // Maks volum: bruk dam.VolumeMm3 hvis satt; ellers bruk nyeste observerte
        // volum + ledig kapasitet (NEDBKAP) hvis tilgjengelig. Forenklet: krev VolumeMm3.
        var maxVolumeM3 = (terminalDam.VolumeMm3 ?? 0) * 1_000_000;

        var result = InflowOverflowEstimator.Estimate(
            lookbackSamples, windowFrom, windowTo, maxVolumeM3, fillAtStart);

        _log.LogDebug(
            "InflowEstimate for {PlantId}/{DamId} [{From}, {To}): tilsig {Inflow:F2} m³/s, fritt {Free:F0} m³, ville-overflow {Hours:F1} t",
            plantId, terminalDam.DamId, windowFrom, windowTo,
            result.EstimatedInflowM3PerS, result.FreeCapacityAtStartM3, result.EstimatedOverflowHours);

        return new InflowEstimateResponse(
            PlantId: plantId,
            FromUtc: windowFrom,
            ToUtc: windowTo,
            EstimatedOverflowHours: result.EstimatedOverflowHours,
            EstimatedInflowM3PerS: result.EstimatedInflowM3PerS,
            FreeCapacityAtStartM3: result.FreeCapacityAtStartM3,
            HoursToFull: double.IsInfinity(result.HoursToFull) ? -1 : result.HoursToFull,
            DataAvailable: result.DataAvailable,
            Forklaring: result.Forklaring);
    }

    /// <summary>
    /// Henter time-pivoterte dam-samples + maks-volum + fyllgrad-per-time for
    /// [<paramref name="windowFrom"/> − 24 t, <paramref name="windowTo"/>], til
    /// bruk i Vakt-ROI counterfactual-overløp (SPEC-VAKT-ROI-OVERLOP-V2).
    /// <see cref="VaktRoiCalculator"/> slicer selv lookback + fyllgrad per
    /// leder-gruppe. Returnerer null hvis terminal-dam, ReservoirVolume+
    /// TotalDamFlow-tagger eller <c>VolumeMm3</c> mangler — da faller Vakt-ROI
    /// tilbake til kun observert overløp (dagens oppførsel).
    /// </summary>
    public async Task<DamTelemetry?> GetDamTelemetryAsync(
        string plantId, DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct).ConfigureAwait(false);
        if (terminalDam is null) return null;

        // Manuelt volum fra dam-metadata (PlantAdmin) når satt. Mangler det,
        // utleder vi maks-volum fra samtidige volum-/fyllgrad-samples lenger
        // ned — dammene i porteføljen mangler stort sett VolumeMm3, og uten
        // maks-volum står hele tilsigsmodellen av (Vakt-ROI kunne da aldri
        // kreditere counterfactual-overløp, jf. Øgreyfoss 27.02/16.05-2026).
        var maxVolumeM3 = (terminalDam.VolumeMm3 ?? 0) * 1_000_000;

        var volumeSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.ReservoirVolume, ct).ConfigureAwait(false);
        var totalDamFlowSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.TotalDamFlow, ct).ConfigureAwait(false);
        if (volumeSignals.Count == 0 || totalDamFlowSignals.Count == 0) return null;

        var fillRateSignals = await _signalMaps.GetByPlantDamAndRoleAsync(
            plantId, terminalDam.DamId, SignalRole.ReservoirFillFactor, ct).ConfigureAwait(false);
        var turbineFlowSignal = await _signalMaps.GetSignalIdForRoleAsync(
            plantId, SignalRole.TurbineWaterFlow, ct).ConfigureAwait(false);

        var lookbackFrom = windowFrom.AddHours(-LookbackHours);
        var allSignalIds = volumeSignals.Select(s => s.SignalId)
            .Concat(totalDamFlowSignals.Select(s => s.SignalId))
            .Concat(fillRateSignals.Select(s => s.SignalId))
            .Concat(turbineFlowSignal is null ? Array.Empty<string>() : new[] { turbineFlowSignal })
            .Distinct()
            .ToArray();

        var samples = await _samples
            .ListAsync(plantId, allSignalIds, lookbackFrom, windowTo, ct)
            .ConfigureAwait(false);

        var volTagId = volumeSignals[0].SignalId;
        var totDamTagId = totalDamFlowSignals[0].SignalId;
        var fillTagId = fillRateSignals.Count > 0 ? fillRateSignals[0].SignalId : null;
        var turbTagId = turbineFlowSignal;

        // NB: pivot-logikken speiler EstimateAsync (ReservoirVolume Mill.m³ → m³,
        // fyllgrad % → ratio). Hold dem i synk.
        var byHour = new Dictionary<DateTimeOffset, (double? Vol, double? TotDam, double? Turb, double? Fill)>();
        foreach (var s in samples)
        {
            var hour = TruncateToHour(s.TimeUtc);
            byHour.TryGetValue(hour, out var row);
            if (s.SignalId == volTagId && s.Value.HasValue)
                row = (s.Value.Value * 1_000_000, row.TotDam, row.Turb, row.Fill);
            else if (s.SignalId == totDamTagId)
                row = (row.Vol, s.Value, row.Turb, row.Fill);
            else if (s.SignalId == turbTagId)
                row = (row.Vol, row.TotDam, s.Value, row.Fill);
            else if (fillTagId is not null && s.SignalId == fillTagId)
                row = (row.Vol, row.TotDam, row.Turb, s.Value);
            byHour[hour] = row;
        }

        var hourly = byHour
            .OrderBy(kv => kv.Key)
            .Select(kv => new InflowOverflowEstimator.HourlySample(
                kv.Key, kv.Value.Vol, kv.Value.TotDam, kv.Value.Turb))
            .ToList();

        var fillByHour = byHour
            .Where(kv => kv.Value.Fill.HasValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Fill!.Value / 100.0);

        // Selvkalibrering: maks-volum = median av (volum_m3 / fyllgrad-ratio)
        // over timer der begge signaler finnes og fyllgraden er i et pålitelig
        // område (20–120 % — lave verdier gir støyfølsom divisjon, og >120 %
        // er sensor-avvik). SCADA-fyllgraden refererer HRV, så ratioen er
        // per definisjon volum-ved-HRV = maks-volum. Manuelt volum i
        // dam-metadata trumfer (sjekket over).
        if (maxVolumeM3 <= 0)
        {
            var ratios = byHour.Values
                .Where(r => r.Vol is > 0 && r.Fill is >= 20 and <= 120)
                .Select(r => r.Vol!.Value / (r.Fill!.Value / 100.0))
                .OrderBy(x => x)
                .ToList();
            // Krev noen samples så én enkelt glitch ikke setter kalibreringen.
            if (ratios.Count >= 3)
            {
                maxVolumeM3 = ratios[ratios.Count / 2];
            }
        }
        if (maxVolumeM3 <= 0) return null;

        return new DamTelemetry(hourly, maxVolumeM3, fillByHour);
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// Dam-telemetri for Vakt-ROI counterfactual-overløp: time-pivoterte samples,
/// maks magasin-volum (m³) og fyllgrad (ratio 0..1) per UTC-time.
/// </summary>
public sealed record DamTelemetry(
    IReadOnlyList<InflowOverflowEstimator.HourlySample> Samples,
    double MaxVolumeM3,
    IReadOnlyDictionary<DateTimeOffset, double> FillRateByHour);

public sealed record InflowEstimateResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double EstimatedOverflowHours,
    double EstimatedInflowM3PerS,
    double FreeCapacityAtStartM3,
    double HoursToFull,
    bool DataAvailable,
    string Forklaring);
