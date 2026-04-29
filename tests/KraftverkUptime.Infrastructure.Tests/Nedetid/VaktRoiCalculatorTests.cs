using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// Verifiserer Vakt-ROI-modellen mot eksempel-beregningen i overleveringen
/// 2026-04-28: Drivdal 2.2 MW, trip onsdag 16:00 fikset 17:30 → vakt redder
/// 14.5 t × 0.5 utnyttelse × 2.2 MW × 850 NOK/MWh ≈ 13 600 NOK.
///
/// Etter spec 2026-04-29 forutsetter alle ROI-tester at counterfactual-perioden
/// hadde overløp i magasinet. Tester uten overløp gir per definisjon null ROI.
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

    private static IReadOnlySet<DateTimeOffset> OverflowAlleTimer(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var set = new HashSet<DateTimeOffset>();
        var u = fromUtc.UtcDateTime;
        var startHour = new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
        for (var h = startHour; h < toUtc; h = h.AddHours(1))
        {
            set.Add(h);
        }
        return set;
    }

    [Fact]
    public void Trip_Innenfor_Vakt_Onsdag_Beregner_ROI_Med_Overlop()
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

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(ev.EndUtc, counterfactualEnd);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, installertEffektMw: 2.2,
            snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5,
            overflowHours: overflow, overflowDataAvailable: true);

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

        // Overløps-telling jobber på time-presisjon:
        // 17:00 (start floor) → 08:00 (end floor) = 15 hele timer.
        r.OverflowTimerInCounterfactual.Should().Be(15);
        r.OverflowDataMissing.Should().BeFalse();

        // ROI = 15 × 2.2 × 0.5 × 850 = 14 025 NOK
        r.ReddetMwh.Should().BeApproximately(15 * 2.2 * 0.5, 0.01);
        r.ReddetNok.Should().BeApproximately(15 * 2.2 * 0.5 * 850, 1.0);
        r.ReddetProduksjon_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 850, 1.0);
        r.ReddetUbalanse_NOK.Should().Be(0);  // ingen ubalanse-tillegg → bare produksjon
    }

    [Fact]
    public void Trip_Uten_Overlop_Gir_Null_ROI()
    {
        // Samme trip som over, men ingen overløp i counterfactual-perioden →
        // vannet er trygt magasinert, vakten redder ingenting.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850, 0.5,
            overflowHours: new HashSet<DateTimeOffset>(),
            overflowDataAvailable: true);

        var r = roi[0];
        r.ErInnenforVakt.Should().BeTrue();
        r.ErReddbar.Should().BeTrue();
        r.EkstraTimerSpart.Should().BeApproximately(14.5, 0.01);
        r.OverflowTimerInCounterfactual.Should().Be(0);
        r.OverflowDataMissing.Should().BeFalse();
        r.ReddetMwh.Should().Be(0);
        r.ReddetNok.Should().Be(0);
        r.Forklaring.Should().Contain("ingen overløp");
    }

    [Fact]
    public void Trip_Med_Overlop_Halve_Counterfactual_Gir_Halv_ROI()
    {
        // Trip onsdag 16:00-17:30 lokal. Counterfactual = torsdag 08:00.
        // 6 av 15 mulige overløps-timer registrert.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        // Plukk seks vilkårlige timer i counterfactual-vinduet med overløp.
        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var alle = OverflowAlleTimer(ev.EndUtc, counterfactualEnd).ToList();
        var halvt = alle.Take(6).ToHashSet();

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850, 0.5,
            overflowHours: halvt, overflowDataAvailable: true);

        var r = roi[0];
        r.OverflowTimerInCounterfactual.Should().Be(6);
        r.ReddetMwh.Should().BeApproximately(6 * 2.2 * 0.5, 0.01);
        r.ReddetNok.Should().BeApproximately(6 * 2.2 * 0.5 * 850, 1.0);
    }

    [Fact]
    public void Mangler_Overlop_Data_Konservativ_Antagelse()
    {
        // SCADA-data mangler — vi flagger eventet og setter ROI = 0.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850, 0.5,
            overflowHours: new HashSet<DateTimeOffset>(),
            overflowDataAvailable: false);

        var r = roi[0];
        r.OverflowDataMissing.Should().BeTrue();
        r.OverflowTimerInCounterfactual.Should().Be(0);
        r.ReddetNok.Should().Be(0);
        r.Forklaring.Should().Contain("SCADA mangler");
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
        roi[0].OverflowDataMissing.Should().BeFalse();
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
        roi[0].OverflowDataMissing.Should().BeFalse();
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

        // Selv med overløp i hele perioden — ekstraTimer = 0 betyr ingen ROI.
        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850, 0.5,
            overflowHours: OverflowAlleTimer(ev.StartUtc, ev.EndUtc),
            overflowDataAvailable: true);

        roi[0].ErInnenforVakt.Should().BeTrue();
        roi[0].ErReddbar.Should().BeTrue();
        roi[0].EkstraTimerSpart.Should().Be(0);
        roi[0].OverflowTimerInCounterfactual.Should().Be(0);
        roi[0].ReddetNok.Should().Be(0);
    }

    [Fact]
    public void Helg_Trip_Hopper_Til_Mandag_0800_Med_Overlop()
    {
        // Lørdag 7. feb 2026 kl 12:00 → mandag 9. feb 08:00 = 43 t ekstra
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 7, 12),
            EndUtc = OsloLokal(2026, 2, 7, 13),  // vakt fikset på 1 time
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };

        var counterfactualEnd = OsloLokal(2026, 2, 9, 8);
        var overflow = OverflowAlleTimer(ev.EndUtc, counterfactualEnd);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev }, 2.2, 850, 0.5,
            overflowHours: overflow, overflowDataAvailable: true);

        roi[0].EkstraTimerSpart.Should().BeApproximately(43.0, 0.01); // 13:00 lørdag → 08:00 mandag
        roi[0].OverflowTimerInCounterfactual.Should().Be(43);
        roi[0].ReddetNok.Should().BeGreaterThan(0);
    }

    // ---- v3: ubalanse-komponent ------------------------------------------

    [Fact]
    public void V3_Trip_Med_Overlop_Og_Ubalansetillegg_Beregner_Begge_Komponenter()
    {
        // Onsdag 16:00-17:30 lokal, full overlap-dekning, snitt-RK-tillegg = 200 NOK/MWh
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };
        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(ev.EndUtc, counterfactualEnd);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            installertEffektMw: 2.2, snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5,
            overflowHours: overflow, overflowDataAvailable: true,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        // Produksjon: 15 t × 2.2 × 0.5 × 850 = 14 025 NOK
        r.ReddetProduksjon_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 850, 1.0);
        // Ubalanse: 14.5 t (ekstra) × 2.2 × 0.5 × 200 = 3 190 NOK
        r.ReddetUbalanse_NOK.Should().BeApproximately(14.5 * 2.2 * 0.5 * 200, 1.0);
        // Total = sum
        r.ReddetNok.Should().BeApproximately(r.ReddetProduksjon_NOK + r.ReddetUbalanse_NOK, 0.01);
        r.Forklaring.Should().Contain("Ubalanse-gebyr");
    }

    [Fact]
    public void V3_Trip_Uten_Overlop_Med_Ubalansetillegg_Gir_Bare_Ubalanse_ROI()
    {
        // Selv uten overløp (vannet trygt magasinert) reddet vakten ubalanse-gebyret
        // for de timene plant'en var forpliktet til å levere.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            installertEffektMw: 2.2, snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5,
            overflowHours: new HashSet<DateTimeOffset>(), overflowDataAvailable: true,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        r.ReddetProduksjon_NOK.Should().Be(0);
        r.ReddetUbalanse_NOK.Should().BeApproximately(14.5 * 2.2 * 0.5 * 200, 1.0);
        r.ReddetNok.Should().BeApproximately(14.5 * 2.2 * 0.5 * 200, 1.0);
        r.Forklaring.Should().Contain("ingen overløp");
        r.Forklaring.Should().Contain("ubalanse-gebyret");
    }

    [Fact]
    public void V3_Mangler_Overlop_Data_Men_Med_Ubalansetillegg_Gir_Ubalanse_ROI()
    {
        // SCADA-data mangler for OverflowFlow, men ubalanse-tillegg gjelder uansett.
        // Vakt-tjenesten reddet ubalanse-gebyret selv om vi ikke kan kvantifisere
        // produksjonsdelen.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            installertEffektMw: 2.2, snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5,
            overflowHours: new HashSet<DateTimeOffset>(), overflowDataAvailable: false,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        r.OverflowDataMissing.Should().BeTrue();
        r.ReddetProduksjon_NOK.Should().Be(0);
        r.ReddetUbalanse_NOK.Should().BeApproximately(14.5 * 2.2 * 0.5 * 200, 1.0);
        r.Forklaring.Should().Contain("SCADA mangler");
        r.Forklaring.Should().Contain("Ubalanse");
    }

    [Fact]
    public void V3_Negativ_Ubalansetillegg_Kastes()
    {
        // Negativt tillegg er ikke meningsfullt — kontrakten kaster.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 2,
        };

        var calc = new VaktRoiCalculator();
        FluentActions.Invoking(() => calc.Calculate(new[] { ev },
            installertEffektMw: 2.2, snittSpotprisNokMwh: 850, kapasitetsfaktor: 0.5,
            snittUbalansetillegg_NokMwh: -100))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
