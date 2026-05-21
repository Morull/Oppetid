namespace KraftverkUptime.Modules.Reporting.Effektivitet;

/// <summary>
/// Default-impl av <see cref="IEffektivitetEpisodeService"/>. Ren funksjon —
/// ingen DB- eller HTTP-avhengighet — kalles fra en wrapping query-service
/// som leverer spotpriser ved siden av effektivitets-responsen.
///
/// Algoritme (Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3.1–3.2):
///   1. Baseline per effekt-bin = bin-snitt-η (allerede beregnet i query-
///      service-en). Bin'er som har færre enn
///      <see cref="EpisodeAnalyseOpsjoner.MinSamplesPerBaselineBin"/> samples
///      ignoreres for baseline — for tynt grunnlag til å sammenligne mot.
///   2. For hvert Genuine-punkt: Δη = η_faktisk − baseline_for_bin'en.
///      Punkter i bin'er uten baseline droppes (kan ikke vurderes).
///   3. Sorter punkter på tid. Et punkt er underytende hvis Δη ≤ terskelen
///      (default −2,0 pp). Sammenhengende underytende punkter slås sammen
///      til en episode. Inntil <see cref="EpisodeAnalyseOpsjoner.TillattGapIntervaller"/>
///      normale punkter i mellom er ok (typisk én — robust mot enkelt-
///      målepunkt-støy uten å slå sammen reelt uavhengige episoder).
///   4. Per episode: tapt MWh ≈ Σ faktisk_kWh × (baseline_η − faktisk_η) / faktisk_η
///      og tapt NOK = Σ tapt_MWh × spotpris(time) der spotpris er kjent.
///   5. Aggreger pr effekt-bånd basert på bin'ene punktene falt i.
/// </summary>
public sealed class EffektivitetEpisodeService : IEffektivitetEpisodeService
{
    public EpisodeAnalysisResult Analyse(
        EffektivitetResponse effektivitet,
        IReadOnlyDictionary<DateTimeOffset, double>? spotPrisNokMwhPerTime,
        EpisodeAnalyseOpsjoner? opsjoner = null)
    {
        ArgumentNullException.ThrowIfNull(effektivitet);
        opsjoner ??= new EpisodeAnalyseOpsjoner();

        // 1. Referanse-lookup: returnerer "forventet η" for en gitt effekt-bin.
        //    Baseline-modus  : bin-spesifikk (anleggets snitt-η i samme bin).
        //    Sweet-spot-modus: konstant (anleggets toppunkt — uavhengig av bin).
        Func<double, double?> getReferanseEta;
        if (opsjoner.Referanse == EpisodeReferanseTyp.SweetSpot)
        {
            if (effektivitet.SweetSpotEtaPct <= 0)
            {
                // Ingen sweet-spot funnet → kan ikke kjøre denne analysen.
                return EmptyResult(spotPrisNokMwhPerTime is null);
            }
            var sweetEta = effektivitet.SweetSpotEtaPct;
            getReferanseEta = _ => sweetEta;
        }
        else
        {
            var baselineByBinStart = effektivitet.Bins
                .Where(b => b.Antall >= opsjoner.MinSamplesPerBaselineBin)
                .ToDictionary(b => b.EffektKwStart, b => b.SnittEtaPct);
            getReferanseEta = binStart =>
                baselineByBinStart.TryGetValue(binStart, out var eta) ? eta : null;
        }

        // 2. Gjør Genuine-punktene om til Δη-pakker. Sorter på tid.
        //    Punkter der referanse-η ikke finnes (bin uten baseline, eller
        //    intervall ≥ sweet-spot-η) faller ut av analysen.
        var underytende = new List<EvaluertPunkt>();
        var totalGenuine = 0;
        foreach (var p in effektivitet.Punkter
            .Where(p => p.Klassifisering == PunktKlassifisering.Genuine)
            .OrderBy(p => p.TimeUtc))
        {
            totalGenuine++;
            var binStart = Math.Floor(p.EffektKw / EffektivitetQueryService.PowerBinKw)
                * EffektivitetQueryService.PowerBinKw;
            var referanseEta = getReferanseEta(binStart);
            if (referanseEta is null) continue;
            var deltaEta = p.EtaPct - referanseEta.Value;
            underytende.Add(new EvaluertPunkt(p, binStart, referanseEta.Value, deltaEta));
        }

        // 3. Slå sammen sammenhengende underytende intervaller til episoder.
        //    "Sammenhengende" = inntil TillattGapIntervaller normale punkter
        //    mellom. Sammenligningen gjøres på TID-rekkefølge, ikke index, så
        //    bin'er uten baseline (som er droppet) ikke teller som "gap".
        var manglerPriser = spotPrisNokMwhPerTime is null;
        var episoder = BuildEpisoder(underytende, opsjoner, spotPrisNokMwhPerTime).ToList();

        // 4. Aggregat per effekt-bånd. Baserer seg på bin-start fra hvert
        //    interval i hver episode — én episode kan spenne flere bånd.
        var perBaand = AggregerPerEffektBaand(episoder, underytende, spotPrisNokMwhPerTime).ToList();

        return new EpisodeAnalysisResult(
            Episoder: episoder,
            AggregatPerEffektBaand: perBaand,
            TotalTaptMwh: episoder.Sum(e => e.TaptMwh),
            TotalTaptNok: episoder.Sum(e => e.TaptNok),
            AntallGenuineIntervaller: totalGenuine,
            AntallUnderytendeIntervaller: episoder.Sum(e => e.AntallIntervaller),
            ManglerSpotpriser: manglerPriser);
    }

    /// <summary>Tomt resultat — brukes når sweet-spot-modus ikke har en gyldig referanse.</summary>
    private static EpisodeAnalysisResult EmptyResult(bool manglerPriser) =>
        new(
            Array.Empty<UnderytendeEpisode>(),
            Array.Empty<EffektBaandAggregat>(),
            TotalTaptMwh: 0,
            TotalTaptNok: 0,
            AntallGenuineIntervaller: 0,
            AntallUnderytendeIntervaller: 0,
            ManglerSpotpriser: manglerPriser);

    private static IEnumerable<UnderytendeEpisode> BuildEpisoder(
        IReadOnlyList<EvaluertPunkt> punkter,
        EpisodeAnalyseOpsjoner opsjoner,
        IReadOnlyDictionary<DateTimeOffset, double>? priser)
    {
        // Tid-basert episode-deling: en episode avsluttes hvis det går mer enn
        // (1 + TillattGapIntervaller) × 15 min mellom to underytende intervaller.
        // Mellomliggende tid kan være enten eksplisitte normale intervaller eller
        // implisitt manglende data (Transition/datahull) — algoritmen
        // behandler begge likt: er det for langt fra forrige underytende, så er
        // episoden slutt.
        const double intervallMinutter = 15.0;
        var maxGap = TimeSpan.FromMinutes(intervallMinutter * (1 + opsjoner.TillattGapIntervaller));

        var current = new List<EvaluertPunkt>();
        DateTimeOffset? sisteUnderytende = null;

        foreach (var p in punkter)
        {
            var erUnderytende = p.DeltaEtaPp <= opsjoner.DeltaEtaTerskelPp;
            if (!erUnderytende) continue;

            if (sisteUnderytende is not null && p.Punkt.TimeUtc - sisteUnderytende.Value > maxGap)
            {
                yield return BuildEpisode(current, priser);
                current = new List<EvaluertPunkt>();
            }

            current.Add(p);
            sisteUnderytende = p.Punkt.TimeUtc;
        }

        if (current.Count > 0)
        {
            yield return BuildEpisode(current, priser);
        }
    }

    private static UnderytendeEpisode BuildEpisode(
        IReadOnlyList<EvaluertPunkt> intervaller,
        IReadOnlyDictionary<DateTimeOffset, double>? priser)
    {
        // Antar 15-min-intervaller (Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md).
        // Hourly-fallback gir ikke meningsfull episode-analyse — den krever
        // 15-min for å fange ramp-aktige avvik.
        const double intervallTimer = 0.25;

        var faktiskMwh = 0.0;
        var taptMwh = 0.0;
        var taptNok = 0.0;
        var taptNokErEstimat = false;
        double minKw = double.MaxValue, maksKw = double.MinValue;
        var sumDeltaEta = 0.0;

        foreach (var iv in intervaller)
        {
            var faktiskKwh = iv.Punkt.EffektKw * intervallTimer;
            faktiskMwh += faktiskKwh / 1000.0;

            // Tapt energi: hvis vi hadde kjørt på baseline-η, hadde vi produsert
            //   faktisk_produksjon × baseline_η / faktisk_η
            // → tap = faktisk × (baseline − faktisk) / faktisk
            if (iv.Punkt.EtaPct > 0.0)
            {
                var taptKwh = faktiskKwh * (iv.ReferanseEtaPct - iv.Punkt.EtaPct) / iv.Punkt.EtaPct;
                var taptMwhI = taptKwh / 1000.0;
                taptMwh += taptMwhI;

                if (priser is not null)
                {
                    // Spotpris er hourly i NO-markedet. Bruker hourly-bucket på time-stempelet.
                    var hourKey = ToHourBucket(iv.Punkt.TimeUtc);
                    if (priser.TryGetValue(hourKey, out var prisNokMwh))
                    {
                        taptNok += taptMwhI * prisNokMwh;
                    }
                    else
                    {
                        taptNokErEstimat = true;
                    }
                }
            }

            if (iv.Punkt.EffektKw < minKw) minKw = iv.Punkt.EffektKw;
            if (iv.Punkt.EffektKw > maksKw) maksKw = iv.Punkt.EffektKw;
            sumDeltaEta += iv.DeltaEtaPp;
        }

        var first = intervaller[0].Punkt.TimeUtc;
        var last = intervaller[^1].Punkt.TimeUtc;

        return new UnderytendeEpisode(
            StartUtc: first,
            SluttUtc: last.AddMinutes(15), // siste intervall slutter 15 min etter sitt time-stempel
            AntallIntervaller: intervaller.Count,
            VarighetTimer: intervaller.Count * intervallTimer,
            SnittDeltaEtaPp: sumDeltaEta / intervaller.Count,
            EffektMinKw: minKw,
            EffektMaksKw: maksKw,
            FaktiskProduksjonMwh: faktiskMwh,
            TaptMwh: taptMwh,
            TaptNok: taptNok,
            TaptNokErEstimat: priser is null || taptNokErEstimat);
    }

    /// <summary>
    /// Aggregat per effekt-bånd. Itererer over alle underytende intervaller
    /// (ikke bare episode-sammenslåtte) og grupperer på bin-start. Et
    /// effekt-bånd kan dermed dekkes av flere uavhengige episoder.
    /// </summary>
    private static IEnumerable<EffektBaandAggregat> AggregerPerEffektBaand(
        IReadOnlyList<UnderytendeEpisode> episoder,
        IReadOnlyList<EvaluertPunkt> underytende,
        IReadOnlyDictionary<DateTimeOffset, double>? priser)
    {
        const double intervallTimer = 0.25;
        var underytendePunkter = underytende
            .Where(p => p.DeltaEtaPp <= -2.0) // matcher default-terskel; aggregering låst på det
            .ToList();

        // Tell hvor mange episoder som rører hver bin.
        var episoderPerBaand = new Dictionary<double, HashSet<int>>();
        for (var ei = 0; ei < episoder.Count; ei++)
        {
            var ep = episoder[ei];
            var startBin = Math.Floor(ep.EffektMinKw / EffektivitetQueryService.PowerBinKw)
                * EffektivitetQueryService.PowerBinKw;
            var sluttBin = Math.Floor(ep.EffektMaksKw / EffektivitetQueryService.PowerBinKw)
                * EffektivitetQueryService.PowerBinKw;
            for (var b = startBin; b <= sluttBin; b += EffektivitetQueryService.PowerBinKw)
            {
                if (!episoderPerBaand.TryGetValue(b, out var set))
                {
                    set = new HashSet<int>();
                    episoderPerBaand[b] = set;
                }
                set.Add(ei);
            }
        }

        var perBaand = underytendePunkter
            .GroupBy(p => p.BinStartKw)
            .Select(g =>
            {
                var taptMwh = 0.0;
                var taptNok = 0.0;
                var sumDelta = 0.0;
                foreach (var p in g)
                {
                    var faktiskKwh = p.Punkt.EffektKw * intervallTimer;
                    if (p.Punkt.EtaPct > 0)
                    {
                        var taptKwh = faktiskKwh * (p.ReferanseEtaPct - p.Punkt.EtaPct) / p.Punkt.EtaPct;
                        var taptMwhI = taptKwh / 1000.0;
                        taptMwh += taptMwhI;
                        if (priser is not null && priser.TryGetValue(ToHourBucket(p.Punkt.TimeUtc), out var pris))
                        {
                            taptNok += taptMwhI * pris;
                        }
                    }
                    sumDelta += p.DeltaEtaPp;
                }
                return new EffektBaandAggregat(
                    EffektKwStart: g.Key,
                    EffektKwSlutt: g.Key + EffektivitetQueryService.PowerBinKw,
                    AntallEpisoder: episoderPerBaand.TryGetValue(g.Key, out var s) ? s.Count : 0,
                    TotalVarighetTimer: g.Count() * intervallTimer,
                    SnittDeltaEtaPp: sumDelta / g.Count(),
                    TaptMwh: taptMwh,
                    TaptNok: taptNok);
            })
            .OrderByDescending(b => b.TaptNok > 0 ? b.TaptNok : b.TaptMwh * 1000.0)
            .ToList();
        return perBaand;
    }

    private static DateTimeOffset ToHourBucket(DateTimeOffset t)
        => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);

    /// <summary>Interno-pakke: Genuine-punkt med utregnet Δη og bin-tilhørighet.</summary>
    private sealed record EvaluertPunkt(
        EffektivitetPunkt Punkt,
        double BinStartKw,
        double ReferanseEtaPct,
        double DeltaEtaPp);
}
