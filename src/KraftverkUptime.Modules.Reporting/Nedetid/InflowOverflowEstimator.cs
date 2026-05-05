namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Pure-logic estimator for "ville-vært-overløp" basert på tilsig + ledig
/// magasin-kapasitet. Brukes som tilsig-basert alternativ til den direkte
/// SCADA-overflow-deteksjonen (som er sårbar for sensor-glitches).
///
/// Modell:
///   tilsig(t)           ≈ ΔVolum(t)/Δt + utløp(t)            (m³/s)
///   utløp(t)            = TotalDamFlow(t) + TurbineFlow(t)   (m³/s)
///   ledig_kapasitet     = (1 − fyllgrad) × max_volum_m3      (m³)
///   netto_inn_uten_drift = tilsig_snitt − total_dam_flow_uten_turbin (m³/s)
///   tid_til_fullt       = ledig_kapasitet / (netto_inn × 3600) (timer)
///
///   ville_overlop_timer = max(0, vindu_h − tid_til_fullt)
///
/// Antar at i counterfactual-perioden:
///   - turbinen er AV (TurbineFlow = 0)
///   - tilsig holder seg ≈ snittet av siste lookback-timer
///   - dam-luker holdes som de var (TotalDamFlow uten turbin)
///
/// Hvis netto_inn ≤ 0 (mer ut enn inn) → ingen overlap mulig.
/// Hvis ledig_kapasitet ≤ 0 (allerede fullt) → overlop fra time 0.
/// </summary>
public static class InflowOverflowEstimator
{
    /// <summary>
    /// Time-aggregert sample-rad. Verdier kan være null (manglende SCADA-data
    /// for den timen) — estimator filtrerer dem ut.
    /// </summary>
    public sealed record HourlySample(
        DateTimeOffset HourUtc,
        double? VolumeM3,
        double? TotalDamFlowM3PerS,
        double? TurbineFlowM3PerS);

    /// <summary>Resultat av estimat for ett vindu.</summary>
    /// <param name="EstimatedOverflowHours">Antall timer i [from, to) der modellen sier magasinet ville vært fullt.</param>
    /// <param name="EstimatedInflowM3PerS">Snitt-tilsig fra lookback-perioden (m³/s).</param>
    /// <param name="FreeCapacityAtStartM3">Ledig kapasitet ved vindu-start (m³).</param>
    /// <param name="HoursToFull">Estimert timer til magasinet er fullt; double.PositiveInfinity hvis netto_inn ≤ 0.</param>
    /// <param name="DataAvailable">False hvis vi ikke hadde nok samples til å beregne et meningsfullt estimat.</param>
    /// <param name="Forklaring">Menneskelig forklaring av regne-stega.</param>
    public sealed record EstimateResult(
        double EstimatedOverflowHours,
        double EstimatedInflowM3PerS,
        double FreeCapacityAtStartM3,
        double HoursToFull,
        bool DataAvailable,
        string Forklaring);

    /// <summary>
    /// Beregner "ville-overflow"-timer for vinduet [<paramref name="windowFrom"/>, <paramref name="windowTo"/>).
    /// </summary>
    /// <param name="lookbackSamples">Sample-rader for ≥ 6 timer før vinduet. Brukes til å estimere snitt-tilsig.</param>
    /// <param name="windowFrom">Start på counterfactual-vinduet.</param>
    /// <param name="windowTo">Slutt på counterfactual-vinduet.</param>
    /// <param name="maxVolumeM3">
    /// Maks magasin-volum i m³ (ved 100 % fyllgrad). Hvis ukjent, send 0 — da
    /// returnerer estimator <see cref="EstimateResult.DataAvailable"/>=false.
    /// </param>
    /// <param name="fillRateAtStart">
    /// Fyllgrad ved vindu-start (0..1). Hvis ukjent, send null — da brukes
    /// volum-derivativet alene og overflow-estimat hopper over.
    /// </param>
    public static EstimateResult Estimate(
        IReadOnlyList<HourlySample> lookbackSamples,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        double maxVolumeM3,
        double? fillRateAtStart)
    {
        ArgumentNullException.ThrowIfNull(lookbackSamples);
        if (windowTo <= windowFrom)
        {
            return new EstimateResult(0, 0, 0, double.PositiveInfinity, false,
                "Tomt vindu (to ≤ from).");
        }

        var windowHours = (windowTo - windowFrom).TotalHours;

        // 1) Estimer snitt-tilsig fra lookback-samples.
        // tilsig per time ≈ ΔVolum/Δt + TotalDamFlow + TurbineFlow
        // (alle kilder ut av magasinet + endring i lagret volum = total inn)
        var sortedLookback = lookbackSamples
            .Where(s => s.VolumeM3.HasValue)
            .OrderBy(s => s.HourUtc)
            .ToList();

        if (sortedLookback.Count < 2)
        {
            return new EstimateResult(0, 0, 0, double.PositiveInfinity, false,
                $"For få volum-samples i lookback ({sortedLookback.Count}). Trenger minst 2.");
        }

        double inflowSum = 0;
        var inflowSamples = 0;
        for (var i = 1; i < sortedLookback.Count; i++)
        {
            var prev = sortedLookback[i - 1];
            var cur = sortedLookback[i];
            var dtHours = (cur.HourUtc - prev.HourUtc).TotalHours;
            if (dtHours <= 0) continue;

            var dvM3 = cur.VolumeM3!.Value - prev.VolumeM3!.Value;
            var dvPerSecond = dvM3 / (dtHours * 3600.0);

            // Utløp per time: TotalDamFlow + TurbineFlow (snitt av endepunktene
            // hvis begge har verdi, ellers den som finnes; null = 0).
            var totDam = AvgOrZero(prev.TotalDamFlowM3PerS, cur.TotalDamFlowM3PerS);
            var turb = AvgOrZero(prev.TurbineFlowM3PerS, cur.TurbineFlowM3PerS);
            var outflow = totDam + turb;

            // Tilsig = endring + ut.
            inflowSum += dvPerSecond + outflow;
            inflowSamples++;
        }

        if (inflowSamples == 0)
        {
            return new EstimateResult(0, 0, 0, double.PositiveInfinity, false,
                "Kunne ikke beregne tilsig fra lookback (manglende intervaller).");
        }

        var avgInflow = inflowSum / inflowSamples;

        // 2) Snitt-utløp (uten turbin — turbinen er av i counterfactual).
        var avgTotalDamFlow = sortedLookback
            .Where(s => s.TotalDamFlowM3PerS.HasValue)
            .Select(s => s.TotalDamFlowM3PerS!.Value)
            .DefaultIfEmpty(0)
            .Average();

        var nettoInnUtenDrift = avgInflow - avgTotalDamFlow;

        // 3) Ledig kapasitet ved vindu-start.
        if (maxVolumeM3 <= 0 || !fillRateAtStart.HasValue)
        {
            return new EstimateResult(
                EstimatedOverflowHours: 0,
                EstimatedInflowM3PerS: avgInflow,
                FreeCapacityAtStartM3: 0,
                HoursToFull: double.PositiveInfinity,
                DataAvailable: false,
                Forklaring: $"Snitt-tilsig {avgInflow:F2} m³/s, men ukjent maks-volum/fyllgrad. Kan ikke estimere overflow-tid.");
        }

        var freeCapacity = (1.0 - fillRateAtStart.Value) * maxVolumeM3;

        // 4) Timer til fullt magasin.
        if (nettoInnUtenDrift <= 0)
        {
            return new EstimateResult(0, avgInflow, freeCapacity, double.PositiveInfinity, true,
                $"Snitt-tilsig {avgInflow:F2} m³/s er ≤ utløp {avgTotalDamFlow:F2} m³/s. " +
                "Magasinet ville ikke fylles opp i counterfactual — ingen estimert overflow.");
        }

        var hoursToFull = freeCapacity / (nettoInnUtenDrift * 3600.0);
        var estimatedOverflowHours = Math.Max(0, windowHours - hoursToFull);

        var forklaring =
            $"Snitt-tilsig {avgInflow:F2} m³/s, ledig {freeCapacity / 1_000_000:F2} Mill.m³. " +
            $"Tid til fullt: {hoursToFull:F1} t (vinduet er {windowHours:F1} t). " +
            $"Estimert overflow: {estimatedOverflowHours:F1} t.";

        return new EstimateResult(
            EstimatedOverflowHours: estimatedOverflowHours,
            EstimatedInflowM3PerS: avgInflow,
            FreeCapacityAtStartM3: freeCapacity,
            HoursToFull: hoursToFull,
            DataAvailable: true,
            Forklaring: forklaring);
    }

    private static double AvgOrZero(double? a, double? b)
    {
        if (a.HasValue && b.HasValue) return (a.Value + b.Value) / 2.0;
        if (a.HasValue) return a.Value;
        if (b.HasValue) return b.Value;
        return 0;
    }
}
