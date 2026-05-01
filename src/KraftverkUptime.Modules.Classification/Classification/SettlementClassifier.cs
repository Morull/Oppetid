using System.Globalization;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Classification.Classification;

/// <summary>
/// Klassifiserer settlement-rader til <see cref="UnitState"/> per time. Bruker
/// MwhElhub og SpotbudMwh som primære signaler, samt produksjonsplan vs Elhub
/// for å detektere underlevering.
///
/// <para>Regler i prioritert rekkefølge:</para>
/// <list type="number">
///   <item>Elhub mangler eller er negativ → <see cref="UnitState.InformationUnavailable"/>
///         (1.0 confidence). Negativ verdi tolkes som datafeil eller
///         regulerkraft-kjøp som vi ikke kan skille uten SCADA.</item>
///   <item>Elhub &gt; 0 OG Plan &gt; 0 OG <c>Elhub &lt; DeratingThreshold × Plan</c>
///         → <see cref="UnitState.ForcedDerating"/> (0.85). Drifts-leders krav:
///         avvik &gt; (1 − DeratingThreshold) fra plan teller som feil. Default-
///         terskel 0.80 = 20 % toleranse.</item>
///   <item>Elhub &gt; 0 → <see cref="UnitState.InService"/> (0.95).</item>
///   <item>Elhub == 0:
///     <list type="bullet">
///       <item>Spotbud &gt; 0 → <see cref="UnitState.ForcedOutage"/> (0.90).
///             Verket var forpliktet til å levere day-ahead, men leverte
///             ingenting. Per definisjon et uvarslet utfall.</item>
///       <item>Spotbud == 0 eller mangler → <see cref="UnitState.ReserveShutdown"/>
///             (0.80). Ingen markedsforpliktelse — verket er stille av
///             markeds- eller plan-grunner. Manuell annotering kan merke det
///             som vedlikehold, vannmangel, e.l.</item>
///     </list>
///   </item>
/// </list>
///
/// <para><see cref="PlantClassificationConfig.DeratingThreshold"/> er per-anlegg-
/// justerbar via PlantAdmin-UI. Default 0.80 betyr at Elhub mindre enn 80 % av
/// Plan klassifiseres som ForcedDerating; brukeren kan stramme inn til 0.90 for
/// strengere regulatoriske krav, eller løsne til 0.50 for variabel kraft.</para>
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

        var result = new List<ClassifiedHourlyRow>(n);
        for (var i = 0; i < n; i++)
        {
            var (state, conf, cause, rationale) = ClassifyOne(hourly[i], plant.DeratingThreshold);
            result.Add(new ClassifiedHourlyRow
            {
                Row = hourly[i],
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
        double deratingThreshold)
    {
        var elhub = row.MwhElhub;
        var bid = row.SpotbudMwh;
        var plan = row.ProduksjonplanMwh;

        // 1. Mangler data
        if (row.DqState == DataQualityState.InformationUnavailable || !elhub.HasValue)
        {
            return (UnitState.InformationUnavailable, 1.0, "9.1-DataMissing",
                "Manglende Elhub-data");
        }

        // 1b. Negativ Elhub = datafeil eller regulerkraft-kjøp; vi kan ikke skille
        if (elhub.Value < 0)
        {
            return (UnitState.InformationUnavailable, 0.6, "9.2-NegativeReading",
                $"Negativ MWh-Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} – datafeil eller regulerkraft-kjøp");
        }

        // 2. Positiv produksjon — sjekk plan-avvik først
        if (elhub.Value > 0)
        {
            // 2a. Plan-avvik over toleranse → ForcedDerating
            // Krever at både Plan > 0 (vi forpliktet oss til noe) og at Elhub
            // er < terskel × Plan (underlevering). Plan = 0 = vi bød ikke
            // og avvik er ikke meningsfullt.
            if (plan.HasValue && plan.Value > 0 && elhub.Value < deratingThreshold * plan.Value)
            {
                var avvikPct = (1 - elhub.Value / plan.Value) * 100;
                return (UnitState.ForcedDerating, 0.85, "U2-PlanDeviation",
                    $"Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} MWh er " +
                    $"{avvikPct.ToString("F0", CultureInfo.InvariantCulture)} % under Plan=" +
                    $"{plan.Value.ToString("F3", CultureInfo.InvariantCulture)} MWh " +
                    $"(toleranse {((1 - deratingThreshold) * 100).ToString("F0", CultureInfo.InvariantCulture)} %)");
            }
            // 2b. Innen toleranse eller ingen plan → InService
            return (UnitState.InService, 0.95, "0-Normal",
                $"Elhub={elhub.Value.ToString("F3", CultureInfo.InvariantCulture)} MWh");
        }

        // 3. Elhub == 0
        if (bid.HasValue && bid.Value > 0)
        {
            return (UnitState.ForcedOutage, 0.90, "U1-UnplannedStop",
                $"Spotbud={bid.Value.ToString("F3", CultureInfo.InvariantCulture)} MWh men Elhub=0 – uvarslet utfall");
        }

        return (UnitState.ReserveShutdown, 0.80, "M1-NoCommitment",
            "Elhub=0, ingen Spotbud – ingen markedsforpliktelse");
    }
}
