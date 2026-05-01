namespace KraftverkUptime.Modules.Reporting.Produksjon;

/// <summary>
/// Pure-funksjon kalkulator for produksjons-analyse. Tar timesrader (Plan,
/// Elhub, Spotpris) og produserer aggregat + per-måneds-fordeling. Ingen DB-
/// eller IO-tilgang — caller leverer ferdig data.
///
/// Brukes av <see cref="IProduksjonAnalyseService"/>.
/// </summary>
public static class ProduksjonAnalyseCalculator
{
    public sealed record HourlyInput(
        DateTimeOffset TimeUtc,
        double? PlanMwh,
        double? ElhubMwh,
        double? SpotprisNokMwh);

    public static ProduksjonAnalyseResult Compute(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<HourlyInput> hours)
    {
        ArgumentNullException.ThrowIfNull(hours);

        var (planTreff, andelTopp, andelBunn, hgMerverdi, faktiskMerverdi, snittSpot,
             totalElhub, totalPlan, antTimerMedPlan, antTimerProduksjon)
            = ComputeAggregate(hours);

        // Per-måned: re-bruker samme aggregat-funksjon på filtrert delmengde
        var monthly = hours
            .GroupBy(h => new { h.TimeUtc.Year, h.TimeUtc.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var rows = g.ToList();
                var (mt, mTopp, _, mHg, mFaktisk, mSnittSpot, mElhub, mPlan, _, mProd)
                    = ComputeAggregate(rows);
                return new ProduksjonMonthly(
                    Year: g.Key.Year,
                    Month: g.Key.Month,
                    ElhubMwh: mElhub,
                    PlanMwh: mPlan,
                    AntallTimerProduksjon: mProd,
                    PlanTreffProsent: mt,
                    AndelProdIToppKvartil: mTopp,
                    HydrogridMerverdiNok: mHg,
                    FaktiskMerverdiNok: mFaktisk,
                    SnittSpotprisNokMwh: mSnittSpot);
            })
            .ToList();

        var hourlyOutput = hours
            .Select(h => new ProduksjonHourlyPoint(
                TimeUtc: h.TimeUtc,
                PlanMwh: h.PlanMwh,
                ElhubMwh: h.ElhubMwh,
                SpotprisNokMwh: h.SpotprisNokMwh))
            .ToList();

        return new ProduksjonAnalyseResult(
            PlantId: plantId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            AntallTimer: hours.Count,
            AntallTimerMedPlan: antTimerMedPlan,
            AntallTimerProduksjon: antTimerProduksjon,
            TotalElhubMwh: totalElhub,
            TotalPlanMwh: totalPlan,
            PlanTreffProsent: planTreff,
            AndelProdIToppKvartil: andelTopp,
            AndelProdIBunnKvartil: andelBunn,
            HydrogridMerverdiNok: hgMerverdi,
            FaktiskMerverdiNok: faktiskMerverdi,
            SnittSpotprisNokMwh: snittSpot,
            Hourly: hourlyOutput,
            Monthly: monthly);
    }

    private static (
        double planTreff, double andelTopp, double andelBunn,
        double hgMerverdi, double faktiskMerverdi, double snittSpot,
        double totalElhub, double totalPlan, int antTimerMedPlan,
        int antTimerProduksjon)
        ComputeAggregate(IReadOnlyList<HourlyInput> rows)
    {
        if (rows.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        // 1. Plan-treff: 1 - Σ|Elhub - Plan| / Σ|Plan| for timer med Plan > 0
        // Tell også produksjonstimer (Elhub > 0) — høy verdi i kombinasjon med
        // lav snittpris er en sterk overløp-risiko-indikator (vi måtte produsere
        // for å unngå at magasinet flommet over).
        double sumAbsAvvik = 0, sumPlan = 0, sumElhub = 0;
        var antTimerMedPlan = 0;
        var antTimerProduksjon = 0;
        foreach (var r in rows)
        {
            if (r.PlanMwh.HasValue && r.ElhubMwh.HasValue && r.PlanMwh.Value > 0)
            {
                sumAbsAvvik += Math.Abs(r.ElhubMwh.Value - r.PlanMwh.Value);
                sumPlan += r.PlanMwh.Value;
                antTimerMedPlan++;
            }
            if (r.ElhubMwh.HasValue)
            {
                sumElhub += r.ElhubMwh.Value;
                if (r.ElhubMwh.Value > 0) antTimerProduksjon++;
            }
        }
        var planTreff = sumPlan > 0
            ? Math.Clamp(1.0 - sumAbsAvvik / sumPlan, 0.0, 1.0)
            : 0;

        // 2. Andel produksjon i topp-kvartil av spot
        // Sortér timer på spot, plukk topp/bunn-25 % av timene, summer Elhub i de
        var medSpot = rows.Where(r => r.SpotprisNokMwh.HasValue).ToList();
        double andelTopp = 0, andelBunn = 0, snittSpot = 0;
        if (medSpot.Count > 0)
        {
            snittSpot = medSpot.Average(r => r.SpotprisNokMwh!.Value);
            var sortertEtterSpot = medSpot.OrderBy(r => r.SpotprisNokMwh!.Value).ToList();
            var kvartilSize = Math.Max(1, sortertEtterSpot.Count / 4);
            // Topp 25 % = de siste i sortert liste
            var toppTimer = sortertEtterSpot.TakeLast(kvartilSize).ToHashSet();
            var bunnTimer = sortertEtterSpot.Take(kvartilSize).ToHashSet();
            var elhubITopp = toppTimer.Sum(r => r.ElhubMwh ?? 0);
            var elhubIBunn = bunnTimer.Sum(r => r.ElhubMwh ?? 0);
            var totalElhubMedSpot = medSpot.Sum(r => r.ElhubMwh ?? 0);
            andelTopp = totalElhubMedSpot > 0 ? elhubITopp / totalElhubMedSpot : 0;
            andelBunn = totalElhubMedSpot > 0 ? elhubIBunn / totalElhubMedSpot : 0;
        }

        // 3. Hydrogrid-merverdi vs. flat baseline:
        // Smart timing-bidrag = Σ(MWh × spot) - Σ(MWh) × snitt_spot
        // Hvis planen produserer mest når prisen er høy, blir bidraget positivt.
        double hgMerverdi = 0, faktiskMerverdi = 0;
        double sumPlanMwhMedSpot = 0, sumElhubMedSpot = 0;
        double sumPlanRevenue = 0, sumElhubRevenue = 0;
        foreach (var r in rows)
        {
            if (!r.SpotprisNokMwh.HasValue) continue;
            var spot = r.SpotprisNokMwh.Value;
            if (r.PlanMwh.HasValue && r.PlanMwh.Value > 0)
            {
                sumPlanMwhMedSpot += r.PlanMwh.Value;
                sumPlanRevenue += r.PlanMwh.Value * spot;
            }
            if (r.ElhubMwh.HasValue && r.ElhubMwh.Value > 0)
            {
                sumElhubMedSpot += r.ElhubMwh.Value;
                sumElhubRevenue += r.ElhubMwh.Value * spot;
            }
        }
        if (medSpot.Count > 0)
        {
            // Flat baseline: hva hadde inntekten vært hvis all MWh ble fordelt
            // jevnt over alle timer i perioden? = total_mwh × snitt_spot
            hgMerverdi = sumPlanRevenue - sumPlanMwhMedSpot * snittSpot;
            faktiskMerverdi = sumElhubRevenue - sumElhubMedSpot * snittSpot;
        }

        return (planTreff, andelTopp, andelBunn, hgMerverdi, faktiskMerverdi,
                snittSpot, sumElhub, sumPlan, antTimerMedPlan, antTimerProduksjon);
    }
}
