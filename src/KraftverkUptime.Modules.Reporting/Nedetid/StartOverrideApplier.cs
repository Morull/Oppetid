using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Anvender manuelle START-korreksjoner på aggregerte nedetids-hendelser
/// (SPEC-NEDETID-STARTTID-OVERRIDE). Ren funksjon — kalles oppstrøms for
/// <see cref="VaktRoiCalculator"/> (samme mønster som slutt-korreksjonen),
/// slik at kalkulatoren forblir en ren funksjon av events.
///
/// For hver hendelse med korrigert start:
/// <list type="bullet">
/// <item><c>StartUtc</c> settes til korrigert tid; opprinnelig (detektert)
/// start bevares i <see cref="DowntimeEvent.DetectedStartUtc"/> — override-
/// radene er nøklet på detektert start, så øvrige overstyringer beholder
/// ankeret sitt.</item>
/// <item><c>VarighetTimer</c> følger automatisk (beregnet fra Start/End).</item>
/// <item><c>TapMwh</c>/<c>TapNok</c> justeres for timene vinduet utvides/krympes
/// med i forkant: tap_mwh = plan per time (samme kilde som Vakt-ROI), verdsatt
/// til hendelsens egen snittpris (TapNok/TapMwh) — eller fallback-spot når
/// hendelsen ikke har egen pris. Konservativt: tap klippes aldri under 0.</item>
/// </list>
/// </summary>
public static class StartOverrideApplier
{
    public static IReadOnlyList<DowntimeEvent> Apply(
        IReadOnlyList<DowntimeEvent> events,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> startOverridesByDetectedStart,
        IReadOnlyDictionary<DateTimeOffset, double>? planByHour = null,
        double fallbackSpotNokMwh = 0)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(startOverridesByDetectedStart);
        if (startOverridesByDetectedStart.Count == 0) return events;

        var result = new List<DowntimeEvent>(events.Count);
        foreach (var e in events)
        {
            if (!startOverridesByDetectedStart.TryGetValue(e.EffektivDetectedStartUtc, out var nyStart)
                || nyStart == e.StartUtc
                || nyStart >= e.EndUtc)
            {
                // Ingen korreksjon, no-op-korreksjon, eller ugyldig (start etter
                // slutt — API-et validerer, men vær defensiv): behold hendelsen.
                result.Add(e);
                continue;
            }

            // Tap-justering for vindus-endringen i forkant: [nyStart, gammelStart)
            // legges til (start flyttet tidligere), [gammelStart, nyStart) trekkes
            // fra (start flyttet senere, innenfor hendelsen).
            var deltaMwh = SumPlan(planByHour, nyStart, e.StartUtc)
                - SumPlan(planByHour, e.StartUtc, nyStart);
            var spot = e.TapMwh > 0 && e.TapNok != 0
                ? e.TapNok / e.TapMwh
                : fallbackSpotNokMwh;

            result.Add(e with
            {
                StartUtc = nyStart,
                DetectedStartUtc = e.EffektivDetectedStartUtc,
                TapMwh = Math.Max(0, e.TapMwh + deltaMwh),
                TapNok = Math.Max(0, e.TapNok + deltaMwh * spot),
            });
        }
        return result;
    }

    /// <summary>Sum av plan-MWh over hele klokketimer i [fra, til). Tomt intervall → 0.</summary>
    private static double SumPlan(
        IReadOnlyDictionary<DateTimeOffset, double>? planByHour,
        DateTimeOffset fra, DateTimeOffset til)
    {
        if (planByHour is null || til <= fra) return 0;
        double sum = 0;
        for (var h = FloorToHour(fra); h < til; h = h.AddHours(1))
        {
            if (planByHour.TryGetValue(h, out var v) && v > 0) sum += v;
        }
        return sum;
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
