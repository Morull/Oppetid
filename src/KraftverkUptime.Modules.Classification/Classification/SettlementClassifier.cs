using System.Globalization;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Classification.Classification;

/// <summary>
/// Forenklet proxy-klassifisering: én tilstand per time, basert kun på
/// MwhElhub og SpotbudMwh. Plant.PlantType og terskelverdier som
/// DeratingThreshold / SustainedStopHours brukes ikke lenger.
///
/// <para>Tre regler i prioritert rekkefølge:</para>
/// <list type="number">
///   <item>Elhub mangler eller er negativ → <see cref="UnitState.InformationUnavailable"/>
///         (1.0 confidence). Negativ verdi tolkes som datafeil eller
///         regulerkraft-kjøp som vi ikke kan skille uten SCADA.</item>
///   <item>Elhub &gt; 0 → <see cref="UnitState.InService"/> (0.95). Vi
///         vurderer ikke om det matcher Plan eller Spotbud — eventuelle
///         avvik fanges økonomisk via Ubalanse/RK-tallene.</item>
///   <item>Elhub == 0:
///     <list type="bullet">
///       <item>Spotbud &gt; 0 → <see cref="UnitState.ForcedOutage"/> (0.90).
///             Verket var forpliktet til å levere day-ahead, men leverte
///             ingenting. Det er per definisjon et uvarslet utfall fra
///             markedets perspektiv.</item>
///       <item>Spotbud == 0 eller mangler → <see cref="UnitState.ReserveShutdown"/>
///             (0.80). Ingen markedsforpliktelse — verket er stille av
///             markeds- eller plan-grunner. Senere kan en manuell
///             annotering merke det som vedlikehold, vannmangel, e.l.</item>
///     </list>
///   </item>
/// </list>
///
/// <para>Endringer fra forrige modell:</para>
/// <list type="bullet">
///   <item>Ingen <c>ForcedDerating</c> (90% av Plan-regelen er borte).</item>
///   <item>Ingen <c>PlannedOutage</c> automatisk (24t-regelen er borte).</item>
///   <item>Ingen median-spotpris-beregning.</item>
///   <item>Ingen <c>ResourceUnavailable</c> automatisk (skille mot RoR fjernet).</item>
/// </list>
/// Disse tilstandene kan fortsatt produseres via manuell annotering når
/// den funksjonen er bygd.
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
            var (state, conf, cause, rationale) = ClassifyOne(hourly[i]);
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
        SettlementHourlyRow row)
    {
        var elhub = row.MwhElhub;
        var bid = row.SpotbudMwh;

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

        // 2. Positiv produksjon = i drift
        if (elhub.Value > 0)
        {
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
