using FluentAssertions;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// 12 unit-tester for <see cref="CaptureRateCalculator"/> per Spec CAPTURE-RATE
/// akseptansekriterium #12. Dekker happy path, edge-cases og persentil-filter.
/// </summary>
public class CaptureRateCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CaptureRateCalculator.HourlyInput Hour(int hourOffset, double mwh, double spot)
        => new(T0.AddHours(hourOffset), mwh, spot, mwh * spot);

    /// <summary>Lager én sammenhengende sekvens timer med konstant mwh og spot.</summary>
    private static IReadOnlyList<CaptureRateCalculator.HourlyInput> ConstSeries(int hours, double mwh, double spot)
    {
        var list = new List<CaptureRateCalculator.HourlyInput>(hours);
        for (var i = 0; i < hours; i++) list.Add(Hour(i, mwh, spot));
        return list;
    }

    [Fact]
    public void HappyPath_AllaTimerKomplette_GirRiktigeAggregater()
    {
        // 24 timer × 1 MWh × 500 NOK/MWh = 12 000 NOK total
        var rows = ConstSeries(24, mwh: 1, spot: 500);
        var r = CaptureRateCalculator.Compute(rows, historicalDailyForPercentile: Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimer.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(24);
        r.CapturePriceNokMwh.Should().BeApproximately(500, 1e-9);
        r.TimesBaselineNokMwh.Should().BeApproximately(500, 1e-9);
        r.TimesCr.Should().BeApproximately(1.0, 1e-9);
        r.MerverdiNok.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void JevnProduksjon_GirCRakkurat1_0()
    {
        var rows = ConstSeries(48, mwh: 2, spot: 800);
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.TimesCr.Should().BeApproximately(1.0, 1e-9);
        r.DagCr.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void AllProduksjon_IHoyprist_Time_GirCRStørre_15()
    {
        // 24 timer total. Vi produserer kun i én time @ 2000 NOK, andre 23 @ 200 NOK.
        // Baseline = (1×2000 + 23×200) / 24 = 270.83
        // CapturePrice = 2000 (alt produsert i den ene timen)
        // CR = 2000 / 270.83 ≈ 7.38
        var rows = new List<CaptureRateCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            if (i == 12) rows.Add(Hour(i, mwh: 1, spot: 2000));
            else rows.Add(new CaptureRateCalculator.HourlyInput(T0.AddHours(i), MwhElhub: 0, SpotprisNokMwh: 200, SpotomsetningNok: 0));
        }
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.CapturePriceNokMwh.Should().BeApproximately(2000, 1e-9);
        r.TimesCr.Should().BeGreaterThan(1.5);
    }

    [Fact]
    public void AllProduksjon_ILavpristTime_GirCRMindre_07()
    {
        // 24 timer: produksjon i én time @ 100 NOK, baseline-snitt = 800 NOK
        var rows = new List<CaptureRateCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            if (i == 3) rows.Add(Hour(i, mwh: 1, spot: 100));
            else rows.Add(new CaptureRateCalculator.HourlyInput(T0.AddHours(i), MwhElhub: 0, SpotprisNokMwh: 800, SpotomsetningNok: 0));
        }
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.CapturePriceNokMwh.Should().BeApproximately(100, 1e-9);
        r.TimesCr.Should().BeLessThan(0.7);
    }

    [Fact]
    public void PersentilFilter_FjernerOutlierDag()
    {
        // Spec NESTE-CHAT-CR-MERVERDI-OPPRYDDING: ny dag-CR bruker
        // Σ(MWh × spot) / Σ(MWh) som teller (samme grunnlag som times-CR).
        // For å lage en outlier-dag må MWh og spot KORRELERE positivt — dvs.
        // produsere mest når prisen er høyest. Setter outlier-dagen med 2 timer:
        // én med MWh=20 ved spot=2500, én med MWh=0 ved spot=500. Snitt-spot=1500,
        // volumvektet-spot=2500 → rå=2500/1500=1,67.
        // Normaldagene har MWh=10 jevnt fordelt over 2 timer ved spot=500/500
        // → volumvektet=500, snitt=500, rå=1.0.
        var hours = new List<CaptureRateCalculator.HourlyInput>();
        for (var d = 0; d < 11; d++)
        {
            var t = T0.AddDays(d);
            if (d == 10)
            {
                // Outlier: produsert MWh og høy spot sammenfaller.
                hours.Add(new(t, MwhElhub: 20, SpotprisNokMwh: 2500, SpotomsetningNok: 20 * 2500));
                hours.Add(new(t.AddHours(1), MwhElhub: 0, SpotprisNokMwh: 500, SpotomsetningNok: 0));
            }
            else
            {
                // Normaldag: jevnt fordelt, lik spot begge timer → rå = 1.0.
                hours.Add(new(t, MwhElhub: 10, SpotprisNokMwh: 500, SpotomsetningNok: 10 * 500));
                hours.Add(new(t.AddHours(1), MwhElhub: 10, SpotprisNokMwh: 500, SpotomsetningNok: 10 * 500));
            }
        }

        // Historikk = 100 normaldager → P5/P95 ≈ [1.0, 1.0]
        var historical = new List<CaptureRateCalculator.DailyInput>();
        for (var d = 0; d < 100; d++)
        {
            historical.Add(new CaptureRateCalculator.DailyInput(
                Date: DateOnly.FromDateTime(T0.AddDays(d).DateTime),
                MwhDay: 20, NokDay: 10000, SpotDayAvg: 500,
                ElhubSpotValueDay: 20 * 500)); // rå = 1.0
        }

        var r = CaptureRateCalculator.Compute(hours, historical);

        r.AntallDager.Should().Be(11);
        r.AntallDagerEtterFilter.Should().BeLessThan(11, "outlier-dagen (rå ≈ 1,67) skal filtreres ut");
    }

    [Fact]
    public void TomTimesliste_GirAlleTomFelter()
    {
        var r = CaptureRateCalculator.Compute(
            Array.Empty<CaptureRateCalculator.HourlyInput>(),
            Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimer.Should().Be(0);
        r.CapturePriceNokMwh.Should().Be(0);
        r.TimesCr.Should().Be(0);
        r.DagCr.Should().Be(0);
        r.MerverdiNok.Should().Be(0);
    }

    [Fact]
    public void Periode_KortereEnn1Dag_GirGyldigeTall()
    {
        var rows = ConstSeries(3, mwh: 1, spot: 500);
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimer.Should().Be(3);
        r.CapturePriceNokMwh.Should().BeApproximately(500, 1e-9);
        r.AntallDager.Should().BeGreaterThan(0); // Minst én dag bucket
    }

    [Fact]
    public void ManglendeSpotpris_IDeler_FilterresUt_FraTimesBaseline()
    {
        // 12 timer med pris, 12 timer uten — baseline beregnes bare over timene som har pris
        var rows = new List<CaptureRateCalculator.HourlyInput>();
        for (var i = 0; i < 12; i++) rows.Add(Hour(i, mwh: 1, spot: 500));
        for (var i = 12; i < 24; i++) rows.Add(new CaptureRateCalculator.HourlyInput(
            T0.AddHours(i), MwhElhub: 0, SpotprisNokMwh: null, SpotomsetningNok: null));

        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimer.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(12);
        r.TimesBaselineNokMwh.Should().BeApproximately(500, 1e-9);
    }

    [Fact]
    public void ManglendeProduksjon_IDeler_GirRiktigCapturePrice()
    {
        // 24 timer alle med spot=500. Bare 4 timer har produksjon (på time 8-11) @ spot=500
        // CapturePrice = 500, TimesBaseline = 500, CR = 1.0
        var rows = new List<CaptureRateCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            if (i is >= 8 and < 12)
                rows.Add(Hour(i, mwh: 1, spot: 500));
            else
                rows.Add(new CaptureRateCalculator.HourlyInput(T0.AddHours(i), MwhElhub: 0, SpotprisNokMwh: 500, SpotomsetningNok: 0));
        }
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimerProduksjon.Should().Be(4);
        r.CapturePriceNokMwh.Should().BeApproximately(500, 1e-9);
        r.TimesCr.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void NegativSpotpris_HandteresKorrekt()
    {
        // Sjelden men hender — negative priser ved overskudd produksjon i NO2.
        // Baseline kan bli negativ; CR-formelen gir fortsatt et tall.
        var rows = new List<CaptureRateCalculator.HourlyInput>
        {
            Hour(0, mwh: 1, spot: -100),
            Hour(1, mwh: 1, spot: 100),
        };
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.CapturePriceNokMwh.Should().BeApproximately(0, 1e-9);
        r.TimesBaselineNokMwh.Should().BeApproximately(0, 1e-9);
        // 0/0 → 0 i vår defensive sti
        r.TimesCr.Should().Be(0);
    }

    [Fact]
    public void KrysserMaanedsskifte_HandterssomEnPeriode()
    {
        // 48 timer som krysser januar/februar
        var rows = new List<CaptureRateCalculator.HourlyInput>();
        var start = new DateTimeOffset(2025, 1, 31, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 48; i++)
        {
            rows.Add(new CaptureRateCalculator.HourlyInput(
                start.AddHours(i), MwhElhub: 1, SpotprisNokMwh: 500, SpotomsetningNok: 500));
        }
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.AntallTimer.Should().Be(48);
        r.CapturePriceNokMwh.Should().BeApproximately(500, 1e-9);
        r.TimesCr.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void RealisertVsSpot_BeregnesSomDifferanseMotSpot()
    {
        // Spec NESTE-CHAT-CR-MERVERDI-OPPRYDDING: utførelses-gapet (tidligere
        // kalt "merverdi") måler om vi faktisk fikk spotverdien av leveransen.
        // Beregnes som Σ(SpotomsetningNok − Elhub × spot).
        //
        // 2 timer:
        //   T0: produsert 1 MWh til 600 NOK (spot 500) → +100 NOK
        //   T1: produsert 1 MWh til 400 NOK (spot 500) → -100 NOK
        // Sum = 0
        var rows = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0, MwhElhub: 1, SpotprisNokMwh: 500, SpotomsetningNok: 600),
            new(T0.AddHours(1), MwhElhub: 1, SpotprisNokMwh: 500, SpotomsetningNok: 400),
        };
        var r = CaptureRateCalculator.Compute(rows, Array.Empty<CaptureRateCalculator.DailyInput>());

        r.RealisertVsSpotNok.Should().BeApproximately(0, 1e-9);

        // Bytt inn 700 i andre time → realisert vs spot = +100 + +200 = +300
        var rows2 = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0, MwhElhub: 1, SpotprisNokMwh: 500, SpotomsetningNok: 600),
            new(T0.AddHours(1), MwhElhub: 1, SpotprisNokMwh: 500, SpotomsetningNok: 700),
        };
        var r2 = CaptureRateCalculator.Compute(rows2, Array.Empty<CaptureRateCalculator.DailyInput>());

        r2.RealisertVsSpotNok.Should().BeApproximately(300, 1e-9);
    }

    [Fact]
    public void Invariant_TimingMerverdiSammeFortegnSomCRMinusEn()
    {
        // Spec NESTE-CHAT-CR-MERVERDI-OPPRYDDING — hovedfunn: CR > 1 ⟺ Timing-merverdi > 0.
        // Verifiserer med tre case:
        //  (a) positiv timing (mest produksjon når spot er høy)
        //  (b) negativ timing (mest produksjon når spot er lav)
        //  (c) jevn produksjon (timing-merverdi = 0, CR = 1)

        // (a) Positiv: 10 MWh ved spot 1000, 0 MWh ved spot 500.
        var positiv = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0,             MwhElhub: 10, SpotprisNokMwh: 1000, SpotomsetningNok: 10000),
            new(T0.AddHours(1), MwhElhub: 0,  SpotprisNokMwh: 500,  SpotomsetningNok: 0),
        };
        var rPos = CaptureRateCalculator.Compute(positiv, Array.Empty<CaptureRateCalculator.DailyInput>());
        rPos.TimesCr.Should().BeGreaterThan(1.0);
        rPos.TimingMerverdiNok.Should().BeGreaterThan(0);
        Math.Sign(rPos.TimesCr - 1.0).Should().Be(Math.Sign(rPos.TimingMerverdiNok));

        // (b) Negativ: 0 MWh ved spot 1000, 10 MWh ved spot 500.
        var negativ = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0,             MwhElhub: 0,  SpotprisNokMwh: 1000, SpotomsetningNok: 0),
            new(T0.AddHours(1), MwhElhub: 10, SpotprisNokMwh: 500,  SpotomsetningNok: 5000),
        };
        var rNeg = CaptureRateCalculator.Compute(negativ, Array.Empty<CaptureRateCalculator.DailyInput>());
        rNeg.TimesCr.Should().BeLessThan(1.0);
        rNeg.TimingMerverdiNok.Should().BeLessThan(0);
        Math.Sign(rNeg.TimesCr - 1.0).Should().Be(Math.Sign(rNeg.TimingMerverdiNok));

        // (c) Jevn: 5 MWh begge timer, spot 1000 og 500.
        var jevn = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0,             MwhElhub: 5, SpotprisNokMwh: 1000, SpotomsetningNok: 5000),
            new(T0.AddHours(1), MwhElhub: 5, SpotprisNokMwh: 500,  SpotomsetningNok: 2500),
        };
        var rJevn = CaptureRateCalculator.Compute(jevn, Array.Empty<CaptureRateCalculator.DailyInput>());
        rJevn.TimesCr.Should().BeApproximately(1.0, 1e-9);
        rJevn.TimingMerverdiNok.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void CapturePris_BrukerVolumvektetSpot_IkkeFaktiskOmsetning()
    {
        // Spec NESTE-CHAT-CR-MERVERDI-OPPRYDDING: capture-pris er nå Σ(MWh × spot) / Σ(MWh).
        // I høyvanns-måneder der faktisk omsetning er lavere enn spotverdien
        // (vi leverer mer enn budet), skal CR fortsatt være positiv hvis timingen er det.
        //
        // 2 timer, jevn 5 MWh, spot 1000 / 500. Snitt-spot = 750.
        // Omsetning er KUNSTIG lav (3000 NOK total) — viser at CR ikke påvirkes av det.
        var hours = new List<CaptureRateCalculator.HourlyInput>
        {
            new(T0,             MwhElhub: 5, SpotprisNokMwh: 1000, SpotomsetningNok: 2000),
            new(T0.AddHours(1), MwhElhub: 5, SpotprisNokMwh: 500,  SpotomsetningNok: 1000),
        };
        var r = CaptureRateCalculator.Compute(hours, Array.Empty<CaptureRateCalculator.DailyInput>());

        // CapturePris = (5×1000 + 5×500) / 10 = 7500/10 = 750 (= snitt-spot, jevn produksjon)
        r.CapturePriceNokMwh.Should().BeApproximately(750, 1e-9);
        r.TimesCr.Should().BeApproximately(1.0, 1e-9);

        // Realisert pris = 3000/10 = 300 (langt under spot → utførelses-gap)
        r.RealisertPrisNokMwh.Should().BeApproximately(300, 1e-9);
        // Realisert vs spot = 3000 - (5×1000 + 5×500) = -4500
        r.RealisertVsSpotNok.Should().BeApproximately(-4500, 1e-9);
    }
}
