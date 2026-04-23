using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Classification.Kpi;

/// <summary>
/// Beregner IEEE 762 / NERC GADS KPI-katalogen fra klassifiserte timer.
///
/// Samsvarer eksakt med Python-referansen (<c>drivdal-analyse/src/kpi.py</c>) slik at
/// <c>drivdal-feb2025-fasit.json</c> kan brukes som regresjonstest.
///
/// <para>Beregningsgrupper:</para>
/// <list type="bullet">
///   <item>Tidsbaserte: SH, AH, UH, FOH, IU, RU, AF, AF_SystemView, SF, FOR, EFDH, EAF</item>
///   <item>Energibaserte: TotalProduction, CF, OF</item>
///   <item>Hendelsesbaserte: ForcedOutageEvents, MTBF, MTTR</item>
///   <item>Plan/marked: PlanTotal, SpotbudTotal, PlanFulfillment, BidAccuracy,
///         PlanToBidDeviation, PlanDeviation_MWh, PlanDeviation_NOK,
///         ImbalanceResult_NOK, ImbalanceCorrelation</item>
/// </list>
///
/// <para>Merk: OMC (outside management control) = <c>ResourceUnavailable</c> i v1.
/// IU (<c>InformationUnavailable</c>) har egen kategori – ekskluderes fra nevnere
/// i "Unit Performance"-indeksene men inkluderes i "System Reliability".</para>
/// </summary>
public sealed class UptimeKpiCalculator
{
    public UptimeReport Compute(IReadOnlyList<ClassifiedHourlyRow> classified, PlantClassificationConfig plant)
    {
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(plant);

        var ph = classified.Count;
        var avgConf = ph == 0 ? 0.0 : classified.Average(c => c.Confidence);

        var stateCounts = classified
            .GroupBy(c => c.State)
            .ToDictionary(g => g.Key, g => g.Count());

        int sh = stateCounts.GetValueOrDefault(UnitState.InService);
        int rs = stateCounts.GetValueOrDefault(UnitState.ReserveShutdown);
        int ah = sh + rs;
        int poh = stateCounts.GetValueOrDefault(UnitState.PlannedOutage);
        int moh = stateCounts.GetValueOrDefault(UnitState.MaintenanceOutage);
        int foh = stateCounts.GetValueOrDefault(UnitState.ForcedOutage);
        int fdh = stateCounts.GetValueOrDefault(UnitState.ForcedDerating);
        int ruh = stateCounts.GetValueOrDefault(UnitState.ResourceUnavailable);
        int iuh = stateCounts.GetValueOrDefault(UnitState.InformationUnavailable);
        int uh = poh + moh + foh;

        int effectiveHours = ph - ruh - iuh;

        var kpis = new List<KpiResult>();

        // --- Tidsbaserte ---
        kpis.Add(new("ServiceHours_SH", sh, "hours", ph, 1.0, "time",
            "Antall timer i InService-tilstand"));
        kpis.Add(new("AvailableHours_AH", ah, "hours", ph, 1.0, "time",
            "SH + ReserveShutdownHours (timer verket er tilgjengelig)"));
        kpis.Add(new("UnavailableHours_UH", uh, "hours", ph, 1.0, "time",
            "POH + MOH + FOH"));
        kpis.Add(new("ForcedOutageHours_FOH", foh, "hours", ph, 1.0, "time",
            "Timer i ForcedOutage"));
        kpis.Add(new("InformationUnavailable_Hours", iuh, "hours", ph, 1.0, "time",
            "Timer uten data"));
        kpis.Add(new("ResourceUnavailable_Hours", ruh, "hours", ph, 1.0, "time",
            "Timer med ressursbegrensning (OMC)"));

        kpis.Add(new("AvailabilityFactor_AF",
            SafeRatio(ah, effectiveHours), "ratio", effectiveHours, avgConf, "time",
            "AF = AH / (PH − OMC − IU). Unit Performance Index."));
        kpis.Add(new("AvailabilityFactor_AF_SystemView",
            SafeRatio(ah, ph - iuh), "ratio", ph - iuh, avgConf, "time",
            "AF inkludert OMC i nevner. System Reliability Index."));
        kpis.Add(new("ServiceFactor_SF",
            SafeRatio(sh, effectiveHours), "ratio", effectiveHours, avgConf, "time",
            "SF = SH / effektive timer"));
        kpis.Add(new("ForcedOutageRate_FOR",
            SafeRatio(foh, foh + sh), "ratio", foh + sh, avgConf, "time",
            "FOR = FOH / (FOH + SH)"));

        // EFDH: Σ (Plan - Elhub) / Pnom for FD-timer, clampet til ≥ 0
        double efdh = 0.0;
        if (plant.NominalPowerMw > 0)
        {
            foreach (var row in classified)
            {
                if (row.State != UnitState.ForcedDerating)
                {
                    continue;
                }
                var plan = row.ProduksjonplanMwh ?? plant.NominalPowerMw;
                var actual = row.MwhElhub ?? 0.0;
                var delta = Math.Max(0.0, plan - actual);
                efdh += delta / plant.NominalPowerMw;
            }
            kpis.Add(new("EquivalentForcedDerated_Hours", efdh, "hours", ph, avgConf, "time",
                "EFDH = Σ (Plan − Elhub) / Pnom for FD-timer"));
            kpis.Add(new("EquivalentAvailabilityFactor_EAF",
                SafeRatio(ah - efdh, effectiveHours), "ratio", effectiveHours, avgConf, "time",
                "EAF = (AH − EFDH) / effektive timer"));
        }

        // --- Energi ---
        double totalMwh = classified.Sum(c => c.MwhElhub ?? 0);
        double maxPossibleMwh = plant.NominalPowerMw * ph;
        kpis.Add(new("TotalProduction_MWh", totalMwh, "MWh", ph, 1.0, "energy",
            "Σ MWh-Elhub"));
        kpis.Add(new("CapacityFactor_CF",
            SafeRatio(totalMwh, maxPossibleMwh), "ratio", ph, avgConf, "energy",
            $"CF = Σ Elhub / (Pnom × PH), Pnom={plant.NominalPowerMw} MW"));
        kpis.Add(new("OutputFactor_OF",
            SafeRatio(totalMwh, plant.NominalPowerMw * sh), "ratio", sh, avgConf, "energy",
            "OF = Σ Elhub / (Pnom × SH)"));

        // --- Hendelser ---
        int foEvents = CountRuns(classified, UnitState.ForcedOutage);
        double? mtbf = foEvents > 0 ? SafeRatio(sh, foEvents) : null;
        double? mttr = foEvents > 0 ? SafeRatio(foh, foEvents) : null;
        kpis.Add(new("ForcedOutageEvents", foEvents, "events", ph, avgConf, "event",
            "Antall sammenhengende ForcedOutage-hendelser"));
        kpis.Add(new("MTBF", mtbf, "hours", ph, avgConf, "event",
            "Mean Time Between Failures = SH / FO-hendelser"));
        kpis.Add(new("MTTR", mttr, "hours", ph, avgConf, "event",
            "Mean Time To Repair = FOH / FO-hendelser"));

        // --- Plan/marked ---
        double planTotal = classified.Sum(c => c.ProduksjonplanMwh ?? 0);
        double bidTotal = classified.Sum(c => c.Row.SpotbudMwh ?? 0);

        kpis.Add(new("PlanTotal_MWh", planTotal, "MWh", ph, 1.0, "plan",
            "Σ Produksjonplan"));
        kpis.Add(new("SpotbudTotal_MWh", bidTotal, "MWh", ph, 1.0, "plan",
            "Σ Spotbud (Day-Ahead)"));
        kpis.Add(new("PlanFulfillment",
            SafeRatio(totalMwh, planTotal), "ratio", ph, 1.0, "plan",
            "Elhub / Plan – andel av planlagt produksjon som ble levert"));
        kpis.Add(new("BidAccuracy",
            bidTotal > 0 ? SafeRatio(totalMwh, bidTotal) : null, "ratio", ph, 1.0, "plan",
            "Elhub / Spotbud – andel av markedsforpliktelse som ble levert"));
        kpis.Add(new("PlanToBidDeviation",
            planTotal > 0 ? SafeRatio(planTotal - bidTotal, planTotal) : null, "ratio", ph, 1.0, "plan",
            "(Plan − Bud) / Plan"));
        kpis.Add(new("PlanDeviation_MWh", totalMwh - planTotal, "MWh", ph, 1.0, "plan",
            "Elhub − Plan"));

        double planDevNok = 0.0;
        foreach (var row in classified)
        {
            var dev = (row.MwhElhub ?? 0) - (row.ProduksjonplanMwh ?? 0);
            var spot = row.SpotprisNokMwh ?? 0;
            planDevNok += dev * spot;
        }
        kpis.Add(new("PlanDeviation_NOK", planDevNok, "NOK", ph, 1.0, "plan",
            "Σ (Elhub − Plan) × Spotpris"));

        // Imbalance-aggregater
        double imbalanceNok = classified.Sum(c => c.Row.UbalanseResultatNok ?? 0);
        kpis.Add(new("ImbalanceResult_NOK", imbalanceNok, "NOK", ph, 1.0, "plan",
            "Σ Tap/gevinst ubalanse eks. gebyr"));

        var corrPairs = classified
            .Where(c => c.Row.AbsUbalansevolumMwh.HasValue && c.Row.RkPrisNokMwh.HasValue)
            .Select(c => (x: c.Row.AbsUbalansevolumMwh!.Value, y: c.Row.RkPrisNokMwh!.Value))
            .ToList();
        double? correlation = corrPairs.Count > 1 ? Pearson(corrPairs) : null;
        kpis.Add(new("ImbalanceCorrelation_AbsVol_RKPris", correlation,
            "correlation", corrPairs.Count, 1.0, "plan",
            "Pearson-korrelasjon mellom |ubalansevolum| og regulerkraftpris"));

        var periodStart = classified.Count == 0 ? DateTimeOffset.MinValue : classified.Min(c => c.TimeUtc);
        var periodEnd = classified.Count == 0 ? DateTimeOffset.MinValue : classified.Max(c => c.TimeUtc);

        return new UptimeReport
        {
            PlantId = plant.PlantId,
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            PeriodHours = ph,
            StateCounts = stateCounts,
            Classified = classified,
            Kpis = kpis,
        };
    }

    // -- Hjelpere --
    private static double? SafeRatio(double num, double den)
    {
        if (den == 0 || double.IsNaN(den)) return null;
        return num / den;
    }

    private static int CountRuns(IReadOnlyList<ClassifiedHourlyRow> classified, UnitState target)
    {
        int count = 0;
        bool prev = false;
        foreach (var row in classified)
        {
            var isTarget = row.State == target;
            if (isTarget && !prev)
            {
                count++;
            }
            prev = isTarget;
        }
        return count;
    }

    private static double Pearson(List<(double x, double y)> pairs)
    {
        int n = pairs.Count;
        double sumX = 0, sumY = 0;
        for (int i = 0; i < n; i++) { sumX += pairs[i].x; sumY += pairs[i].y; }
        double meanX = sumX / n, meanY = sumY / n;
        double num = 0, denX = 0, denY = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = pairs[i].x - meanX;
            double dy = pairs[i].y - meanY;
            num += dx * dy;
            denX += dx * dx;
            denY += dy * dy;
        }
        if (denX == 0 || denY == 0) return 0.0;
        return num / Math.Sqrt(denX * denY);
    }
}
