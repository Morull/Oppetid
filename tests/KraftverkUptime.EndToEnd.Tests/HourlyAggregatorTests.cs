using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Parsing;
using KraftverkUptime.Modules.Settlement.Quality;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Unit-tester for <see cref="HourlyAggregator"/>: granularity-deteksjon
/// og aggregering av 15-min-rader til time-rader. Bruker syntetiske
/// <see cref="SettlementHourlyRow"/>-objekter direkte (ingen XLSX) for å
/// isolere aggregerings-logikken.
/// </summary>
public class HourlyAggregatorTests
{
    private static SettlementHourlyRow Row(
        DateTimeOffset utc,
        double? mwh = null,
        double? spotbud = null,
        double? spotpris = null,
        double? ubalanse = null,
        double? rkPris = null,
        double? effektMw = null,
        double? oppgjor = null,
        double? produksjonplan = null)
    {
        return new SettlementHourlyRow
        {
            TimeUtc = utc,
            TimeLocal = utc, // tester opererer i UTC for enkelhet
            MwhElhub = mwh,
            MwhESett = mwh,
            SpotbudMwh = spotbud,
            SpotprisNokMwh = spotpris,
            UbalanseMwh = ubalanse,
            RkPrisNokMwh = rkPris,
            EffektavlesningerMw = effektMw,
            OppgjorNok = oppgjor,
            ProduksjonplanMwh = produksjonplan,
            DqState = DataQualityState.Good,
        };
    }

    private static DateTimeOffset T(int hour, int minute) =>
        new(2025, 2, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void DetectGranularity_HourlyRows_ReturnsHourly()
    {
        var rows = new[]
        {
            Row(T(0, 0)),
            Row(T(1, 0)),
            Row(T(2, 0)),
        };
        HourlyAggregator.DetectGranularity(rows)
            .Should().Be(HourlyAggregator.GranularityMinutes.Hourly);
    }

    [Fact]
    public void DetectGranularity_QuarterRows_ReturnsQuarterHourly()
    {
        var rows = new[]
        {
            Row(T(0, 0)),
            Row(T(0, 15)),
            Row(T(0, 30)),
            Row(T(0, 45)),
        };
        HourlyAggregator.DetectGranularity(rows)
            .Should().Be(HourlyAggregator.GranularityMinutes.QuarterHourly);
    }

    [Fact]
    public void DetectGranularity_MixedDifferanser_ReturnsUnknown()
    {
        var rows = new[]
        {
            Row(T(0, 0)),
            Row(T(0, 15)),
            Row(T(1, 0)), // hopp på 45 min
        };
        HourlyAggregator.DetectGranularity(rows)
            .Should().Be(HourlyAggregator.GranularityMinutes.Unknown);
    }

    [Fact]
    public void DetectGranularity_KunEnRad_ReturnsHourlyAsDefault()
    {
        var rows = new[] { Row(T(0, 0)) };
        HourlyAggregator.DetectGranularity(rows)
            .Should().Be(HourlyAggregator.GranularityMinutes.Hourly);
    }

    [Fact]
    public void Process_HourlyRows_ReturnererUendret()
    {
        var rows = new[]
        {
            Row(T(0, 0), mwh: 1.0),
            Row(T(1, 0), mwh: 2.0),
            Row(T(2, 0), mwh: 3.0),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);
        result.Should().BeEquivalentTo(rows);
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Process_QuarterRows_AggregererTilEnTime()
    {
        // Fire kvartal i én time, alle med 0.25 MWh → 1.0 MWh totalt
        var rows = new[]
        {
            Row(T(10, 0), mwh: 0.25),
            Row(T(10, 15), mwh: 0.25),
            Row(T(10, 30), mwh: 0.25),
            Row(T(10, 45), mwh: 0.25),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result.Should().HaveCount(1);
        result[0].TimeUtc.Should().Be(T(10, 0));
        result[0].MwhElhub.Should().BeApproximately(1.0, 1e-9);
        result[0].MwhESett.Should().BeApproximately(1.0, 1e-9);
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Process_QuarterRows_VektetSnittFraSpotbudOgSpotpris()
    {
        // Spotpris vektes på Spotbud-volum:
        //   Q1: 1 MWh @ 100 NOK
        //   Q2: 1 MWh @ 200 NOK
        //   Q3: 0 MWh @ 300 NOK   (vekt 0 — bidrar ikke)
        //   Q4: 2 MWh @ 400 NOK
        // weighted = (1×100 + 1×200 + 0×300 + 2×400) / (1+1+0+2) = 1100/4 = 275
        var rows = new[]
        {
            Row(T(10, 0),  spotbud: 1, spotpris: 100),
            Row(T(10, 15), spotbud: 1, spotpris: 200),
            Row(T(10, 30), spotbud: 0, spotpris: 300),
            Row(T(10, 45), spotbud: 2, spotpris: 400),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result.Should().HaveCount(1);
        result[0].SpotbudMwh.Should().BeApproximately(4, 1e-9);
        result[0].SpotprisNokMwh.Should().BeApproximately(275, 1e-9);
    }

    [Fact]
    public void Process_QuarterRows_VektetSnittFraAbsUbalanseOgRkPris()
    {
        // RkPris vektes på |Ubalanse|. Q3 har negativ ubalanse — vekten blir |−2| = 2.
        //   Q1: ubalanse=+1, rk=100  → bidrag 100
        //   Q2: ubalanse=+0, rk=200  → vekt 0, ingen bidrag
        //   Q3: ubalanse=−2, rk=300  → bidrag 600
        //   Q4: ubalanse=+1, rk=400  → bidrag 400
        // Sum vekt = 1+0+2+1 = 4. Weighted = (100+600+400)/4 = 275
        var rows = new[]
        {
            Row(T(10, 0),  ubalanse: 1,  rkPris: 100),
            Row(T(10, 15), ubalanse: 0,  rkPris: 200),
            Row(T(10, 30), ubalanse: -2, rkPris: 300),
            Row(T(10, 45), ubalanse: 1,  rkPris: 400),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        // UbalanseMwh = sum (ikke abs) = +0
        result[0].UbalanseMwh.Should().BeApproximately(0, 1e-9);
        result[0].RkPrisNokMwh.Should().BeApproximately(275, 1e-9);
    }

    [Fact]
    public void Process_QuarterRows_VektingNullFallbackTilSimpleAverage()
    {
        // Alle Spotbud=0 → kan ikke vektes på volum. Faller tilbake til
        // aritmetisk snitt over kvartalene som har pris.
        var rows = new[]
        {
            Row(T(10, 0),  spotbud: 0, spotpris: 100),
            Row(T(10, 15), spotbud: 0, spotpris: 200),
            Row(T(10, 30), spotbud: 0, spotpris: 300),
            Row(T(10, 45), spotbud: 0, spotpris: 400),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result[0].SpotprisNokMwh.Should().BeApproximately(250, 1e-9);
    }

    [Fact]
    public void Process_QuarterRows_EffektavlesningerSnittesIkkeSummeres()
    {
        // EffektavlesningerMw er momentanverdi (MW) — skal aritmetisk snittes,
        // ikke summeres som energi.
        var rows = new[]
        {
            Row(T(10, 0),  effektMw: 1.0),
            Row(T(10, 15), effektMw: 2.0),
            Row(T(10, 30), effektMw: 3.0),
            Row(T(10, 45), effektMw: 4.0),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);
        result[0].EffektavlesningerMw.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Process_QuarterRows_OppgjorOgProduksjonplanSummeres()
    {
        var rows = new[]
        {
            Row(T(10, 0),  oppgjor: 100, produksjonplan: 0.20),
            Row(T(10, 15), oppgjor: 150, produksjonplan: 0.25),
            Row(T(10, 30), oppgjor: 200, produksjonplan: 0.30),
            Row(T(10, 45), oppgjor: 250, produksjonplan: 0.25),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);
        result[0].OppgjorNok.Should().BeApproximately(700, 1e-9);
        result[0].ProduksjonplanMwh.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Process_QuarterRows_AlleNullVerdier_ReturnererNull()
    {
        var rows = new[]
        {
            Row(T(10, 0)),
            Row(T(10, 15)),
            Row(T(10, 30)),
            Row(T(10, 45)),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result.Should().HaveCount(1);
        result[0].MwhElhub.Should().BeNull();
        result[0].SpotprisNokMwh.Should().BeNull();
    }

    [Fact]
    public void Process_QuarterRows_UkompletteTime_FlaggesMenAggregeresLikevel()
    {
        // 3 kvartal istedenfor 4 — første time får ufullstendig data.
        var rows = new[]
        {
            Row(T(10, 0),  mwh: 0.25),
            Row(T(10, 15), mwh: 0.25),
            Row(T(10, 30), mwh: 0.25),
            // Q4 mangler
            Row(T(11, 0),  mwh: 0.30),
            Row(T(11, 15), mwh: 0.30),
            Row(T(11, 30), mwh: 0.30),
            Row(T(11, 45), mwh: 0.30),
        };
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result.Should().HaveCount(2);
        result[0].MwhElhub.Should().BeApproximately(0.75, 1e-9); // bare 3 kvartal
        result[1].MwhElhub.Should().BeApproximately(1.20, 1e-9); // alle 4 kvartal

        issues.Should().ContainSingle(i => i.Code == "QUARTERLY_INCOMPLETE_HOUR");
    }

    [Fact]
    public void Process_QuarterRows_DSTAutumn_ToHourBucketsForSammeLokalKlokkeslett()
    {
        // 26. okt 2025 02:00 lokal eksisterer to ganger (DST-slutt).
        // I UTC blir det 00:00 og 01:00 — to forskjellige hour-bucketer.
        // 4 kvartal i hver bucket → 2 aggregerte timer.
        var rows = new List<SettlementHourlyRow>();
        for (var min = 0; min < 60; min += 15)
        {
            // Første runde: 00:00 UTC = 02:00 sommertid CEST (+02:00)
            rows.Add(Row(new DateTimeOffset(2025, 10, 26, 0, min, 0, TimeSpan.Zero), mwh: 0.25));
        }
        for (var min = 0; min < 60; min += 15)
        {
            // Andre runde: 01:00 UTC = 02:00 standard CET (+01:00)
            rows.Add(Row(new DateTimeOffset(2025, 10, 26, 1, min, 0, TimeSpan.Zero), mwh: 0.25));
        }
        var issues = new List<ValidationIssue>();
        var result = HourlyAggregator.Process(rows, issues);

        result.Should().HaveCount(2);
        result[0].MwhElhub.Should().BeApproximately(1.0, 1e-9);
        result[1].MwhElhub.Should().BeApproximately(1.0, 1e-9);
    }
}
