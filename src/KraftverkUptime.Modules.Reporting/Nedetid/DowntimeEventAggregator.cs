using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Pure funksjon som tar en sekvens klassifiserte timer + (valgfritt) operlog-events
/// og produserer aggregerte <see cref="DowntimeEvent"/>-rader.
///
/// Aggregerings-regelen:
///   – Sammenhengende timer der <c>State</c> regnes som nedetid slås sammen
///     til ett event.
///   – State-bytte (FO → MO eller motsatt) starter et nytt event, fordi vi
///     vil rapportere skille mellom forced og maintenance i UI.
///   – Hull i timene (én eller flere ikke-nedetidstimer mellom) avslutter
///     et event.
///   – Tap_mwh per time = ProduksjonplanMwh ?? SpotbudMwh ?? 0.
///   – Tap_nok per time = tap_mwh × SpotprisNokMwh (fallback: 0).
///
/// Operlog-overlay (hvis events er gitt):
///   – For hvert aggregert event sjekker vi om noen operlog-event har
///     StartUtc innenfor [event.StartUtc, event.EndUtc]. Da settes
///     <see cref="DowntimeEvent.HarOperlogMatch"/> = true og CauseCode
///     overstyres til operlog-eventets cause hvis den er mer presis.
///   – Vi flytter IKKE event.Start/End til operlog sin sub-time-presisjon i v1
///     fordi tap-beregningen er bundet til time-rader. Dette er et bevisst
///     valg for å holde modellen transparent og reproduserbar.
///
/// Pure (ingen DB-tilgang) for å være lett å unit-teste.
/// </summary>
public static class DowntimeEventAggregator
{
    /// <summary>States som teller som nedetid og dermed gir events.</summary>
    private static readonly HashSet<UnitState> NedetidsStates = new()
    {
        UnitState.ForcedOutage,
        UnitState.MaintenanceOutage,
        UnitState.PlannedOutage,
        UnitState.ResourceUnavailable,
        UnitState.ForcedDerating,
        UnitState.PlannedDerating,
    };

    public static IReadOnlyList<DowntimeEvent> Aggregate(
        string plantId,
        IReadOnlyList<ClassifiedHourlyRow> classifiedHours,
        IReadOnlyList<ClassifiedEvent>? operlogEvents = null,
        IRistAlarmDetector? ristDetector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentNullException.ThrowIfNull(classifiedHours);

        if (classifiedHours.Count == 0)
        {
            return Array.Empty<DowntimeEvent>();
        }

        var sortedHours = classifiedHours.OrderBy(h => h.TimeUtc).ToList();
        var events = new List<DowntimeEvent>();

        DateTimeOffset? currentStart = null;
        UnitState currentState = UnitState.InService;
        var currentCause = string.Empty;
        var currentRationale = string.Empty;
        var hourCount = 0;
        var sumTapMwh = 0.0;
        var sumTapNok = 0.0;

        for (var i = 0; i < sortedHours.Count; i++)
        {
            var row = sortedHours[i];
            var isDowntime = NedetidsStates.Contains(row.State);

            // Bestem om vi skal lukke gjeldende event
            var prevHour = i > 0 ? sortedHours[i - 1].TimeUtc : (DateTimeOffset?)null;
            var continuous = prevHour.HasValue && row.TimeUtc == prevHour.Value.AddHours(1);
            var stateChanged = currentStart.HasValue && row.State != currentState;

            if (currentStart.HasValue && (!isDowntime || !continuous || stateChanged))
            {
                // Lukk pågående event på forrige time + 1
                var endUtc = prevHour!.Value.AddHours(1);
                events.Add(BuildEvent(plantId, currentStart.Value, endUtc,
                    currentState, currentCause, currentRationale,
                    hourCount, sumTapMwh, sumTapNok));
                currentStart = null;
            }

            if (isDowntime && !currentStart.HasValue)
            {
                // Start nytt event
                currentStart = row.TimeUtc;
                currentState = row.State;
                currentCause = row.CauseCode;
                currentRationale = row.Rationale;
                hourCount = 0;
                sumTapMwh = 0;
                sumTapNok = 0;
            }

            if (isDowntime && currentStart.HasValue)
            {
                hourCount++;
                var tapMwh = row.ProduksjonplanMwh ?? row.SpotbudMwh ?? 0.0;
                if (tapMwh < 0) tapMwh = 0;
                sumTapMwh += tapMwh;
                if (row.SpotprisNokMwh.HasValue)
                {
                    sumTapNok += tapMwh * row.SpotprisNokMwh.Value;
                }
            }
        }

        // Lukk siste event hvis det fortsatt er åpent
        if (currentStart.HasValue)
        {
            var endUtc = sortedHours[^1].TimeUtc.AddHours(1);
            events.Add(BuildEvent(plantId, currentStart.Value, endUtc,
                currentState, currentCause, currentRationale,
                hourCount, sumTapMwh, sumTapNok));
        }

        // Berik med operlog-overlay hvis tilgjengelig
        if (operlogEvents is { Count: > 0 })
        {
            events = ApplyOperlogOverlay(events, operlogEvents).ToList();

            // Sub-hour trips fra operlog som IKKE manifesterer seg som FO-timer
            // i settlement (kortvarig stopp innen samme time som elhub > 0)
            // legges til som synthetiske 1-time events. Bevarer presisjonen
            // for /nedetid-rapporten — drifts-leder ser alle reelle trips,
            // ikke bare de som gjorde Elhub-time = 0.
            events = AddOperlogOnlyFaultEvents(plantId, events, operlogEvents).ToList();
        }

        // Rist-deteksjon: trip-events innen ±60 min av rist-falltap-alarm blir
        // omklassifisert til TettInntaksrist. Egen kategori for at drifts-leder
        // skal kunne skille vedlikeholds-relaterte stopp fra mekaniske feil.
        var detector = ristDetector ?? new RistAlarmDetector();
        var ristAlarms = operlogEvents is null
            ? Array.Empty<DateTimeOffset>()
            : detector.ExtractRistAlarmTimes(operlogEvents);
        if (ristAlarms.Count > 0)
        {
            events = ApplyRistDetection(events, ristAlarms, detector).ToList();
        }

        return events;
    }

    /// <summary>
    /// Legger til synthetiske downtime-events fra operlog-fault-events som ikke
    /// ble dekket av settlement-aggregeringen. Disse representerer typisk korte
    /// sub-time trips der anlegget restartet innen samme time og dermed ikke
    /// produserte Elhub=0. Hver slik fault blir en 1-time event som er klar
    /// for rist-deteksjon (om det finnes en rist-alarm i nærheten).
    /// </summary>
    private static IEnumerable<DowntimeEvent> AddOperlogOnlyFaultEvents(
        string plantId,
        IList<DowntimeEvent> existingEvents,
        IReadOnlyList<ClassifiedEvent> operlogEvents)
    {
        // Bevar eksisterende events først
        foreach (var e in existingEvents) yield return e;

        // Bare operlog-faults skal eskaleres til downtime-events.
        var faults = operlogEvents
            .Where(o => string.Equals(o.CauseCode, "operlog:fault", StringComparison.Ordinal))
            .OrderBy(o => o.StartUtc)
            .ToList();

        // Dedupliser: hvis flere faults er innenfor samme klokketime, lag bare én event.
        var emittedHours = new HashSet<DateTimeOffset>();

        foreach (var fault in faults)
        {
            var faultHour = FloorToHour(fault.StartUtc);

            // Hopp over hvis fault-tidspunktet allerede dekkes av en settlement-event
            var alreadyCovered = existingEvents.Any(e =>
                fault.StartUtc >= e.StartUtc && fault.StartUtc < e.EndUtc);
            if (alreadyCovered) continue;

            // Dedup på time
            if (!emittedHours.Add(faultHour)) continue;

            yield return new DowntimeEvent
            {
                PlantId = plantId,
                StartUtc = faultHour,
                EndUtc = faultHour.AddHours(1),
                State = UnitState.ForcedOutage,
                Category = DowntimeEventCategory.TripFeil, // overstyres av rist-deteksjon hvis aktuelt
                CauseCode = fault.CauseCode,
                TapMwh = 0, // sub-time trip — settlement viste fortsatt produksjon
                TapNok = 0,
                TimerSettlement = 0, // markerer at eventet er operlog-only
                HarOperlogMatch = true,
                Rationale = fault.Rationale ?? "operlog-only fault (sub-time trip)",
            };
        }
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    private static IEnumerable<DowntimeEvent> ApplyRistDetection(
        IList<DowntimeEvent> events,
        IReadOnlyList<DateTimeOffset> ristAlarmTimes,
        IRistAlarmDetector detector)
    {
        foreach (var e in events)
        {
            // Bare trip-relaterte states kan omklassifiseres som rist.
            var trippable = e.State == UnitState.ForcedOutage
                || e.State == UnitState.ForcedDerating;
            if (!trippable)
            {
                yield return e;
                continue;
            }

            if (detector.IsRistRelated(e.StartUtc, ristAlarmTimes))
            {
                yield return e with
                {
                    Category = DowntimeEventCategory.TettInntaksrist,
                    CauseCode = "operlog:rist-falltap",
                };
            }
            else
            {
                yield return e;
            }
        }
    }

    private static DowntimeEvent BuildEvent(
        string plantId,
        DateTimeOffset start,
        DateTimeOffset end,
        UnitState state,
        string causeCode,
        string rationale,
        int hourCount,
        double sumTapMwh,
        double sumTapNok)
    {
        return new DowntimeEvent
        {
            PlantId = plantId,
            StartUtc = start,
            EndUtc = end,
            State = state,
            Category = MapCategory(state, causeCode),
            CauseCode = causeCode,
            TapMwh = sumTapMwh,
            TapNok = sumTapNok,
            TimerSettlement = hourCount,
            HarOperlogMatch = false,
            Rationale = rationale,
        };
    }

    private static IEnumerable<DowntimeEvent> ApplyOperlogOverlay(
        IList<DowntimeEvent> events,
        IReadOnlyList<ClassifiedEvent> operlogEvents)
    {
        // Indekser operlog-events i tids-sortert array. For hvert aggregert event
        // gjør vi binærsøk-light: bruk bare events hvis StartUtc faller innenfor
        // event-intervallet. Det holder for v1; for store volumer kan vi bytte
        // til interval-tree senere.
        var operlogSorted = operlogEvents.OrderBy(e => e.StartUtc).ToList();
        foreach (var e in events)
        {
            var match = operlogSorted.FirstOrDefault(o =>
                o.StartUtc >= e.StartUtc && o.StartUtc < e.EndUtc);

            if (match is not null)
            {
                yield return e with
                {
                    HarOperlogMatch = true,
                    CauseCode = match.CauseCode ?? e.CauseCode,
                };
            }
            else
            {
                yield return e;
            }
        }
    }

    /// <summary>
    /// Mapper (state, cause) til en presentasjons-vennlig kategori. Brukes til
    /// Vakt-ROI-filtreringen og donut-diagrammet på /nedetid-siden.
    /// </summary>
    public static DowntimeEventCategory MapCategory(UnitState state, string? causeCode)
    {
        var cc = causeCode ?? string.Empty;

        // Eksterne forstyrrelser markeres eksplisitt via cause-code-prefiks.
        // I v1 har vi ikke dette i klassifikatoren, men annoteringer/operlog kan sette det.
        if (cc.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
            cc.Contains("nett", StringComparison.OrdinalIgnoreCase) ||
            cc.Contains("frekvens", StringComparison.OrdinalIgnoreCase))
        {
            return DowntimeEventCategory.EksternForstyrrelse;
        }

        return state switch
        {
            UnitState.ForcedOutage => DowntimeEventCategory.TripFeil,
            UnitState.ForcedDerating => DowntimeEventCategory.TripFeil,
            UnitState.MaintenanceOutage => DowntimeEventCategory.PlanlagtVedlikehold,
            UnitState.PlannedOutage => DowntimeEventCategory.PlanlagtVedlikehold,
            UnitState.PlannedDerating => DowntimeEventCategory.PlanlagtVedlikehold,
            UnitState.ResourceUnavailable => DowntimeEventCategory.Ressursmangel,
            UnitState.ReserveShutdown => DowntimeEventCategory.Markedstopp,
            UnitState.InformationUnavailable => DowntimeEventCategory.DataMangel,
            _ => DowntimeEventCategory.UkjentNedetid,
        };
    }
}
