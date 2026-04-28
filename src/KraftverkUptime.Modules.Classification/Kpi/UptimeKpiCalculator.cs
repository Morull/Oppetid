using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Classification.Kpi;

/// <summary>
/// Forenklet KPI-katalog organisert i tre familier:
///
/// <list type="bullet">
///   <item><b>drift</b> — hvordan gikk anlegget rent operasjonelt:
///     ServiceHours, ForcedOutageHours, OutOfServiceHours, NoDataHours,
///     AvailabilityFactor.</item>
///   <item><b>marked</b> — hvor godt traff vi day-ahead-budet vårt:
///     BidDelivery, BidVolume, ProductionVolume.</item>
///   <item><b>okonomi</b> — kroner og øre per periode:
///     Spotomsetning, RkNetto, Ubalanseresultat, Oppgjor, og brutto
///     RK-kjøp/-salg som synlighet.</item>
/// </list>
///
/// Sammenlignet med forrige modell er disse fjernet:
/// <list type="bullet">
///   <item>AvailableHours_AH (slått sammen — vi skiller bare drift / ikke-drift)</item>
///   <item>UnavailableHours_UH (overflødig med FOH som primær)</item>
///   <item>AF_SystemView, ServiceFactor, ForcedOutageRate (derivat av AF)</item>
///   <item>EFDH, EAF (derating-konseptet er borte)</item>
///   <item>CapacityFactor, OutputFactor (passer ikke vannkraft)</item>
///   <item>ForcedOutageEvents, MTBF, MTTR (event-baserte; krever annoteringer
///         for å skille planlagt vs uvarslet på event-nivå)</item>
///   <item>PlanFulfillment, PlanDeviation, ImbalanceCorrelation (Plan-baserte;
///         vi bruker Spotbud nå)</item>
/// </list>
///
/// Disse kan introduseres igjen når annotering-funksjonen gir oss bedre
/// data å regne på.
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
        int foh = stateCounts.GetValueOrDefault(UnitState.ForcedOutage);
        int oos = stateCounts.GetValueOrDefault(UnitState.ReserveShutdown);
        int iuh = stateCounts.GetValueOrDefault(UnitState.InformationUnavailable);

        var kpis = new List<KpiResult>();

        // -------------------------------------------------------------------
        // DRIFT — kjørte anlegget?
        // -------------------------------------------------------------------
        kpis.Add(new("ServiceHours_SH", sh, "hours", ph, 1.0, "drift",
            "Antall timer i drift (Elhub > 0)"));
        kpis.Add(new("ForcedOutageHours_FOH", foh, "hours", ph, 1.0, "drift",
            "Timer hvor verket var forpliktet (Spotbud > 0) men ikke produserte (Elhub = 0)"));
        kpis.Add(new("OutOfServiceHours", oos, "hours", ph, 1.0, "drift",
            "Timer uten produksjon og uten markedsforpliktelse (kan være planlagt stans, vannmangel, etc.)"));
        kpis.Add(new("InformationUnavailable_Hours", iuh, "hours", ph, 1.0, "drift",
            "Timer uten gyldige data (manglende eller negativ Elhub)"));
        kpis.Add(new("AvailabilityFactor_AF",
            SafeRatio(sh, sh + foh), "ratio", sh + foh, avgConf, "drift",
            "AF = SH / (SH + FOH). Andel av forpliktede timer hvor verket faktisk leverte."));

        // -------------------------------------------------------------------
        // MARKED — leverte vi det vi lovet day-ahead?
        // -------------------------------------------------------------------
        double totalProduction = classified.Sum(c => c.Row.MwhElhub ?? 0);
        double totalBid = classified.Sum(c => c.Row.SpotbudMwh ?? 0);
        double committedBid = classified
            .Where(c => (c.Row.SpotbudMwh ?? 0) > 0)
            .Sum(c => c.Row.SpotbudMwh!.Value);
        double productionInCommittedHours = classified
            .Where(c => (c.Row.SpotbudMwh ?? 0) > 0)
            .Sum(c => c.Row.MwhElhub ?? 0);

        kpis.Add(new("BidVolume_MWh", totalBid, "MWh", ph, 1.0, "marked",
            "Σ Spotbud — total volumforpliktelse mot day-ahead-markedet"));
        kpis.Add(new("BidDelivery",
            SafeRatio(productionInCommittedHours, committedBid), "ratio", ph, 1.0, "marked",
            "Σ Elhub / Σ Spotbud, kun timer der Spotbud > 0. Hvor godt vi leverte det vi lovte."));

        // -------------------------------------------------------------------
        // ØKONOMI — netto resultat per periode
        // -------------------------------------------------------------------
        // Sign-konvensjon i kildefila: positivt = penger inn til oss,
        // negativt = penger ut. Netto er derfor en enkel sum av de signede
        // beløpene.
        double spotomsetning = classified.Sum(c => c.Row.SpotomsetningNok ?? 0);
        double rkKjop = classified.Sum(c => c.Row.RkKjopNok ?? 0);   // typisk negativ (kostnad)
        double rkSalg = classified.Sum(c => c.Row.RkSalgNok ?? 0);   // typisk positiv (inntekt)
        double ubalanseResultat = classified.Sum(c => c.Row.UbalanseResultatNok ?? 0);
        double esettVolumgebyr = classified.Sum(c => c.Row.ESettVolumgebyrNok ?? 0);  // negativ
        double esettUbalansegebyr = classified.Sum(c => c.Row.ESettUbalansegebyrNok ?? 0);  // negativ
        double oppgjorTotal = classified.Sum(c => c.Row.OppgjorNok ?? 0);

        // Ubalansekost = netto økonomisk effekt av å ikke ha levert Spotbud
        // presis, sammenlignet med perfekt day-ahead-leveranse.
        //
        // Counterfactual: hadde vi levert budet eksakt, ville Spotomsetning
        // vært våre eneste handelspenger, og alle RK/ubalanse-postene ville
        // vært null. Vi får uansett betalt Spotomsetning for budvolumet —
        // også for MWh vi senere må kjøpe tilbake via RK. Det betyr at den
        // relevante kostnaden for under-leveranse er IKKE brutto RK-kjøp,
        // men mellomlegget RK-pris vs Spotpris × volum (siden vi allerede
        // har fått spotpris for de MWh-ene gjennom Spotomsetning).
        //
        // Det netto mellomlegget er nettopp <c>Ubalanseresultat_NOK</c>
        // ("Tap/gevinst ubalanse eks. gebyr") fra kildedata — som per
        // definisjon allerede er netted mot spot. Identitet:
        //   Ubalanseresultat ≡ RkSalgVsSpot + RkKjopVsSpot
        //
        // Ubalansekost er derfor: netto ubalanseresultat + eSett-gebyrene
        // (som er rene rene kostnader uavhengig av spot-baselinet).
        double ubalansekost = ubalanseResultat + esettVolumgebyr + esettUbalansegebyr;

        // RK-vs-Spot strategiske marginer:
        // For hver time hvor vi tradet via RK, sammenlign mot å ha tradet samme
        // volum på spot. AbsUbalansevolumMwh gir volumet, retning bestemmes av
        // hvilken NOK-kolonne som er ikke-null den timen.
        double rkSalgVsSpot = 0;
        double rkKjopVsSpot = 0;
        foreach (var c in classified)
        {
            var spot = c.Row.SpotprisNokMwh;
            var vol = c.Row.AbsUbalansevolumMwh;
            if (!spot.HasValue || !vol.HasValue || vol.Value == 0)
            {
                continue;
            }

            var salgRow = c.Row.RkSalgNok ?? 0;
            var kjopRow = c.Row.RkKjopNok ?? 0;

            if (salgRow > 0)
            {
                // Vi solgte vol MWh via RK. Spot-ekvivalent inntekt = vol × spotpris.
                // Differanse = faktisk RK-inntekt − spot-ekvivalent.
                rkSalgVsSpot += salgRow - vol.Value * spot.Value;
            }
            if (kjopRow != 0)
            {
                // Vi kjøpte vol MWh via RK (kjopRow er negativ).
                // Spot-ekvivalent kostnad signed = −vol × spotpris.
                // Differanse = faktisk RK-kostnad − spot-ekvivalent kostnad.
                rkKjopVsSpot += kjopRow - (-vol.Value * spot.Value);
            }
        }

        kpis.Add(new("TotalProduction_MWh", totalProduction, "MWh", ph, 1.0, "okonomi",
            "Σ MWh-Elhub — total produsert energi i perioden"));
        kpis.Add(new("Spotomsetning_NOK", spotomsetning, "NOK", ph, 1.0, "okonomi",
            "Σ Spotomsetning — bruttoinntekt fra day-ahead-handel (basis-inntekt for budet)"));
        kpis.Add(new("Ubalansekost_NOK", ubalansekost, "NOK", ph, 1.0, "okonomi",
            "Netto økonomisk effekt av å avvike fra Spotbud (Ubalanseresultat + eSett-gebyrer). Spotomsetning er allerede betalt for budvolumet, så kun mellomlegget RK vs Spot per ubalansert MWh er den reelle kostnaden — ikke brutto RK-flyten. Negativ = nettokostnad. Positiv = vi tjente på avviket."));
        kpis.Add(new("RkSalgVsSpot_NOK", rkSalgVsSpot, "NOK", ph, 1.0, "okonomi",
            "Mer-/mindreinntekt fra RK-salg sammenlignet med å selge samme volum dayahead til spotpris. Positiv = RK gav høyere snittpris enn spot — dvs det lønte seg å produsere over budet og selge overskytende på RK."));
        kpis.Add(new("RkKjopVsSpot_NOK", rkKjopVsSpot, "NOK", ph, 1.0, "okonomi",
            "Mer-/mindrekostnad ved RK-kjøp sammenlignet med å handle samme volum dayahead til spotpris. Positiv = vi fikk billigere RK enn spot. Negativ = vi betalte premium for å dekke shortfall."));
        kpis.Add(new("RkBruttoSalg_NOK", rkSalg, "NOK", ph, 1.0, "okonomi",
            "Σ RK-Salg — bruttoinntekter fra regulerkraft-salg (positivt tall)"));
        kpis.Add(new("RkBruttoKjop_NOK", rkKjop, "NOK", ph, 1.0, "okonomi",
            "Σ RK-Kjøp — bruttokostnad for regulerkraft-kjøp (negativt tall i kildedata)"));
        kpis.Add(new("RkNetto_NOK", rkSalg + rkKjop, "NOK", ph, 1.0, "okonomi",
            "RK-Salg + RK-Kjøp — netto pengestrøm RK isolert (uten gebyrer/ubalanse)."));
        kpis.Add(new("Ubalanseresultat_NOK", ubalanseResultat, "NOK", ph, 1.0, "okonomi",
            "Σ Tap/gevinst ubalanse eks. gebyr — markedseffekt av avvik fra eSett-posisjon"));
        kpis.Add(new("ESettVolumgebyr_NOK", esettVolumgebyr, "NOK", ph, 1.0, "okonomi",
            "Σ eSett volumgebyr — Statnett-gebyr for å være ute av balanse (negativt = kostnad)"));
        kpis.Add(new("ESettUbalansegebyr_NOK", esettUbalansegebyr, "NOK", ph, 1.0, "okonomi",
            "Σ eSett ubalansegebyr — Statnett-penalty for selve ubalansen (negativt = kostnad)"));
        kpis.Add(new("Oppgjor_NOK", oppgjorTotal, "NOK", ph, 1.0, "okonomi",
            "Σ Oppgjør — totalt nettooppgjør for perioden"));

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

    private static double? SafeRatio(double num, double den)
    {
        if (den == 0 || double.IsNaN(den)) return null;
        return num / den;
    }
}
