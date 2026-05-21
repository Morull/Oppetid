using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Reporting.Effektivitet;

/// <summary>
/// Standard <see cref="IEffectivityQueryService"/>: aggregerer SCADA-15-min-rader
/// fra signal-rolle-mapping og bygger η(P)-histogram + KPI-er.
///
/// 15-min-overgang (2026-05-21, Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md):
///   – Leser fra <see cref="IScadaSampleFineRepository"/> i stedet for hourly,
///     med fallback til <see cref="IScadaSampleRepository"/> hvis 15-min-data
///     mangler. Holder kontrakten arbeidsdyktig før første 15-min-import.
///   – Klassifiserer hvert intervall som Genuine eller Transition (start/stopp-
///     ramper) basert på (P ≥ terskel, η &lt; gulv). Transition-punkter er
///     synlige men teller IKKE i snitt-aggregat — ellers ville en ramp-down
///     med η = 20 % dra snittet ned fra 91 % til 88 % på en ellers stabil dag.
///   – Integrasjons-konstantene endres fra 1 t til 0.25 t per intervall:
///     <c>sumP_kWh += P × 0.25</c> (15-min = 0.25 t), <c>sumQ_m3 += Q × 900</c>
///     (15 min × 60 s = 900 s).
///
/// Sweet-spot finnes algoritmisk: SCADA-rådata bin'es i <see cref="PowerBinKw"/>-
/// brede effekt-intervaller, snitt-virkningsgrad beregnes per bin med &gt;
/// <see cref="MinSamplesPerBin"/> samples, og bin'en med høyest snitt vinner.
/// </summary>
public sealed class EffektivitetQueryService : IEffectivityQueryService
{
    /// <summary>Effekt under denne ignoreres som "ikke-produksjon" (kW).</summary>
    public const double ProductionThresholdKw = 50;

    /// <summary>Bredde på effekt-bin i kW for sweet-spot-deteksjon.</summary>
    public const double PowerBinKw = 200;

    /// <summary>Minimum antall samples i en bin før den vurderes som sweet-spot-kandidat.</summary>
    public const int MinSamplesPerBin = 3;

    /// <summary>
    /// η-gulv (prosent) for "Genuine"-klassifisering. Intervaller med
    /// P ≥ <see cref="ProductionThresholdKw"/> men η under dette flagges som
    /// <see cref="PunktKlassifisering.Transition"/> og holdes utenfor snitt-
    /// aggregat. Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md: 50 % er valgt fordi
    /// alle 11 Dalane Kraft-anlegg har stabil η i 85–93 % område når de er
    /// i full drift — alt under ~50 % er per definisjon start/stopp-ramp.
    /// </summary>
    public const double GenuineEtaFloorPct = 50;

    /// <summary>Lengden på ett 15-min-intervall i timer (0.25 t).</summary>
    private const double IntervalHours = 0.25;

    /// <summary>Lengden på ett 15-min-intervall i sekunder (900 s).</summary>
    private const double IntervalSeconds = 900;

    private readonly ISignalMapRepository _signalMaps;
    private readonly IScadaSampleFineRepository _samplesFine;
    private readonly IScadaSampleRepository _samplesHourly;
    private readonly ILogger<EffektivitetQueryService> _log;

    public EffektivitetQueryService(
        ISignalMapRepository signalMaps,
        IScadaSampleFineRepository samplesFine,
        IScadaSampleRepository samplesHourly,
        ILogger<EffektivitetQueryService> log)
    {
        _signalMaps = signalMaps ?? throw new ArgumentNullException(nameof(signalMaps));
        _samplesFine = samplesFine ?? throw new ArgumentNullException(nameof(samplesFine));
        _samplesHourly = samplesHourly ?? throw new ArgumentNullException(nameof(samplesHourly));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<EffektivitetResponse> GetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return Empty(plantId, fromUtc, toUtc, dataMissing: false);
        }

        var powerSignal = await _signalMaps
            .GetSignalIdForRoleAsync(plantId, SignalRole.GeneratorActivePower, ct)
            .ConfigureAwait(false);
        var etaSignal = await _signalMaps
            .GetSignalIdForRoleAsync(plantId, SignalRole.TurbineEfficiency, ct)
            .ConfigureAwait(false);
        var flowSignal = await _signalMaps
            .GetSignalIdForRoleAsync(plantId, SignalRole.TurbineWaterFlow, ct)
            .ConfigureAwait(false);

        if (powerSignal is null || etaSignal is null || flowSignal is null)
        {
            _log.LogInformation(
                "Effektivitet for {PlantId}: mangler SCADA-rolle (power={P}, eta={E}, flow={F}).",
                plantId, powerSignal, etaSignal, flowSignal);
            return Empty(plantId, fromUtc, toUtc, dataMissing: true);
        }

        // 15-min er ny primær-kilde (Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md).
        // Hourly faller vi tilbake til hvis ingen 15-min-data finnes — slik at
        // historikk fra før 15-min-pipelinen ble innført fortsatt vises uten
        // tom-rapport.
        var samples = await _samplesFine
            .ListAsync(plantId, new[] { powerSignal, etaSignal, flowSignal }, fromUtc, toUtc, ct)
            .ConfigureAwait(false);
        var usedFineSource = samples.Count > 0;

        if (samples.Count == 0)
        {
            _log.LogDebug(
                "Effektivitet for {PlantId}: ingen 15-min-samples i {From}–{To}, " +
                "faller tilbake til hourly.", plantId, fromUtc, toUtc);
            samples = await _samplesHourly
                .ListAsync(plantId, new[] { powerSignal, etaSignal, flowSignal }, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
        }

        if (samples.Count == 0)
        {
            return Empty(plantId, fromUtc, toUtc, dataMissing: true);
        }

        // Pivot: gruppere per time-stamp slik at hver rad har (P, η, Q).
        // 15-min er kvarter-aligned (00, 15, 30, 45); hourly er hel-time —
        // begge format-er funker med dictionary-pivot.
        var byTime = new Dictionary<DateTimeOffset, (double? P, double? Eta, double? Q)>();
        foreach (var s in samples)
        {
            byTime.TryGetValue(s.TimeUtc, out var current);
            if (s.SignalId == powerSignal) current.P = s.Value;
            else if (s.SignalId == etaSignal) current.Eta = s.Value;
            else if (s.SignalId == flowSignal) current.Q = s.Value;
            byTime[s.TimeUtc] = current;
        }

        // Hvis vi falt tilbake til hourly er hvert intervall 1 t, ikke 0.25 t.
        var hours = usedFineSource ? IntervalHours : 1.0;
        var seconds = usedFineSource ? IntervalSeconds : 3600.0;

        var punkter = new List<EffektivitetPunkt>(byTime.Count);
        double sumEtaPct = 0;
        var sumP_kWh = 0.0;
        var sumQ_m3 = 0.0;
        var genuineCount = 0;

        foreach (var (time, vals) in byTime.OrderBy(kv => kv.Key))
        {
            if (!vals.P.HasValue || !vals.Eta.HasValue || !vals.Q.HasValue) continue;
            if (vals.P.Value < ProductionThresholdKw) continue;

            // Klassifiser: η under gulv = Transition (start/stopp-ramp).
            var klass = vals.Eta.Value < GenuineEtaFloorPct
                ? PunktKlassifisering.Transition
                : PunktKlassifisering.Genuine;

            punkter.Add(new EffektivitetPunkt(
                TimeUtc: time,
                EffektKw: vals.P.Value,
                EtaPct: vals.Eta.Value,
                VannforingM3PerS: vals.Q.Value,
                Klassifisering: klass));

            // Bare Genuine teller i KPI-aggregat. Transition holdes utenfor for
            // å unngå at start/stopp-ramper drar snitt-η ned med 5-10 prosent-
            // poeng på et ellers stabilt anlegg.
            if (klass == PunktKlassifisering.Genuine)
            {
                sumEtaPct += vals.Eta.Value;
                sumP_kWh += vals.P.Value * hours;
                sumQ_m3 += vals.Q.Value * seconds;
                genuineCount++;
            }
        }

        if (genuineCount == 0)
        {
            // Vi kan ha Transition-punkter (visualiseres) men ingen Genuine —
            // betyr at anlegget ikke har stabil drift i perioden. Returner
            // tomme aggregat men behold punktene så UI kan vise ramp-mønster.
            return new EffektivitetResponse(
                PlantId: plantId,
                FromUtc: fromUtc,
                ToUtc: toUtc,
                ProduksjonsTimer: 0,
                SnittEtaPct: 0,
                SweetSpotEffektKw: 0,
                SweetSpotEtaPct: 0,
                SnittSpesifiktVannforbrukM3PerKwh: 0,
                TotalProduksjonKwh: 0,
                DataMissing: false,
                Punkter: punkter,
                Bins: Array.Empty<EffektivitetBin>());
        }

        var snittEta = sumEtaPct / genuineCount;
        var svf = sumP_kWh > 0 ? sumQ_m3 / sumP_kWh : 0;

        // Bins bygges KUN fra Genuine — sweet-spot skal ikke flytte seg pga.
        // ramp-statistikk.
        var genuinePunkter = punkter.Where(p => p.Klassifisering == PunktKlassifisering.Genuine).ToList();
        var bins = ComputeBins(genuinePunkter);
        var (sweetSpotKw, sweetSpotEta) = FindSweetSpot(bins);

        return new EffektivitetResponse(
            PlantId: plantId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            ProduksjonsTimer: genuineCount,
            SnittEtaPct: snittEta,
            SweetSpotEffektKw: sweetSpotKw,
            SweetSpotEtaPct: sweetSpotEta,
            SnittSpesifiktVannforbrukM3PerKwh: svf,
            TotalProduksjonKwh: sumP_kWh,
            DataMissing: false,
            Punkter: punkter,
            Bins: bins);
    }

    /// <summary>
    /// Bin'er punkter på <see cref="PowerBinKw"/>-brede effekt-intervaller og
    /// returnerer ikke-tomme bins sortert på start-effekt.
    /// </summary>
    private static IReadOnlyList<EffektivitetBin> ComputeBins(IReadOnlyList<EffektivitetPunkt> punkter)
    {
        var grouped = new Dictionary<int, (int Count, double SumEta)>();
        foreach (var p in punkter)
        {
            var bucketIndex = (int)Math.Floor(p.EffektKw / PowerBinKw);
            grouped.TryGetValue(bucketIndex, out var current);
            current.Count++;
            current.SumEta += p.EtaPct;
            grouped[bucketIndex] = current;
        }

        var bins = new List<EffektivitetBin>(grouped.Count);
        foreach (var (idx, agg) in grouped.OrderBy(kv => kv.Key))
        {
            var start = idx * PowerBinKw;
            bins.Add(new EffektivitetBin(
                EffektKwStart: start,
                EffektKwMid: start + PowerBinKw / 2,
                Antall: agg.Count,
                SnittEtaPct: agg.SumEta / agg.Count));
        }
        return bins;
    }

    private static (double Kw, double EtaPct) FindSweetSpot(IReadOnlyList<EffektivitetBin> bins)
    {
        EffektivitetBin? best = null;
        foreach (var bin in bins)
        {
            if (bin.Antall < MinSamplesPerBin) continue;
            if (best is null || bin.SnittEtaPct > best.SnittEtaPct)
            {
                best = bin;
            }
        }
        return best is null ? (0, 0) : (best.EffektKwMid, best.SnittEtaPct);
    }

    private static EffektivitetResponse Empty(
        string plantId, DateTimeOffset from, DateTimeOffset to, bool dataMissing) =>
        new(
            PlantId: plantId,
            FromUtc: from,
            ToUtc: to,
            ProduksjonsTimer: 0,
            SnittEtaPct: 0,
            SweetSpotEffektKw: 0,
            SweetSpotEtaPct: 0,
            SnittSpesifiktVannforbrukM3PerKwh: 0,
            TotalProduksjonKwh: 0,
            DataMissing: dataMissing,
            Punkter: Array.Empty<EffektivitetPunkt>(),
            Bins: Array.Empty<EffektivitetBin>());
}
