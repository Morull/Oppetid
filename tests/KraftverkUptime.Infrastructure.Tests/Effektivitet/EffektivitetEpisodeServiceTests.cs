using FluentAssertions;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Effektivitet;

/// <summary>
/// Unit-tester for <see cref="EffektivitetEpisodeService"/> — episode-deteksjon,
/// tap-beregning og effekt-bånd-aggregering. Stub-data, ingen DB.
///
/// Konvensjoner i testene:
///   – Hvert intervall er 15 min (= 0,25 t).
///   – Effekt-bin = 200 kW (default fra <see cref="EffektivitetQueryService.PowerBinKw"/>).
///   – Baseline-bin'er bygges fra Bins i responsen. Tester konstruerer Bins
///     direkte med høyt nok Antall (≥ 3) for å passere MinSamplesPerBaselineBin-
///     filteret.
/// </summary>
public class EffektivitetEpisodeServiceTests
{
    private static DateTimeOffset Q(int quarter) =>
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(quarter * 15);

    private static EffektivitetPunkt Genuine(int quarter, double kw, double eta) =>
        new(Q(quarter), kw, eta, 0.0, PunktKlassifisering.Genuine);

    private static EffektivitetResponse ResponseMed(
        IEnumerable<EffektivitetPunkt> punkter,
        IEnumerable<EffektivitetBin> bins,
        double sweetSpotEtaPct = 0)
    {
        var list = punkter.ToList();
        return new EffektivitetResponse(
            PlantId: "test",
            FromUtc: Q(0),
            ToUtc: Q(100),
            ProduksjonsTimer: list.Count(p => p.Klassifisering == PunktKlassifisering.Genuine),
            SnittEtaPct: 0,
            SweetSpotEffektKw: 0,
            SweetSpotEtaPct: sweetSpotEtaPct,
            SnittSpesifiktVannforbrukM3PerKwh: 0,
            TotalProduksjonKwh: 0,
            DataMissing: false,
            Punkter: list,
            Bins: bins.ToList());
    }

    [Fact]
    public void Analyse_IngenPunkter_GirTomtResultat()
    {
        var svc = new EffektivitetEpisodeService();
        var resp = ResponseMed(Array.Empty<EffektivitetPunkt>(), Array.Empty<EffektivitetBin>());

        var result = svc.Analyse(resp, spotPrisNokMwhPerTime: null);

        result.Episoder.Should().BeEmpty();
        result.AntallGenuineIntervaller.Should().Be(0);
        result.TotalTaptMwh.Should().Be(0);
        result.ManglerSpotpriser.Should().BeTrue();
    }

    [Fact]
    public void Analyse_AltOverTerskel_GirIngenEpisoder()
    {
        // Bin 1600-1800, baseline 90 %. Punkter ligger 88-91 % — alle innen ±2 pp.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 88.5),  // -1,5 pp → ikke underytende
            Genuine(1, 1700, 90.0),  //   0,0 pp
            Genuine(2, 1700, 91.0),  //  +1,0 pp
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().BeEmpty();
        result.AntallUnderytendeIntervaller.Should().Be(0);
    }

    [Fact]
    public void Analyse_TreSammenhengendeUnderUnderTerskel_SlasSammenTilEnEpisode()
    {
        // Baseline 90 % i bin 1600-1800. Tre kvarter på 85 % (-5 pp) etter hverandre.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),  // -5 pp
            Genuine(1, 1700, 85.0),  // -5 pp
            Genuine(2, 1700, 85.0),  // -5 pp
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().HaveCount(1);
        var ep = result.Episoder[0];
        ep.AntallIntervaller.Should().Be(3);
        ep.VarighetTimer.Should().BeApproximately(0.75, 0.001);
        ep.SnittDeltaEtaPp.Should().BeApproximately(-5.0, 0.001);
        ep.EffektMinKw.Should().Be(1700);
        ep.EffektMaksKw.Should().Be(1700);
        // Faktisk produksjon: 3 × 1700 × 0.25 / 1000 = 1.275 MWh.
        ep.FaktiskProduksjonMwh.Should().BeApproximately(1.275, 0.001);
        // Tapt MWh: 1.275 × (90-85)/85 = 0.075 MWh.
        ep.TaptMwh.Should().BeApproximately(0.075, 0.005);
        ep.TaptNok.Should().Be(0);
        ep.TaptNokErEstimat.Should().BeTrue();
    }

    [Fact]
    public void Analyse_NormalIntervallMidtI_TolereresSomGap()
    {
        // Default TillattGapIntervaller = 1.
        // Under, OK, Under, Under → én sammenhengende episode (4 intervaller).
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),  // under
            Genuine(1, 1700, 90.0),  // ok
            Genuine(2, 1700, 85.0),  // under
            Genuine(3, 1700, 84.0),  // under
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().HaveCount(1);
        // Episoden inneholder kun de 3 underytende intervallene; det normale
        // tolererte tas IKKE med i statistikken (det er per definisjon ikke
        // underytende).
        result.Episoder[0].AntallIntervaller.Should().Be(3);
    }

    [Fact]
    public void Analyse_ToNormaleMidtI_SpaltesITo()
    {
        // Default TillattGapIntervaller = 1. To OK på rad → episoden brytes.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),
            Genuine(1, 1700, 90.0),  // ok
            Genuine(2, 1700, 90.0),  // ok — 2 på rad → bryt
            Genuine(3, 1700, 85.0),
            Genuine(4, 1700, 85.0),
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().HaveCount(2);
        result.Episoder[0].AntallIntervaller.Should().Be(1);
        result.Episoder[1].AntallIntervaller.Should().Be(2);
    }

    [Fact]
    public void Analyse_BinUtenBaseline_HopperOverPunkterIBinnen()
    {
        // Bin 1000-1200 har for få samples (1 < MinSamplesPerBaselineBin=3) → ikke baseline.
        // Punktene der droppes fra evalueringen. Bin 1600-1800 har baseline.
        var bins = new[]
        {
            new EffektivitetBin(1000, 1100, Antall: 1, SnittEtaPct: 75.0),    // for tynt
            new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0),   // ok
        };
        var punkter = new[]
        {
            Genuine(0, 1050, 50.0),  // skulle vært underytende, men ingen baseline → droppet
            Genuine(1, 1700, 85.0),  // -5 pp → underytende
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().HaveCount(1);
        result.Episoder[0].EffektMinKw.Should().Be(1700);
    }

    [Fact]
    public void Analyse_MedSpotpriser_BeregnerTaptNok()
    {
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),
            Genuine(1, 1700, 85.0),
            Genuine(2, 1700, 85.0),
            Genuine(3, 1700, 85.0),  // 4 kvarter = 1 time, alle innenfor samme hourly-bucket
        };
        // Spotpris 500 NOK/MWh for time 00:00.
        var priser = new Dictionary<DateTimeOffset, double>
        {
            [new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)] = 500.0,
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), priser);

        result.Episoder.Should().HaveCount(1);
        var ep = result.Episoder[0];
        // Faktisk MWh: 4 × 1700 × 0.25 / 1000 = 1.7 MWh.
        // Tapt MWh: 1.7 × (90-85)/85 = 0.1 MWh.
        // Tapt NOK: 0.1 × 500 = 50 NOK.
        ep.TaptNok.Should().BeApproximately(50.0, 1.0);
        ep.TaptNokErEstimat.Should().BeFalse(); // alle priser dekket
        result.ManglerSpotpriser.Should().BeFalse();
    }

    [Fact]
    public void Analyse_SpotpriserDelvis_FlaggesSomEstimat()
    {
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),  // time 00:00, har pris
            Genuine(5, 1700, 85.0),  // time 01:15 → bucket 01:00, INGEN pris
        };
        var priser = new Dictionary<DateTimeOffset, double>
        {
            [new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)] = 500.0,
            // 01:00 mangler
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), priser);

        // To separate episoder (gap > 1 normalt intervall — det er bare ingen
        // genuine i mellom, men time-stempelet hopper). Faktisk: vi har bare
        // to Genuine på rad i listen, så de er etterfølgende i tids-rekkefølge.
        // Med gap-toleranse 1 vil de slås sammen.
        result.Episoder.Should().HaveCountGreaterOrEqualTo(1);
        // Minst én episode skal ha TaptNokErEstimat = true fordi 01:00-prisen mangler.
        result.Episoder.Any(e => e.TaptNokErEstimat).Should().BeTrue();
    }

    [Fact]
    public void Analyse_KunTransition_GirIngenEpisoderOgIngenGenuine()
    {
        // Transition-punkter (η under gulv) skal ikke evalueres.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            new EffektivitetPunkt(Q(0), 1700, 30.0, 0.0, PunktKlassifisering.Transition),
            new EffektivitetPunkt(Q(1), 1700, 35.0, 0.0, PunktKlassifisering.Transition),
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().BeEmpty();
        result.AntallGenuineIntervaller.Should().Be(0);
    }

    [Fact]
    public void Analyse_TerskelKanJusteres()
    {
        // Standard terskel -2 pp; vi setter til -10 pp → ingen episode for -5 pp.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),
            Genuine(1, 1700, 85.0),
            Genuine(2, 1700, 85.0),
        };

        var result = new EffektivitetEpisodeService().Analyse(
            ResponseMed(punkter, bins),
            null,
            new EpisodeAnalyseOpsjoner(DeltaEtaTerskelPp: -10.0));

        result.Episoder.Should().BeEmpty();
    }

    // ----- Sweet-spot-referanse --------------------------------------------

    [Fact]
    public void Analyse_SweetSpotModus_BrukerSweetSpotIstedenforBaseline()
    {
        // Baseline (bin-snitt) er 90 %, sweet-spot er 93 %.
        // Punkter på 91 % → -1 pp mot sweet-spot men +1 pp mot baseline.
        // Med terskel -2 pp og sweet-spot-modus skal vi få 0 episoder (-1 pp under terskel).
        // Med terskel -0.5 pp skal vi få 1 episode.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[]
        {
            Genuine(0, 1700, 91.0),
            Genuine(1, 1700, 91.0),
            Genuine(2, 1700, 91.0),
        };

        var svc = new EffektivitetEpisodeService();
        var resp = ResponseMed(punkter, bins, sweetSpotEtaPct: 93.0);

        // Mot baseline (90 %): alle på +1 pp → ingen episoder.
        var motBaseline = svc.Analyse(resp, null);
        motBaseline.Episoder.Should().BeEmpty();

        // Mot sweet-spot (93 %): alle på -2 pp → terskelfilter (-2.0) treffer akkurat.
        var motSweetSpot = svc.Analyse(resp, null,
            new EpisodeAnalyseOpsjoner(Referanse: EpisodeReferanseTyp.SweetSpot));
        motSweetSpot.Episoder.Should().HaveCount(1);
        motSweetSpot.Episoder[0].SnittDeltaEtaPp.Should().BeApproximately(-2.0, 0.001);
    }

    [Fact]
    public void Analyse_SweetSpotModus_UtenSweetSpot_GirTomtResultat()
    {
        // Hvis responsen har SweetSpotEtaPct = 0 (ikke nok data for sweet-spot),
        // skal sweet-spot-modus returnere tomt resultat uten å kaste.
        var bins = new[] { new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0) };
        var punkter = new[] { Genuine(0, 1700, 85.0), Genuine(1, 1700, 85.0) };

        var resp = ResponseMed(punkter, bins, sweetSpotEtaPct: 0); // ingen sweet-spot
        var result = new EffektivitetEpisodeService().Analyse(resp, null,
            new EpisodeAnalyseOpsjoner(Referanse: EpisodeReferanseTyp.SweetSpot));

        result.Episoder.Should().BeEmpty();
    }

    [Fact]
    public void Analyse_AggregerPerEffektBaand_SamlerPunkter()
    {
        // To bins, hver med en kort episode.
        var bins = new[]
        {
            new EffektivitetBin(1600, 1700, Antall: 10, SnittEtaPct: 90.0),
            new EffektivitetBin(800,  900,  Antall: 10, SnittEtaPct: 80.0),
        };
        var punkter = new[]
        {
            Genuine(0, 1700, 85.0),  // -5 pp i 1600-1800
            Genuine(1, 1700, 85.0),
            Genuine(10, 850, 75.0),  // -5 pp i 800-1000 (langt unna i tid)
            Genuine(11, 850, 75.0),
        };

        var result = new EffektivitetEpisodeService().Analyse(ResponseMed(punkter, bins), null);

        result.Episoder.Should().HaveCount(2);
        result.AggregatPerEffektBaand.Should().HaveCount(2);
        // Sortert synkende på tap — 1700 kW har høyere faktisk produksjon →
        // høyere tap selv om Δη er lik. Verifiserer bare at begge båndene er der.
        result.AggregatPerEffektBaand.Select(a => a.EffektKwStart)
            .Should().BeEquivalentTo(new[] { 1600.0, 800.0 });
    }
}
