using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// SPEC-NEDETID-STARTTID-OVERRIDE §13: start-korreksjonen forskyver StartUtc,
/// bevarer detektert start (override-ankeret), og reberegner varighet + tap
/// fra plan-data. Haukland-caset: detektert 17:00 → korrigert 10:00 gir
/// +7 t varighet og tilsvarende økt tap.
/// </summary>
public class StartOverrideApplierTests
{
    private static DowntimeEvent MakeEvent(
        DateTimeOffset start, DateTimeOffset end, double tapMwh = 10, double tapNok = 5000)
        => new()
        {
            PlantId = "haukland",
            StartUtc = start,
            EndUtc = end,
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = tapMwh,
            TapNok = tapNok,
            TimerSettlement = (int)(end - start).TotalHours,
        };

    private static IReadOnlyDictionary<DateTimeOffset, double> PlanFlat(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, double mwhPerHour)
    {
        var dict = new Dictionary<DateTimeOffset, double>();
        var u = fromUtc.UtcDateTime;
        var h = new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
        for (; h < toUtc; h = h.AddHours(1)) dict[h] = mwhPerHour;
        return dict;
    }

    [Fact]
    public void Haukland_Case_Start_Flyttes_Tidligere_Oker_Varighet_Og_Tap()
    {
        // Detektert 17:00–20:00 (3 t). Reell start 10:00 → 10 t.
        var detektert = new DateTimeOffset(2026, 5, 28, 17, 0, 0, TimeSpan.Zero);
        var slutt = new DateTimeOffset(2026, 5, 28, 20, 0, 0, TimeSpan.Zero);
        var korrigert = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var ev = MakeEvent(detektert, slutt, tapMwh: 6, tapNok: 3000); // 500 NOK/MWh implisitt

        var plan = PlanFlat(korrigert, slutt, 2.0); // 2 MWh/t også i de nye timene

        var result = StartOverrideApplier.Apply(
            new[] { ev },
            new Dictionary<DateTimeOffset, DateTimeOffset> { [detektert] = korrigert },
            plan);

        var r = result.Single();
        r.StartUtc.Should().Be(korrigert);
        r.DetectedStartUtc.Should().Be(detektert, "override-ankeret må bevares");
        r.EffektivDetectedStartUtc.Should().Be(detektert);
        r.VarighetTimer.Should().BeApproximately(10, 0.01, "17:00→10:00 gir +7 t");

        // 7 nye timer × 2 MWh = 14 MWh ekstra, verdsatt til hendelsens egen
        // snittpris 3000/6 = 500 NOK/MWh → +7000 NOK.
        r.TapMwh.Should().BeApproximately(6 + 14, 0.01);
        r.TapNok.Should().BeApproximately(3000 + 14 * 500, 1.0);
    }

    [Fact]
    public void Uten_Override_Er_Events_Uendret()
    {
        var ev = MakeEvent(
            new DateTimeOffset(2026, 5, 28, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 28, 20, 0, 0, TimeSpan.Zero));

        var result = StartOverrideApplier.Apply(
            new[] { ev }, new Dictionary<DateTimeOffset, DateTimeOffset>());

        result.Single().Should().BeSameAs(ev);
        result.Single().DetectedStartUtc.Should().BeNull();
    }

    [Fact]
    public void Start_Flyttes_Senere_Reduserer_Tap_Men_Klippes_Ikke_Under_Null()
    {
        var detektert = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var slutt = new DateTimeOffset(2026, 5, 28, 20, 0, 0, TimeSpan.Zero);
        var korrigert = new DateTimeOffset(2026, 5, 28, 18, 0, 0, TimeSpan.Zero);
        var ev = MakeEvent(detektert, slutt, tapMwh: 5, tapNok: 2500);

        // Plan 2 MWh/t → 8 fjernede timer = 16 MWh > 5 MWh → klipp til 0.
        var plan = PlanFlat(detektert, slutt, 2.0);

        var result = StartOverrideApplier.Apply(
            new[] { ev },
            new Dictionary<DateTimeOffset, DateTimeOffset> { [detektert] = korrigert },
            plan);

        var r = result.Single();
        r.StartUtc.Should().Be(korrigert);
        r.VarighetTimer.Should().BeApproximately(2, 0.01);
        r.TapMwh.Should().Be(0);
        r.TapNok.Should().Be(0);
    }

    [Fact]
    public void Ugyldig_Korreksjon_Start_Etter_Slutt_Ignoreres_Defensivt()
    {
        var detektert = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var slutt = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);
        var ev = MakeEvent(detektert, slutt);

        var result = StartOverrideApplier.Apply(
            new[] { ev },
            new Dictionary<DateTimeOffset, DateTimeOffset> { [detektert] = slutt.AddHours(1) });

        result.Single().Should().BeSameAs(ev, "API-et validerer, men applieren skal være defensiv");
    }

    [Fact]
    public void Korreksjon_Nokles_Paa_Detektert_Start_Ogsaa_Ved_Gjentatt_Anvendelse()
    {
        // Simulerer at events allerede har DetectedStartUtc satt (f.eks. re-kjøring):
        // oppslaget skal treffe på detektert, ikke effektiv, start.
        var detektert = new DateTimeOffset(2026, 5, 28, 17, 0, 0, TimeSpan.Zero);
        var slutt = new DateTimeOffset(2026, 5, 28, 20, 0, 0, TimeSpan.Zero);
        var ev = MakeEvent(new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero), slutt)
            with
        { DetectedStartUtc = detektert };

        var korrigert = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var result = StartOverrideApplier.Apply(
            new[] { ev },
            new Dictionary<DateTimeOffset, DateTimeOffset> { [detektert] = korrigert });

        var r = result.Single();
        r.StartUtc.Should().Be(korrigert);
        r.DetectedStartUtc.Should().Be(detektert);
    }
}
