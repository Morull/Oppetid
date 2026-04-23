using System.Globalization;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Classification.Classification;

/// <summary>
/// Proxy-klassifisering av hver time basert på settlement-data.
///
/// Reglene følger Python-referansen (<c>drivdal-analyse/src/classifier.py</c>)
/// eksakt, og er betinget på <see cref="PlantType"/>:
///
/// <para>Ordre for én time:</para>
/// <list type="number">
///   <item>DqState = InformationUnavailable → UnitState.InformationUnavailable
///         (1.0 confidence)</item>
///   <item>MwhElhub &lt; 0 → InformationUnavailable (0.6 confidence) – negativ
///         verdi kan være regulerkraft-kjøp eller måleavvik, vi skiller ikke
///         uten SCADA.</item>
///   <item>MwhElhub == 0 og Plan &gt; 0.01 → ForcedOutage (0.80) – sterkt signal.</item>
///   <item>MwhElhub == 0 og Plan ≤ 0.01, Regulated:
///     <list type="bullet">
///       <item>run_len ≥ SustainedStopHours → PlannedOutage (0.70)</item>
///       <item>Spotpris &lt; median → ReserveShutdown (0.55, markedsstyrt)</item>
///       <item>Spotpris ≥ median → ForcedOutage (0.45, uvanlig)</item>
///       <item>Ingen spotpris → ReserveShutdown (0.40)</item>
///     </list>
///   </item>
///   <item>MwhElhub == 0 og Plan ≤ 0.01, RunOfRiver → ResourceUnavailable (0.40)</item>
///   <item>MwhElhub &gt; 0 og Plan &gt; 0, ratio &lt; DeratingThreshold →
///         ForcedDerating (0.65)</item>
///   <item>MwhElhub &gt; 0 og Plan &gt; 0, ratio ≥ threshold → InService (0.95)</item>
///   <item>MwhElhub &gt; 0 og Plan mangler → InService (0.80)</item>
/// </list>
///
/// <para>Merk: median spotpris beregnes én gang per periode og brukes i alle
/// RS/FO-beslutninger. Dette er viktig fordi absolutt terskel (f.eks. 300 NOK/MWh)
/// ville vært feil for måneder med ekstreme priser.</para>
/// </summary>
public sealed class SettlementClassifier
{
    public IReadOnlyList<ClassifiedHourlyRow> Classify(
        IReadOnlyList<SettlementHourlyRow> hourly,
        PlantClassificationConfig plant)
    {
        ArgumentNullException.ThrowIfNull(hourly);
        ArgumentNullException.ThrowIfNull(plant);

        var n = hourly.Count;
        if (n == 0)
        {
            return Array.Empty<ClassifiedHourlyRow>();
        }

        // Pre-beregn hjelpedatastrukturer
        var isZero = new bool[n];
        for (var i = 0; i < n; i++)
        {
            isZero[i] = hourly[i].MwhElhub.HasValue && hourly[i].MwhElhub!.Value == 0;
        }
        var runLengths = RunLengthCalculator.Compute(isZero);

        var medianSpot = ComputeMedianSpotPrice(hourly);

        var result = new List<ClassifiedHourlyRow>(n);
        for (var i = 0; i < n; i++)
        {
            var row = hourly[i];
            var (state, conf, cause, rationale) = ClassifyOne(row, plant, medianSpot, runLengths[i]);
            result.Add(new ClassifiedHourlyRow
            {
                Row = row,
                State = state,
                CauseCode = cause,
                Confidence = conf,
                Rationale = rationale,
            });
        }
        return result;
    }

    private static (UnitState state, double conf, string cause, string rationale) ClassifyOne(
        SettlementHourlyRow row,
        PlantClassificationConfig plant,
        double? medianSpot,
        int runLen)
    {
        var elhub = row.MwhElhub;
        var plan = row.ProduksjonplanMwh;
        var spot = row.SpotprisNokMwh;

        // 1. Mangler data
        if (row.DqState == DataQualityState.InformationUnavailable || !elhub.HasValue)
        {
            return (UnitState.InformationUnavailable, 1.0, "9.1-DataMissing", "Manglende Elhub-data");
        }

        // 2. Negativ Elhub
        if (elhub.Value < 0)
        {
            return (UnitState.InformationUnavailable, 0.6, "9.2-NegativeReading",
                $"Negativ MWh-Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)}, kan være regulerkraft-kjøp eller måleavvik");
        }

        // 3. Null produksjon
        if (elhub.Value == 0)
        {
            if (plan.HasValue && plan.Value > 0.01)
            {
                return (UnitState.ForcedOutage, 0.80, "U1-UnplannedStop",
                    $"Plan={plan.Value.ToString("F3", CultureInfo.InvariantCulture)} men Elhub=0 (uvarslet stans)");
            }

            if (plant.PlantType == PlantType.Regulated)
            {
                if (runLen >= plant.SustainedStopHours)
                {
                    return (UnitState.PlannedOutage, 0.70, "P1-ScheduledStop",
                        $"Elhub=0 sammenhengende {runLen}h (≥ {plant.SustainedStopHours}h), sannsynlig planlagt");
                }

                if (spot.HasValue && medianSpot.HasValue)
                {
                    if (spot.Value < medianSpot.Value)
                    {
                        return (UnitState.ReserveShutdown, 0.55, "M1-MarketDriven",
                            $"Elhub=0, Plan=0, Spotpris={spot.Value:F0} < median {medianSpot.Value:F0} – markedsstyrt");
                    }
                    return (UnitState.ForcedOutage, 0.45, "U2-UnexpectedStop",
                        $"Elhub=0, Plan=0, Spotpris={spot.Value:F0} ≥ median {medianSpot.Value:F0} – uvanlig");
                }

                return (UnitState.ReserveShutdown, 0.40, "M1-MarketDriven",
                    "Elhub=0, Plan=0 – antatt markedsstyrt (lav confidence)");
            }

            // RunOfRiver (og andre uten hydrologi-tilkobling)
            return (UnitState.ResourceUnavailable, 0.40, "8.1-WaterLimited",
                "Elhub=0 på elvekraft – antatt ressursbegrenset (krever hydrologi for bekreftelse)");
        }

        // 4. Positiv produksjon, sammenlign med plan
        if (plan.HasValue && plan.Value > 0)
        {
            var ratio = elhub.Value / plan.Value;
            if (ratio < plant.DeratingThreshold)
            {
                return (UnitState.ForcedDerating, 0.65, "D1-ForcedDerating",
                    $"Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} = {ratio:P0} av Plan={plan.Value.ToString("F3", CultureInfo.InvariantCulture)} – redusert kapasitet");
            }
            return (UnitState.InService, 0.95, "0-Normal",
                $"Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} ≈ Plan={plan.Value.ToString("F3", CultureInfo.InvariantCulture)} (ratio={ratio:P0})");
        }

        // 5. Positiv produksjon, ingen plan
        return (UnitState.InService, 0.80, "0-Normal",
            $"Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} (ingen plan-sammenligning)");
    }

    private static double? ComputeMedianSpotPrice(IReadOnlyList<SettlementHourlyRow> hourly)
    {
        var values = new List<double>();
        foreach (var r in hourly)
        {
            if (r.SpotprisNokMwh.HasValue)
            {
                values.Add(r.SpotprisNokMwh.Value);
            }
        }
        if (values.Count == 0)
        {
            return null;
        }
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2.0;
    }
}
