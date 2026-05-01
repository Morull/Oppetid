using FluentAssertions;
using KraftverkUptime.Modules.Reporting.Produksjon;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Produksjon;

/// <summary>
/// Unit-tester for <see cref="ProduksjonAnalyseCalculator"/> per
/// SPEC-PRODUKSJON-FIX akseptansekriterium #1. Pure-funksjon — ingen DB.
///
/// Hver test bruker hardkodet input og verifiserer eksakte forventede
/// verdier. Verifiserer at:
///   - Eksisterende formler er stabile (regresjon)
///   - Nye time-andel-felt (B1 fra spec) beregnes korrekt
///   - Edge cases håndteres (tom liste, negativ spot, manglende plan)
/// </summary>
public class ProduksjonAnalyseCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private const string PlantId = "test-plant";

    private static ProduksjonAnalyseCalculator.HourlyInput Hour(
        int offset, double? plan, double? elhub, double? spot)
        => new(T0.AddHours(offset), plan, elhub, spot);

    private static ProduksjonAnalyseResult Run(
        IReadOnlyList<ProduksjonAnalyseCalculator.HourlyInput> rows,
        DateTimeOffset? to = null)
        => ProduksjonAnalyseCalculator.Compute(
            PlantId, T0, to ?? T0.AddHours(rows.Count > 0 ? rows.Count : 1), rows);

    [Fact]
    public void Compute_TomtInput_ReturnererNullKpi()
    {
        var r = Run(Array.Empty<ProduksjonAnalyseCalculator.HourlyInput>());

        r.AntallTimer.Should().Be(0);
        r.AntallTimerMedPlan.Should().Be(0);
        r.AntallTimerProduksjon.Should().Be(0);
        r.PlanTreffProsent.Should().Be(0);
        r.AndelProdIToppKvartil.Should().Be(0);
        r.AndelTimerProdIToppKvartil.Should().Be(0);
        r.HydrogridMerverdiNok.Should().Be(0);
        r.FaktiskMerverdiNok.Should().Be(0);
        r.SnittSpotprisNokMwh.Should().Be(0);
        r.Hourly.Should().BeEmpty();
        r.Monthly.Should().BeEmpty();
    }

    [Fact]
    public void Compute_KunPlan_NoElhub_PlanTreffNull()
    {
        // 24 timer plan = 1 MWh, men 0 produksjon hver time
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1.0, elhub: 0, spot: 500))
            .ToList();

        var r = Run(rows);

        r.AntallTimerMedPlan.Should().Be(24); // alle har plan > 0
        r.AntallTimerProduksjon.Should().Be(0); // ingen elhub > 0
        // PlanTreff = 1 - sum(|0-1|)/sum(1) = 1 - 24/24 = 0
        r.PlanTreffProsent.Should().Be(0);
    }

    [Fact]
    public void Compute_PerfektTreff_PlanLikElhub()
    {
        // 24 timer der elhub = plan
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1.0, elhub: 1.0, spot: 500))
            .ToList();

        var r = Run(rows);

        r.PlanTreffProsent.Should().BeApproximately(1.0, 1e-9);
        r.AntallTimerMedPlan.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(24);
    }

    [Fact]
    public void Compute_AlleTimerIToppKvartil_AndelTopp100()
    {
        // 24 timer, alle med ulik spotpris. Plan + elhub er 1 MWh
        // bare for de 6 timene med høyest pris (top-25%-kvartil = 24/4 = 6).
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            // Spot stiger fra 100 til 100+23×100=2400. Topp-6 har spot ≥ 1900.
            // De er timer 18-23 (de 6 høyeste).
            var spot = 100.0 + i * 100;
            var produserer = i >= 18; // bare topp-6
            rows.Add(Hour(i,
                plan: produserer ? 1.0 : 0,
                elhub: produserer ? 1.0 : 0,
                spot: spot));
        }

        var r = Run(rows);

        // All elhub-volum (6 MWh) er i topp-kvartilen
        r.AndelProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
        // Alle drifts-timer (6 stk) er i topp-kvartil
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_KortPeriode_FireTimer_KvartilSize1()
    {
        // 4 timer → kvartil = max(1, 4/4) = 1
        var rows = new[]
        {
            Hour(0, plan: 1, elhub: 1, spot: 100),
            Hour(1, plan: 1, elhub: 1, spot: 200),
            Hour(2, plan: 1, elhub: 1, spot: 300),
            Hour(3, plan: 1, elhub: 1, spot: 400), // dette er topp-1
        };

        var r = Run(rows);

        // 1 av 4 timer er topp = 25 %; volum-andel = 1/4 = 25 %
        r.AndelProdIToppKvartil.Should().BeApproximately(0.25, 1e-9);
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Compute_NegativSpotpris_HandteresKorrekt()
    {
        // 24 timer der noen har negativ spot
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = i < 6 ? -50.0 : 100.0 + i * 50;
            rows.Add(Hour(i, plan: 1, elhub: 1, spot: spot));
        }

        var r = Run(rows);

        // Beregningen skal fullføre uten exception
        r.AntallTimer.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(24);
        // De 6 negative timene er bunn-kvartil
        r.AndelProdIBunnKvartil.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Compute_ManglendeSpot_TimerEkskluderes()
    {
        // 24 timer, halvparten mangler spot
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            rows.Add(Hour(i, plan: 1, elhub: 1, spot: i < 12 ? 500 : (double?)null));
        }

        var r = Run(rows);

        // Kvartil-beregninger bruker bare 12 timer med spot
        // Plan-treff er over alle 24 timer (alle har plan>0 og elhub=plan)
        r.PlanTreffProsent.Should().BeApproximately(1.0, 1e-9);
        // SnittSpot beregnes over 12 timer = 500
        r.SnittSpotprisNokMwh.Should().BeApproximately(500, 1e-9);
    }

    [Fact]
    public void Compute_FlatSpotpris_AlleTimerLikePris_MerverdiNull()
    {
        // 24 timer med konstant spot = 1000 NOK/MWh
        // Da har timing-strategi ingen effekt → merverdi = 0
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1, elhub: 1, spot: 1000.0))
            .ToList();

        var r = Run(rows);

        r.HydrogridMerverdiNok.Should().BeApproximately(0, 1e-6);
        r.FaktiskMerverdiNok.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void Compute_OptimalTiming_AllProdIHoyestePris_HgMerverdiPositiv()
    {
        // 24 timer. All produksjon i topp-6 (høyeste priser).
        // Hg-merverdi skal være positiv (Plan flytter volum til høypris).
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100; // 100 → 2400
            var planAndElhub = i >= 18 ? 4.0 : 0.0;
            rows.Add(Hour(i, plan: planAndElhub, elhub: planAndElhub, spot: spot));
        }

        var r = Run(rows);

        r.HydrogridMerverdiNok.Should().BeGreaterThan(0);
        r.AndelProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_DarligTiming_AllProdILavestePris_HgMerverdiNegativ()
    {
        // 24 timer. All produksjon i bunn-6.
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            var planAndElhub = i < 6 ? 4.0 : 0.0;
            rows.Add(Hour(i, plan: planAndElhub, elhub: planAndElhub, spot: spot));
        }

        var r = Run(rows);

        r.HydrogridMerverdiNok.Should().BeLessThan(0);
        r.AndelProdIBunnKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_KrysserManedsskifte_GirToMonthly()
    {
        // 48 timer som krysser månedsskifte: 24 timer feb-28 + 24 timer mars-1
        var start = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 48)
            .Select(i => new ProduksjonAnalyseCalculator.HourlyInput(
                start.AddHours(i), PlanMwh: 1, ElhubMwh: 1, SpotprisNokMwh: 500))
            .ToList();

        var r = ProduksjonAnalyseCalculator.Compute(
            PlantId, start, start.AddHours(48), rows);

        r.Monthly.Should().HaveCount(2);
        r.Monthly[0].Year.Should().Be(2026);
        r.Monthly[0].Month.Should().Be(2);
        r.Monthly[1].Month.Should().Be(3);
    }

    [Fact]
    public void Compute_TimeAndelOgVolumAndel_KomplementaereMen_Distinkt()
    {
        // Når all produksjon ligger i topp-6 har vi:
        //   AndelProdIToppKvartil  = 1.0 (alt MWh-volum er der)
        //   AndelTimerProdIToppKvartil = 1.0 (alle drifts-timer er der)
        //
        // Men hvis vi sprer prod-volum ujevnt: stor MWh i én topp-time +
        // små MWh i mange bunn-timer, divergerer de to målene.
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            double? elhub;
            if (i == 23) elhub = 100.0;     // én "stor" time i topp
            else if (i < 6) elhub = 1.0;    // 6 "små" timer i bunn
            else elhub = 0.0;
            rows.Add(Hour(i, plan: elhub, elhub: elhub, spot: spot));
        }

        var r = Run(rows);

        // MWh-volum: 100 MWh i topp av total 106 MWh = 94 %
        r.AndelProdIToppKvartil.Should().BeApproximately(100.0 / 106.0, 1e-6);
        // Drifts-timer: 1 av 7 prod-timer er i topp = 14 %
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(1.0 / 7.0, 1e-6);
        // Tallene divergerer — det er hele poenget med å ha begge
        r.AndelProdIToppKvartil.Should().NotBeApproximately(r.AndelTimerProdIToppKvartil, 0.5);
    }

    [Fact]
    public void Compute_AntallTimerProduksjon_TellerKunElhubPositive()
    {
        // 24 timer der bare 10 har elhub > 0
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            rows.Add(Hour(i, plan: 1, elhub: i < 10 ? 1 : 0, spot: 500));
        }

        var r = Run(rows);

        r.AntallTimerProduksjon.Should().Be(10);
        r.KapasitetsutnyttelseProsent.Should().BeApproximately(10.0 / 24.0, 1e-9);
    }

    [Fact]
    public void Compute_OverflowHours_TellerKorrekt()
    {
        // 24 timer + 5 av dem markert som overløp via overflowHours-set
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1, elhub: 1, spot: 500))
            .ToList();

        var overflowHours = new HashSet<DateTimeOffset>(
            Enumerable.Range(0, 5).Select(i => T0.AddHours(i)));

        var r = ProduksjonAnalyseCalculator.Compute(
            PlantId, T0, T0.AddHours(24), rows, overflowHours, overlopDataTilgjengelig: true);

        r.AntallTimerOverlop.Should().Be(5);
        r.OverlopProsent.Should().BeApproximately(5.0 / 24.0, 1e-9);
        r.OverlopDataTilgjengelig.Should().BeTrue();
    }
}
