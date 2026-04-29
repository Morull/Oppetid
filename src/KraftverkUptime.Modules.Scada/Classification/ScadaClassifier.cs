using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Classification;

/// <summary>
/// Klassifiserer SCADA-time-rader til en av <see cref="UnitState"/>-tilstandene.
/// Pure funksjon — ingen DB-tilgang. Caller mater inn ferdig-pivoterte
/// hourly-records og operlog-events; klassifikatoren anvender reglene fra
/// <c>ANALYSE-NEDETID-SCADA.md</c> i prioritert rekkefølge.
///
/// Anlegg-uavhengig: alle terskler er normaliserte (% av installert effekt
/// eller absolutte SI-verdier som universelt definerer "står"/"snurrer").
/// Per-anleggs nominal RPM kan overstyres via <see cref="ScadaClassifierOptions"/>.
/// </summary>
public static class ScadaClassifier
{
    /// <summary>
    /// Klassifiserer en sekvens timer. Returnerer én rad per input-time i samme
    /// rekkefølge.
    /// </summary>
    public static IReadOnlyList<ScadaClassifiedHour> Classify(
        IReadOnlyList<ScadaHourlyInput> hours,
        ScadaClassifierOptions options,
        IReadOnlyList<ClassifiedEvent>? operlog = null)
    {
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<ScadaClassifiedHour>(hours.Count);
        var operlogList = operlog ?? Array.Empty<ClassifiedEvent>();

        foreach (var h in hours)
        {
            result.Add(ClassifyOne(h, options, operlogList));
        }
        return result;
    }

    private static ScadaClassifiedHour ClassifyOne(
        ScadaHourlyInput h,
        ScadaClassifierOptions options,
        IReadOnlyList<ClassifiedEvent> operlog)
    {
        // Regel 1: kommunikasjons-alarm aktiv → datahull
        if (h.CommunicationAlarm == true)
        {
            return new(h.TimeUtc, UnitState.InformationUnavailable, 0.95,
                "scada:com_alarm",
                "Kommunikasjons-alarm aktiv — SCADA-data ikke pålitelig.");
        }

        // Regel 2: kjørende (turtall nær nominell ELLER P > terskel)
        var isRunning = IsSpinning(h, options) || (h.PowerKw ?? 0) >= options.MinProductionKw;

        if (isRunning)
        {
            return ClassifyRunning(h, options);
        }

        // Regel 3: anlegget står
        return ClassifyStandstill(h, options, operlog);
    }

    private static bool IsSpinning(ScadaHourlyInput h, ScadaClassifierOptions options)
    {
        if (!h.RpmAvg.HasValue) return false;
        return h.RpmAvg.Value >= options.NominalRpm * options.SpinningRatio;
    }

    private static ScadaClassifiedHour ClassifyRunning(
        ScadaHourlyInput h, ScadaClassifierOptions options)
    {
        var p = h.PowerKw ?? 0;
        var capacityKw = options.InstalledCapacityKw;

        // Hvis vi ikke vet kapasitet kan vi ikke skille derating fra full drift.
        // Faller tilbake til InService for sikkerhetens skyld.
        if (capacityKw <= 0)
        {
            return new(h.TimeUtc, UnitState.InService, 0.85,
                "scada:running",
                $"Roterer ({h.RpmAvg:F0} rpm) og produserer {p:F0} kW.");
        }

        var loadFraction = p / capacityKw;
        if (loadFraction >= options.FullProductionRatio)
        {
            return new(h.TimeUtc, UnitState.InService, 0.95,
                "scada:in_service",
                $"Full drift: {p:F0} kW = {loadFraction:P0} av {capacityKw:F0} kW kapasitet.");
        }

        if (loadFraction >= options.PartialDeratingRatio)
        {
            // Lett redusert ytelse — flagg som derating men med moderat confidence
            return new(h.TimeUtc, UnitState.ForcedDerating, 0.75,
                "scada:partial_derating",
                $"Redusert ytelse: {p:F0} kW = {loadFraction:P0} (under {options.FullProductionRatio:P0}-terskelen).");
        }

        // Sterk derating
        return new(h.TimeUtc, UnitState.ForcedDerating, 0.85,
            "scada:strong_derating",
            $"Sterk derating: {p:F0} kW = {loadFraction:P0} (under {options.PartialDeratingRatio:P0}-terskelen).");
    }

    private static ScadaClassifiedHour ClassifyStandstill(
        ScadaHourlyInput h,
        ScadaClassifierOptions options,
        IReadOnlyList<ClassifiedEvent> operlog)
    {
        // 3a: vannmangel — magasin på/under LRV
        if (IsAtLowerRegulatedLevel(h, options))
        {
            return new(h.TimeUtc, UnitState.ResourceUnavailable, 0.90,
                "scada:resource_unavailable",
                $"Magasin på/under LRV ({h.UpstreamLevelMoh:F2} m vs LRV {h.LowestRegulatedLevelMoh:F2} m). " +
                "Ingen vann tilgjengelig.");
        }

        // 3b: operlog-overlay
        var operlogMatch = operlog.FirstOrDefault(o =>
            o.StartUtc >= h.TimeUtc && o.StartUtc < h.TimeUtc.AddHours(1));
        if (operlogMatch is not null)
        {
            var cause = operlogMatch.CauseCode ?? string.Empty;
            if (cause.Contains("fault", StringComparison.OrdinalIgnoreCase)
                || cause.Contains("alarm", StringComparison.OrdinalIgnoreCase)
                || cause.Contains("feil", StringComparison.OrdinalIgnoreCase))
            {
                return new(h.TimeUtc, UnitState.ForcedOutage, 0.92,
                    "operlog:fault",
                    $"Stopp m/operlog-feil: {cause}.");
            }
            if (cause.Contains("plan", StringComparison.OrdinalIgnoreCase)
                || cause.Contains("scheduled", StringComparison.OrdinalIgnoreCase))
            {
                return new(h.TimeUtc, UnitState.PlannedOutage, 0.88,
                    "operlog:planned",
                    $"Planlagt stopp: {cause}.");
            }
            // Manuell stopp uten klassifisert årsak — fall tilbake til vedlikehold
            if (cause.Contains("stop", StringComparison.OrdinalIgnoreCase))
            {
                return new(h.TimeUtc, UnitState.MaintenanceOutage, 0.80,
                    "operlog:manual_stop",
                    $"Manuell stopp via operlog: {cause}.");
            }
        }

        // 3c: spotbud-forpliktelse → ForcedOutage
        if ((h.SpotbudMwh ?? 0) > 0)
        {
            return new(h.TimeUtc, UnitState.ForcedOutage, 0.85,
                "scada:committed_no_delivery",
                $"Står med Spotbud-forpliktelse {h.SpotbudMwh:F2} MWh — ukjent årsak.");
        }

        // 3d: ingen forpliktelse, magasin OK → markedsstyrt stopp
        return new(h.TimeUtc, UnitState.ReserveShutdown, 0.80,
            "scada:reserve_shutdown",
            "Står uten Spotbud-forpliktelse og uten ressurs-mangel.");
    }

    private static bool IsAtLowerRegulatedLevel(ScadaHourlyInput h, ScadaClassifierOptions options)
    {
        if (!h.UpstreamLevelMoh.HasValue || !h.LowestRegulatedLevelMoh.HasValue) return false;
        var margin = h.UpstreamLevelMoh.Value - h.LowestRegulatedLevelMoh.Value;
        return margin <= options.LrvMarginMoh;
    }
}

/// <summary>
/// En time med pivoterte SCADA-samples, klar for klassifisering. Caller
/// (typisk en query-tjeneste) bygger denne fra <c>core.signal_map</c>-rolle-
/// lookup + sample_facts.
/// </summary>
public sealed record ScadaHourlyInput
{
    public required DateTimeOffset TimeUtc { get; init; }
    public double? PowerKw { get; init; }
    public double? RpmAvg { get; init; }
    public double? FrequencyHz { get; init; }
    public double? UpstreamLevelMoh { get; init; }
    public double? LowestRegulatedLevelMoh { get; init; }
    public double? ReservoirFillPct { get; init; }
    public bool? CommunicationAlarm { get; init; }
    public double? SpotbudMwh { get; init; }
}

/// <summary>
/// Konfigurasjon for klassifikatoren. Default-verdier matcher en typisk
/// vannkraftverk-installasjon. Spesielle anlegg kan overstyre via PlantConfig.
/// </summary>
public sealed record ScadaClassifierOptions
{
    public required double InstalledCapacityKw { get; init; }
    public double NominalRpm { get; init; } = 750; // typisk synkron-rpm for Francis-turbin
    public double SpinningRatio { get; init; } = 0.95;
    public double FullProductionRatio { get; init; } = 0.95;
    public double PartialDeratingRatio { get; init; } = 0.50;
    public double MinProductionKw { get; init; } = 50;
    public double LrvMarginMoh { get; init; } = 0.05;
}

/// <summary>Klassifisert SCADA-time. Brukes som input til FusionClassifier.</summary>
public sealed record ScadaClassifiedHour(
    DateTimeOffset TimeUtc,
    UnitState State,
    double Confidence,
    string CauseCode,
    string Rationale);
