using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// Verifiserer Vakt-ROI-modellen mot eksempel-beregningen i overleveringen
/// 2026-04-28: Drivdal 2.2 MW, trip onsdag 16:00 fikset 17:30 → vakt redder
/// 14.5 t × 0.5 utnyttelse × 2.2 MW × 850 NOK/MWh ≈ 13 600 NOK
///
/// Merk: tallet i overleveringen (24 700 NOK) brukte effekt × pris uten
/// kapasitetsfaktor. Vi modellerer eksplisitt en kapasitetsfaktor &lt; 1.0
/// fordi reelle elvekraftverk sjelden produserer på full kapasitet i 14.5
/// strake timer.
/// </summary>
public class VaktRoiCalculatorTests
{
    private static readonly TimeZoneInfo Oslo = TimeZones.Norway;

    private static DateTimeOffset OsloLokal(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = Oslo.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    [Fact]
    public void Trip_Innenfor_Vakt_Onsdag_Beregner_ROI()
    {
        // Onsdag 4. feb 2026 16:00-17:30 lokal — vakt aktiv
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "operlog:fault",
            TapMwh = 1.0,
            TapNok = 850,
            TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, installertEffektMw: 2.2,
            snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5);

        roi.Should().HaveCount(1);
        var r = roi[0];
        r.ErInnenforVakt.Should().BeTrue();
        r.ErReddbar.Should().BeTrue();

        // Counterfactual = torsdag 5. feb 08:00 lokal
        var lokal = TimeZoneInfo.ConvertTime(r.CounterfactualEndUtc!.Value, Oslo);
        lokal.Day.Should().Be(5);
        lokal.Hour.Should().Be(8);

        // Ekstra timer = 08:00 (5. feb) − 17:30 (4. feb) = 14.5 t
        r.EkstraTimerSpart.Should().BeApproximately(14.5, 0.01);

        // ROI = 14.5 × 2.2 × 0.5 × 850 = 13 557.5 NOK
        r.ReddetMwh.Should().BeApproximately(14.5 * 2.2 * 0.5, 0.01);
        r.ReddetNok.Should().BeApproximately(14.5 * 2.2 * 0.5 * 850, 1.0);
    }

    [Fact]
    public void Trip_I_Arbeidstid_Gir_Ingen_ROI()
    {
        // Onsdag 4. feb 2026 kl 10:00 lokal — ordinær arbeidstid
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 10),
            EndUtc = OsloLokal(2026, 2, 4, 11),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 500, TimerSettlement = 1,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850);

        roi[0].ErInnenforVakt.Should().BeFalse();
        roi[0].ReddetNok.Should().Be(0);
    }

    [Fact]
    public void Planlagt_Vedlikehold_Innenfor_Vakt_Er_IKKE_Reddbart()
    {
        // Selv om planlagt-vedlikehold er innenfor vakt-vinduet, er det ikke ROI
        // — vakt fikser ikke planlagte ting.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 7, 10),  // Lørdag 10:00
            EndUtc = OsloLokal(2026, 2, 7, 12),
            State = UnitState.PlannedOutage,
            Category = DowntimeEventCategory.PlanlagtVedlikehold,
            TapMwh = 2.0, TapNok = 1700, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850);

        roi[0].ErInnenforVakt.Should().BeTrue();
        roi[0].ErReddbar.Should().BeFalse();
        roi[0].ReddetNok.Should().Be(0);
    }

    [Fact]
    public void Lang_Trip_Forbi_Counterfactual_Gir_Null_ROI()
    {
        // Trip onsdag 16:00, fikset først fredag morgen 06:00 — vakten brukte
        // mer tid enn driftspersonell ville gjort. Counterfactual = torsdag 08:00.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 6, 6),    // 38 t senere
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 30, TapNok = 25500, TimerSettlement = 38,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850);

        roi[0].ErInnenforVakt.Should().BeTrue();
        roi[0].ErReddbar.Should().BeTrue();
        roi[0].EkstraTimerSpart.Should().Be(0);
        roi[0].ReddetNok.Should().Be(0);
    }

    [Fact]
    public void Helg_Trip_Hopper_Til_Mandag_0800()
    {
        // Lørdag 7. feb 2026 kl 12:00 → mandag 9. feb 08:00 = 44 t ekstra
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 7, 12),
            EndUtc = OsloLokal(2026, 2, 7, 13),  // vakt fikset på 1 time
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850);

        roi[0].EkstraTimerSpart.Should().BeApproximately(43.0, 0.01); // 13:00 lørdag → 08:00 mandag
        roi[0].ReddetNok.Should().BeGreaterThan(0);
    }
}
