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
///
/// Etter signatur-endring 2026-05-19 tar <see cref="VaktRoiCalculator.Calculate"/>
/// en plan-dictionary per UTC-time istedenfor flat (installertEffekt × kapasitetsfaktor).
/// Testene bruker hjelperen <see cref="PlanFlat"/> for å bygge en konstant plan-verdi
/// per time, slik at de gamle test-forventningene (basert på flat utnyttelse) fortsatt
/// holder math-ekvivalent.
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

    /// <summary>
    /// Bygger en plan-dictionary med konstant MWh-verdi for hver hel klokketime i
    /// counterfactual-vinduet (typisk fra eventets start til neste arbeidsdag 08:00).
    /// Returnerer "installert × kapasitetsfaktor" som flat verdi per time så
    /// summen i den nye plan-baserte formelen matcher de gamle test-forventningene.
    /// </summary>
    private static IReadOnlyDictionary<DateTimeOffset, double> PlanFlat(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, double mwhPerHour)
    {
        var dict = new Dictionary<DateTimeOffset, double>();
        var u = fromUtc.UtcDateTime;
        var startHour = new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
        // Utvid forover for å dekke counterfactual-end (max ~3 dager etter event)
        var endHour = toUtc.AddDays(3);
        for (var h = startHour; h < endHour; h = h.AddHours(1))
        {
            dict[h] = mwhPerHour;
        }
        return dict;
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
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

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

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
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
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: halvt,
            overflowDataAvailable: true);

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

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
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

        var plan = PlanFlat(ev.StartUtc, ev.EndUtc.AddDays(2), 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan);

        roi[0].ErInnenforVakt.Should().BeFalse();
        roi[0].ReddetNok.Should().Be(0);
        roi[0].OverflowDataMissing.Should().BeFalse();
    }

    /// <summary>
    /// Drifts-leders presisering 2026-05-05: Hvis en hendelse starter i ordinær
    /// arbeidstid (08-15 hverdag) og varer over inn i vakt-vinduet, regnes det
    /// IKKE som vakt-redning — drifts-personellet jobber overtid for å fikse
    /// det. Eksempel: hendelse starter 13:00 og varer til 20:00 → ingen ROI
    /// fordi drifts-personellet håndterer overtid (typisk opp til 23:00).
    /// </summary>
    [Fact]
    public void Trip_Starter_I_Arbeidstid_Varer_Inn_I_Vakt_Vindu_Gir_Ingen_ROI()
    {
        // Onsdag 4. feb 2026 13:00 → 20:00 lokal.
        // Vakt-vinduet starter 15:00 hverdag, så event krysser inn i vakt-tid.
        // Men siden eventet startet i arbeidstid, har drifts-personellet
        // ansvar for å håndtere det via overtid — vakta blir ikke kalt ut.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 13),  // arbeidstid
            EndUtc = OsloLokal(2026, 2, 4, 20),    // 7 t outage, krysser 15:00-grensen
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "operlog:fault",
            TapMwh = 7.0, TapNok = 5950, TimerSettlement = 7,
        };

        // Selv med rikelig overflow-data og ubalansetillegg skal ROI være 0
        var counterfactual = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(ev.StartUtc, counterfactual);
        var plan = PlanFlat(ev.StartUtc, counterfactual, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        r.ErInnenforVakt.Should().BeFalse(
            "event startet i arbeidstid (13:00 hverdag) — drifts-personell håndterer overtid");
        r.ReddetNok.Should().Be(0);
        r.ReddetProduksjon_NOK.Should().Be(0);
        r.ReddetUbalanse_NOK.Should().Be(0);
        r.EkstraTimerSpart.Should().Be(0);
        r.Forklaring.Should().Contain("ordinær arbeidstid");
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

        var plan = PlanFlat(ev.StartUtc, ev.EndUtc.AddDays(2), 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan);

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
        var plan = PlanFlat(ev.StartUtc, ev.EndUtc, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
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
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

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
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        // Produksjon: 15 t × 2.2 × 0.5 × 850 = 14 025 NOK
        r.ReddetProduksjon_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 850, 1.0);
        // Ubalanse: 15 hele timer (gulv-kvantisert) × 2.2 × 0.5 × 200 = 3 300 NOK
        // (gammel formel brukte 14.5 t kontinuerlig, men plan summeres per hele time
        // — counterfactual-vinduet dekker timene 16:00..07:00 = 16 hele timer minus
        // outage-timene 16-17, dvs. 15 timer plan-bidrag).
        r.ReddetUbalanse_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 200, 1.0);
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

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: new HashSet<DateTimeOffset>(),
            overflowDataAvailable: true,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        r.ReddetProduksjon_NOK.Should().Be(0);
        // Plan summeres per hele time: 15 timer i counterfactual-vinduet utenfor outage.
        r.ReddetUbalanse_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 200, 1.0);
        r.ReddetNok.Should().BeApproximately(15 * 2.2 * 0.5 * 200, 1.0);
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

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: new HashSet<DateTimeOffset>(),
            overflowDataAvailable: false,
            snittUbalansetillegg_NokMwh: 200);

        var r = roi[0];
        r.OverflowDataMissing.Should().BeTrue();
        r.ReddetProduksjon_NOK.Should().Be(0);
        // 15 hele timer plan-bidrag (gulv-kvantisert) × 2.2 × 0.5 × 200
        r.ReddetUbalanse_NOK.Should().BeApproximately(15 * 2.2 * 0.5 * 200, 1.0);
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

        var plan = PlanFlat(ev.StartUtc, ev.EndUtc.AddDays(2), 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        FluentActions.Invoking(() => calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            snittUbalansetillegg_NokMwh: -100))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Drifts-leders 2026-05-04-bug: hvis det oppstår flere events i samme
    /// vakt-vindu (eks. 21.02.2026 helg-callout), skal IKKE alle telles som
    /// selvstendige ROI-er. Vakta er allerede ute — ekstra hendelser i samme
    /// helg øker ikke omfanget.
    /// </summary>
    [Fact]
    public void Helg_Flere_Events_I_Samme_Vakt_Vindu_Telles_Som_Ett_Callout()
    {
        // Lørdag 21.02.2026 — to events samme helg
        var event1 = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 21, 16),  // lørdag 16:00
            EndUtc = OsloLokal(2026, 2, 21, 17),    // 1 t outage
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "operlog:fault",
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };
        var event2 = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 22, 14),  // søndag 14:00
            EndUtc = OsloLokal(2026, 2, 22, 15),    // 1 t outage
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "operlog:fault",
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };

        // Begge har samme counterfactualEnd: mandag 23.02 08:00 lokal
        var counterfactualEnd = OsloLokal(2026, 2, 23, 8);
        var overflow = OverflowAlleTimer(event1.StartUtc, counterfactualEnd);
        var plan = PlanFlat(event1.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { event1, event2 },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        roi.Should().HaveCount(2);
        var leader = roi.First(r => ReferenceEquals(r.Event, event1));
        var member = roi.First(r => ReferenceEquals(r.Event, event2));

        // Leder samler hele gruppe-ROI
        leader.ErReddbar.Should().BeTrue();
        leader.EkstraTimerSpart.Should().BeApproximately(38, 0.01,
            "lør 16:00 → man 08:00 = 40 t, minus 2 t faktisk outage = 38 t");
        leader.ReddetNok.Should().BeGreaterThan(0);

        // Medlem skal ha 0 ROI med eksplisitt forklaring
        member.ErReddbar.Should().BeTrue();
        member.EkstraTimerSpart.Should().Be(0);
        member.ReddetNok.Should().Be(0);
        member.Forklaring.Should().Contain("Samme vakt-callout");
    }

    [Fact]
    public void Helg_Tre_Events_Med_Overlapp_Tellers_Korrekt_I_Union()
    {
        // Tre events i samme helg, der event 2 og 3 overlapper i tid.
        var e1 = MakeEvent(OsloLokal(2026, 2, 21, 16), OsloLokal(2026, 2, 21, 17)); // 1t
        var e2 = MakeEvent(OsloLokal(2026, 2, 22, 10), OsloLokal(2026, 2, 22, 12)); // 2t
        var e3 = MakeEvent(OsloLokal(2026, 2, 22, 11), OsloLokal(2026, 2, 22, 13)); // 2t (overlapper med e2)
        // Union av outage = {16-17, 10-13} = 1t + 3t = 4t totalt

        var counterfactualEnd = OsloLokal(2026, 2, 23, 8);
        var overflow = OverflowAlleTimer(e1.StartUtc, counterfactualEnd);
        var plan = PlanFlat(e1.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { e1, e2, e3 },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        var leader = roi.First(r => ReferenceEquals(r.Event, e1));
        // Vindu = lør 16:00 → man 08:00 = 40 t. Minus 4 t union = 36 t.
        leader.EkstraTimerSpart.Should().BeApproximately(36, 0.01);

        // De to andre er medlemmer
        roi.Where(r => ReferenceEquals(r.Event, e2) || ReferenceEquals(r.Event, e3))
            .Should().AllSatisfy(r => r.EkstraTimerSpart.Should().Be(0));
    }

    [Fact]
    public void Events_I_Forskjellige_Vakt_Vinduer_Tellers_Selvstendig()
    {
        // To events i forskjellige helger — hver sin counterfactual og ROI
        var e1 = MakeEvent(OsloLokal(2026, 2, 14, 16), OsloLokal(2026, 2, 14, 17)); // helg 1
        var e2 = MakeEvent(OsloLokal(2026, 2, 21, 16), OsloLokal(2026, 2, 21, 17)); // helg 2

        var counterfactual2 = OsloLokal(2026, 2, 23, 8);
        var overflow = OverflowAlleTimer(e1.StartUtc, counterfactual2);
        var plan = PlanFlat(e1.StartUtc, counterfactual2, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { e1, e2 },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        // Begge er ledere for sin gruppe — begge får full ROI
        roi.Should().HaveCount(2);
        roi[0].EkstraTimerSpart.Should().BeApproximately(39, 0.01,
            "helg 1: lør 16:00 → man 08:00 = 40 t, minus 1 t = 39 t");
        roi[1].EkstraTimerSpart.Should().BeApproximately(39, 0.01, "samme math for helg 2");
    }

    [Fact]
    public void Forskjellige_Anlegg_Samme_Vindu_Tellers_Selvstendig()
    {
        // Drivdal-event og Haukland-event samme helg = forskjellige vakt-callouts
        // (eller i hvert fall forskjellige anlegg — ROI per plant er separat).
        var e1 = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 21, 16),
            EndUtc = OsloLokal(2026, 2, 21, 17),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };
        var e2 = new DowntimeEvent
        {
            PlantId = "haukland",
            StartUtc = OsloLokal(2026, 2, 21, 18),
            EndUtc = OsloLokal(2026, 2, 21, 19),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };

        var counterfactual = OsloLokal(2026, 2, 23, 8);
        var overflow = OverflowAlleTimer(e1.StartUtc, counterfactual);
        var plan = PlanFlat(e1.StartUtc, counterfactual, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { e1, e2 },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        // Begge får sin egen ROI siden de er forskjellige plants
        roi.Should().HaveCount(2);
        roi.Should().AllSatisfy(r =>
        {
            r.ErReddbar.Should().BeTrue();
            r.EkstraTimerSpart.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void PlanDataPartial_FlaggesNaarProxyHoursTreffer_CounterfactualVindu()
    {
        // Event onsdag kveld → counterfactual = torsdag 08:00.
        // Vi gir plan for hele vinduet, men markerer noen av timene som proxy.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 18),
            EndUtc = OsloLokal(2026, 2, 4, 19),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };
        var cfEnd = OsloLokal(2026, 2, 5, 8);
        var plan = PlanFlat(ev.StartUtc, cfEnd, mwhPerHour: 1.1);
        var overflow = OverflowAlleTimer(ev.EndUtc, cfEnd);

        // Marker tre timer i counterfactual-perioden som proxy.
        var proxyHours = new HashSet<DateTimeOffset>
        {
            OsloLokal(2026, 2, 5, 5),
            OsloLokal(2026, 2, 5, 6),
            OsloLokal(2026, 2, 5, 7),
        };

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            proxyHours: proxyHours);

        roi[0].PlanDataPartial.Should().BeTrue();
        roi[0].Forklaring.Should().Contain("proxy");
    }

    private static DowntimeEvent MakeEvent(DateTimeOffset start, DateTimeOffset end)
        => new()
        {
            PlantId = "drivdal",
            StartUtc = start,
            EndUtc = end,
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "operlog:fault",
            TapMwh = 1.0, TapNok = 850, TimerSettlement = 1,
        };

    /// <summary>
    /// B1 (2026-05-20): <c>ActualEndOverrideUtc</c> anvendes oppstrøms for
    /// <see cref="VaktRoiCalculator"/> ved at endepunktet bygger om event-listen
    /// med ny <c>EndUtc</c> før <c>Calculate</c> kalles. Denne testen simulerer
    /// det ved å kjøre Calculate to ganger med samme event men ulik EndUtc og
    /// verifisere at EkstraTimerSpart endres med tilsvarende differanse.
    /// </summary>
    [Fact]
    public void ActualEndOverride_Reduserer_EkstraTimerSpart()
    {
        // Onsdag 4. feb 2026 16:00 — vakten ble varslet.
        // Original (fra SCADA): vakten "løste" på 17:30 (1.5 t)
        // Drifts-leder overstyrer: faktisk slutt var 16:30 (0.5 t)
        var start = OsloLokal(2026, 2, 4, 16);
        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(start, counterfactualEnd);
        var plan = PlanFlat(start, counterfactualEnd, 2.2 * 0.5);

        var calc = new VaktRoiCalculator();

        // Original event: EndUtc = 17:30
        var evOriginal = MakeEvent(start, OsloLokal(2026, 2, 4, 17, 30));
        var roiOriginal = calc.Calculate(new[] { evOriginal },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        // Override simulert: samme event, men EndUtc forskjøvet en time tilbake.
        // (Endepunktet bytter ut EndUtc i events-listen FØR Calculate kalles.)
        var evOverridden = evOriginal with { EndUtc = OsloLokal(2026, 2, 4, 16, 30) };
        var roiOverridden = calc.Calculate(new[] { evOverridden },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        // Ekstra timer øker med 1 time fordi vakten "i realiteten" fikset
        // problemet 1 t tidligere — 15.5 t vs 14.5 t.
        roiOriginal[0].EkstraTimerSpart.Should().BeApproximately(14.5, 0.01);
        roiOverridden[0].EkstraTimerSpart.Should().BeApproximately(15.5, 0.01);

        // Reddet NOK skal også øke proporsjonalt — en hel klokketime mer
        // utenfor outage-vinduet, så plan-sum øker med 1 × 2.2 × 0.5 MWh.
        var diff = roiOverridden[0].ReddetNok - roiOriginal[0].ReddetNok;
        diff.Should().BeApproximately(2.2 * 0.5 * 850, 1.0);
    }

    /// <summary>
    /// Spec NESTE-CHAT-VAKTROI-OG-UI-FIKS.md Del A — monoton-invariant:
    /// For samme periode og samme oppmøte, og to vakt-vindu der A ⊆ B,
    /// må TotalReddet(A) ≤ TotalReddet(B).
    ///
    /// Konkret scenario fra dokumentasjonen: brukerens 15:00-23:00 må aldri
    /// redde mer enn døgnvakt 15:00-07:00 (sistnevnte er superset).
    ///
    /// Rot-årsaken som testen fanger: hvis et event utenfor brukerens
    /// kortere vindu (eks. natt 02:00) faktisk skjedde inne i et annet leder-
    /// events counterfactual-vindu, må den outage-tiden fortsatt trekkes fra
    /// savedHours — ellers gir det smalere vinduet kunstig høyere ROI.
    /// </summary>
    [Fact]
    public void Monoton_Invariant_KortereVinduRedderAldriMer()
    {
        // Mandag 2026-02-02 — to events samme natt:
        //   A: 22:00 mandag, varighet 1 t  → 22:00-23:00
        //   B: 02:00 tirsdag, varighet 1 t → 02:00-03:00
        // Begge har counterfactual = tirsdag 08:00.
        var events = new[]
        {
            MakeEvent(OsloLokal(2026, 2, 2, 22), OsloLokal(2026, 2, 2, 23)),
            MakeEvent(OsloLokal(2026, 2, 3, 2),  OsloLokal(2026, 2, 3, 3)),
        };
        var from = OsloLokal(2026, 2, 2, 22);
        var to = OsloLokal(2026, 2, 3, 8);
        var plan = PlanFlat(from, to, mwhPerHour: 1.0);
        var overflow = OverflowAlleTimer(from, to);

        var calc = new VaktRoiCalculator();

        // Vindu B = døgnvakt 15-07 (default — fanger begge events)
        var roiB = calc.Calculate(events,
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        // Vindu A = brukerens 15-23 (kun event A er innenfor)
        var custom = new VaktTidsmodellOptions(
            EttermiddagStart: new TimeSpan(15, 0, 0),
            MorgenCutoff: new TimeSpan(23, 0, 0),
            OppmoteTidspunkt: new TimeSpan(8, 0, 0),
            VaktResponstid: TimeSpan.FromHours(1));
        var roiA = calc.Calculate(events,
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            vaktOptions: custom);

        var totalA = roiA.Sum(r => r.ReddetNok);
        var totalB = roiB.Sum(r => r.ReddetNok);
        var reddbareA = roiA.Count(r => r.ErReddbar && r.ReddetNok > 0);
        var reddbareB = roiB.Count(r => r.ReddetNok > 0);

        totalA.Should().BeLessThanOrEqualTo(totalB,
            "monoton-invariant: A ⊆ B → TotalReddet(A) ≤ TotalReddet(B).");
        reddbareA.Should().BeLessThanOrEqualTo(reddbareB,
            "antall events med ROI skal også være monoton: A ⊆ B → tellingen i A ≤ B.");
    }

    [Fact]
    public void Monoton_Invariant_HoldesAvUtenforVaktBidragTilOutageSet()
    {
        // Verifiserer spesifikt at outage-set inkluderer events som er
        // UTENFOR brukerens vindu, så savedHours ikke kunstig blir høyere
        // når brukerens vindu smalere.
        //
        // Setup: Event A 22:00 mandag (innenfor BÅDE 15-07 OG 15-23).
        //        Event B 02:00 tirsdag (innenfor 15-07, UTENFOR 15-23).
        // Begge gruppes på counterfactual tirsdag 08:00.
        //
        // Window = 22:00 → 08:00 = 10 t.
        // Outage = 1 t (A) + 1 t (B) = 2 t.
        // savedHours = 10 - 2 = 8 t (FOR BEGGE).
        //
        // Bug: før fiksen ble savedHours for custom 15-23 beregnet til 9 t
        // (kun A i merged-listen → totalOutageHours=1), som ga A høyere ROI
        // i custom-modus enn default-modus.
        var events = new[]
        {
            MakeEvent(OsloLokal(2026, 2, 2, 22), OsloLokal(2026, 2, 2, 23)),
            MakeEvent(OsloLokal(2026, 2, 3, 2),  OsloLokal(2026, 2, 3, 3)),
        };
        var from = OsloLokal(2026, 2, 2, 22);
        var to = OsloLokal(2026, 2, 3, 8);
        var plan = PlanFlat(from, to, mwhPerHour: 1.0);
        var overflow = OverflowAlleTimer(from, to);

        var calc = new VaktRoiCalculator();

        var roiDefault = calc.Calculate(events,
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true);

        var custom = new VaktTidsmodellOptions(
            EttermiddagStart: new TimeSpan(15, 0, 0),
            MorgenCutoff: new TimeSpan(23, 0, 0),
            OppmoteTidspunkt: new TimeSpan(8, 0, 0),
            VaktResponstid: TimeSpan.FromHours(1));
        var roiCustom = calc.Calculate(events,
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            vaktOptions: custom);

        // Event A er leder i begge tilfeller. SavedHours skal være IDENTISK
        // siden outage-tidslinjen for plantet i [22:00, 08:00) er den samme.
        var aDefault = roiDefault.First(r => r.Event.StartUtc == events[0].StartUtc);
        var aCustom = roiCustom.First(r => r.Event.StartUtc == events[0].StartUtc);
        aCustom.EkstraTimerSpart.Should().BeApproximately(aDefault.EkstraTimerSpart, 0.001,
            "samme leder-event skal ha samme savedHours uavhengig av om B er innenfor eller utenfor brukerens vindu.");
        aCustom.ReddetMwh.Should().BeApproximately(aDefault.ReddetMwh, 0.001,
            "plan-sum for leder skal ikke vokse når B faller ut av gruppen.");
    }

    // ------------------------------------------------------------------------
    // Spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md (2026-05-22):
    // U2-PlanDeviation-hendelser uten operlog-match skal IKKE telle som
    // vakt-utrykning i Auto-modus. Drifts-leder kan overstyre via Yes/No.
    // Andre cause-koder er upåvirket (regresjons-sjekk).
    // ------------------------------------------------------------------------

    /// <summary>
    /// Bygger excludeFromReddbar-settet slik endepunktene gjør det: kombinerer
    /// event + override-verdi via <see cref="EffectiveGuardResponseEvaluator.ShouldCount"/>.
    /// </summary>
    private static IReadOnlySet<DateTimeOffset> BuildExcludeSet(
        IEnumerable<DowntimeEvent> events,
        IReadOnlyDictionary<DateTimeOffset, GuardResponseOverride> overrides)
    {
        return events
            .Where(e =>
            {
                var ovr = overrides.TryGetValue(e.StartUtc, out var g) ? (GuardResponseOverride?)g : null;
                return !EffectiveGuardResponseEvaluator.ShouldCount(e, ovr);
            })
            .Select(e => e.StartUtc)
            .ToHashSet();
    }

    [Theory]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.Auto, false, false)]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.Auto, true, true)]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.Yes, false, true)]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.Yes, true, true)]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.No, false, false)]
    [InlineData("U2-PlanDeviation", GuardResponseOverride.No, true, false)]
    [InlineData("U1-UnplannedStop", GuardResponseOverride.Auto, false, true)]
    [InlineData("U1-UnplannedStop", GuardResponseOverride.Auto, true, true)]
    public void U2_PlanDeviation_Filter_Matrix(
        string causeCode, GuardResponseOverride ovr, bool harOperlogMatch, bool expectedTellerSomReddbar)
    {
        // Trip onsdag 16:00-17:30 lokal — vakt aktiv, overløp i hele counterfactual.
        // Hendelsen er TripFeil + reddbar-kategori. Eneste varierte forutsetning er
        // EffectiveGuardResponse-utfallet basert på causeCode/override/operlog.
        var ev = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17, 30),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = causeCode,
            HarOperlogMatch = harOperlogMatch,
            TapMwh = 1.0,
            TapNok = 850,
            TimerSettlement = 2,
        };

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(ev.EndUtc, counterfactualEnd);
        var plan = PlanFlat(ev.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var overrides = new Dictionary<DateTimeOffset, GuardResponseOverride>
        {
            [ev.StartUtc] = ovr,
        };
        var excludeFromReddbar = BuildExcludeSet(new[] { ev }, overrides);

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { ev },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            excludeFromReddbar: excludeFromReddbar);

        var r = roi.Should().ContainSingle().Which;

        if (expectedTellerSomReddbar)
        {
            r.ErReddbar.Should().BeTrue(
                "matrise-celle ({0}, {1}, operlog={2}) skal telle som vakt-utrykning",
                causeCode, ovr, harOperlogMatch);
            r.ReddetMwh.Should().BeGreaterThan(0);
            r.ReddetNok.Should().BeGreaterThan(0);
        }
        else
        {
            r.ErReddbar.Should().BeFalse(
                "matrise-celle ({0}, {1}, operlog={2}) skal IKKE telle som vakt-utrykning",
                causeCode, ovr, harOperlogMatch);
            r.ReddetMwh.Should().Be(0);
            r.ReddetNok.Should().Be(0);
            r.EkstraTimerSpart.Should().Be(0);
        }
    }

    [Fact]
    public void U2_Auto_UtenOperlog_TellerFortsattSomOutage_ForAndreVaktEvents()
    {
        // Monoton-invariant-test: en U2-PlanDeviation uten operlog (filtreres ut)
        // skal IKKE inflate ROI-en til en U1-event som ligger like ved i samme
        // vakt-vindu. Outage-tiden fra U2-eventet teller fortsatt selv om den
        // ikke gir ROI selv.
        //
        // Event A (U1): trip 16:00-17:00 — leder, får ROI
        // Event B (U2 uten operlog): plan-deviation 17:30-18:00 — filtreres
        //
        // Begge har counterfactualEnd = torsdag 08:00. B sin outage [17:30, 18:00)
        // skal komme med i merging-passet og dermed kappe A sin SavedHours.
        var a = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 16),
            EndUtc = OsloLokal(2026, 2, 4, 17),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "U1-UnplannedStop",
            HarOperlogMatch = true,
            TapMwh = 0.5, TapNok = 425, TimerSettlement = 1,
        };
        var b = new DowntimeEvent
        {
            PlantId = "drivdal",
            StartUtc = OsloLokal(2026, 2, 4, 17, 30),
            EndUtc = OsloLokal(2026, 2, 4, 18),
            State = UnitState.ForcedOutage,
            Category = DowntimeEventCategory.TripFeil,
            CauseCode = "U2-PlanDeviation",
            HarOperlogMatch = false,    // Auto-modus → filtreres ut
            TapMwh = 0.5, TapNok = 425, TimerSettlement = 1,
        };

        var counterfactualEnd = OsloLokal(2026, 2, 5, 8);
        var overflow = OverflowAlleTimer(OsloLokal(2026, 2, 4, 16), counterfactualEnd);
        var plan = PlanFlat(a.StartUtc, counterfactualEnd, 2.2 * 0.5);

        var excludeFromReddbar = BuildExcludeSet(
            new[] { a, b },
            new Dictionary<DateTimeOffset, GuardResponseOverride>());

        var calc = new VaktRoiCalculator();
        var roi = calc.Calculate(new[] { a, b },
            snittSpotprisNokMwh: 850,
            planByHour: plan,
            overflowHours: overflow,
            overflowDataAvailable: true,
            excludeFromReddbar: excludeFromReddbar);

        var rA = roi.First(r => r.Event.StartUtc == a.StartUtc);
        var rB = roi.First(r => r.Event.StartUtc == b.StartUtc);

        // A er fortsatt reddbar leder.
        rA.ErReddbar.Should().BeTrue();
        rA.ReddetNok.Should().BeGreaterThan(0);

        // B er ekskludert — fortsatt synlig, men null ROI med forklaring som
        // peker drifts-leder mot Detaljer-popup.
        rB.ErReddbar.Should().BeFalse();
        rB.ReddetNok.Should().Be(0);
        rB.Forklaring.Should().Contain("U2-PlanDeviation");
        rB.Forklaring.Should().Contain("operlog");

        // Sanity-sjekk på monoton-invariant: A sin EkstraTimerSpart skal være
        // counterfactual-vindu minus outage fra BÅDE A og B (= [16:00, 17:00)
        // ∪ [17:30, 18:00) = 1.5 t). 16:00 lokal → 08:00 dagen etter = 16 t,
        // savedHours = 16 - 1.5 = 14.5 t.
        rA.EkstraTimerSpart.Should().BeApproximately(14.5, 0.01,
            "B sin outage [17:30, 18:00) skal kuttes fra A sin savedHours selv om B er filtrert fra reddbar-set.");
    }

    [Fact]
    public void EffectiveGuardResponseEvaluator_AndreCauseCodes_ErUendret()
    {
        // Regresjons-sjekk: filteret skal KUN slå inn for U2-PlanDeviation.
        // Andre cause-koder (operlog:fault, operlog:alarm, U1-UnplannedStop,
        // PlanlagtVedlikehold, null) skal alltid kvalifisere — uavhengig av
        // operlog-match og override-verdi (siden override er irrelevant uten
        // U2-filter).
        var causes = new string?[] { "operlog:fault", "operlog:alarm", "U1-UnplannedStop", "PlanlagtVedlikehold", null };
        foreach (var cause in causes)
        {
            var ev = new DowntimeEvent
            {
                PlantId = "drivdal",
                StartUtc = OsloLokal(2026, 2, 4, 16),
                EndUtc = OsloLokal(2026, 2, 4, 17),
                State = UnitState.ForcedOutage,
                Category = DowntimeEventCategory.TripFeil,
                CauseCode = cause,
                HarOperlogMatch = false, // verste tilfelle — filteret bør ikke slå inn likevel
                TapMwh = 0.5, TapNok = 425, TimerSettlement = 1,
            };
            EffectiveGuardResponseEvaluator.ShouldCount(ev, null).Should().BeTrue(
                "cause '{0}' med operlog=false skal fortsatt telle som vakt-utrykning", cause ?? "<null>");
            EffectiveGuardResponseEvaluator.ShouldCount(ev, GuardResponseOverride.Auto).Should().BeTrue(
                "cause '{0}' Auto skal alltid telle (filter gjelder kun U2)", cause ?? "<null>");
            EffectiveGuardResponseEvaluator.ShouldCount(ev, GuardResponseOverride.No).Should().BeTrue(
                "cause '{0}' No-override skal IKKE filtrere ut når cause ikke er U2-PlanDeviation", cause ?? "<null>");
        }
    }
}
