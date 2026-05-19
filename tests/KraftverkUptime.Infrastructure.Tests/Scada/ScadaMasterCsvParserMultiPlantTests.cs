using FluentAssertions;
using KraftverkUptime.Modules.Scada.Import;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Scada;

/// <summary>
/// Tester for <see cref="ScadaMasterCsvParser.ParseMultiPlant"/> — verifiserer
/// at multi-anleggs master-CSV-er splittes korrekt per signal-prefiks slik at
/// hver plant får sin egen sample-batch (og dermed sin egen data_imports-rad).
/// </summary>
public sealed class ScadaMasterCsvParserMultiPlantTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;

    /// <summary>Lookup som matcher prefiks-mappingen i prod (HotFolderOptions/ScadaImportService).</summary>
    private static string? MapSignal(string signalId)
    {
        // Hent prefiks før første underscore
        var underscoreIdx = signalId.IndexOf('_');
        var prefix = underscoreIdx > 0 ? signalId[..underscoreIdx] : signalId;
        return prefix switch
        {
            "VIKESA" => "vikesa",
            "STOLSKRAFT" => "stolskraft",
            "ORSDAL" => "orsdalen",
            "OGREY1" or "OGREY2" => "ogreyfoss",
            "LOGJEN" => "logjen",
            _ => null,
        };
    }

    [Fact]
    public void ParseMultiPlant_GrupperetCsv_SplittesPerPlant()
    {
        var csv = """
            DateTime;Value (Cluster1.VIKESA_G1_GEN_P_PV);Unit (Cluster1.VIKESA_G1_GEN_P_PV);Value (Cluster1.OGREY1_G1_GEN_P_PV);Unit (Cluster1.OGREY1_G1_GEN_P_PV);Value (Cluster1.LOGJEN_G1_GEN_P_PV);Unit (Cluster1.LOGJEN_G1_GEN_P_PV)
            2026-04-01 00:00:00.000;100.0;kW;200.0;kW;300.0;kW
            2026-04-01 01:00:00.000;101.0;kW;201.0;kW;301.0;kW
            """;

        var parser = new ScadaMasterCsvParser();
        var result = parser.ParseMultiPlant(new StringReader(csv), Tz, MapSignal);

        result.RowsParsed.Should().Be(2);
        result.RowsSkipped.Should().Be(0);
        result.PerPlant.Should().HaveCount(3);
        result.UnmappedSignals.Should().BeEmpty();

        var byPlant = result.PerPlant.ToDictionary(p => p.PlantId);
        byPlant["vikesa"].SignalCount.Should().Be(1);
        byPlant["vikesa"].Samples.Should().HaveCount(2);
        byPlant["vikesa"].Samples.Should().AllSatisfy(s =>
        {
            s.AssetId.Should().Be("vikesa");
            s.SignalId.Should().Be("VIKESA_G1_GEN_P_PV");
        });

        byPlant["ogreyfoss"].SignalCount.Should().Be(1);
        byPlant["ogreyfoss"].Samples.Should().HaveCount(2);
        byPlant["ogreyfoss"].Samples.Should().AllSatisfy(s => s.AssetId.Should().Be("ogreyfoss"));

        byPlant["logjen"].SignalCount.Should().Be(1);
        byPlant["logjen"].Samples.Should().HaveCount(2);
    }

    [Fact]
    public void ParseMultiPlant_OGREY1_OG_OGREY2_GirSammeOgreyfossPlant()
    {
        // OGREY1 og OGREY2 mapper begge til "ogreyfoss" — verifiser at de
        // grupperes som ett anlegg, ikke to.
        var csv = """
            DateTime;Value (Cluster1.OGREY1_G1_GEN_P_PV);Unit (Cluster1.OGREY1_G1_GEN_P_PV);Value (Cluster1.OGREY2_G2_GEN_P_PV);Unit (Cluster1.OGREY2_G2_GEN_P_PV)
            2026-04-01 00:00:00.000;500.0;kW;600.0;kW
            """;

        var parser = new ScadaMasterCsvParser();
        var result = parser.ParseMultiPlant(new StringReader(csv), Tz, MapSignal);

        result.PerPlant.Should().HaveCount(1);
        result.PerPlant[0].PlantId.Should().Be("ogreyfoss");
        result.PerPlant[0].SignalCount.Should().Be(2, "begge G1 og G2 hører til Øgreyfoss");
        result.PerPlant[0].Samples.Should().HaveCount(2);
    }

    [Fact]
    public void ParseMultiPlant_UkjentPrefix_SkippesOgRapporteres()
    {
        var csv = """
            DateTime;Value (Cluster1.VIKESA_G1_GEN_P_PV);Unit (Cluster1.VIKESA_G1_GEN_P_PV);Value (Cluster1.UKJENT_NOE_PV);Unit (Cluster1.UKJENT_NOE_PV)
            2026-04-01 00:00:00.000;100.0;kW;999.0;V
            """;

        var parser = new ScadaMasterCsvParser();
        var result = parser.ParseMultiPlant(new StringReader(csv), Tz, MapSignal);

        result.PerPlant.Should().HaveCount(1);
        result.PerPlant[0].PlantId.Should().Be("vikesa");
        result.UnmappedSignals.Should().ContainSingle().And.Contain("UKJENT_NOE_PV");
    }

    [Fact]
    public void ParseMultiPlant_AlleSignalerUkjente_KasterInvalidData()
    {
        var csv = """
            DateTime;Value (Cluster1.UKJENT_A_PV);Unit (Cluster1.UKJENT_A_PV)
            2026-04-01 00:00:00.000;100.0;V
            """;

        var parser = new ScadaMasterCsvParser();
        var act = () => parser.ParseMultiPlant(new StringReader(csv), Tz, MapSignal);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Ingen signaler i CSV-en mapper til kjente plant-prefikser*");
    }

    [Fact]
    public void ParseMultiPlant_TomCsv_KasterInvalidData()
    {
        var parser = new ScadaMasterCsvParser();
        var act = () => parser.ParseMultiPlant(new StringReader(""), Tz, MapSignal);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Tom CSV*");
    }

    [Fact]
    public void ParseMultiPlant_5_anlegg_72_tags_grupperesKorrekt()
    {
        // Mini-simulering av faktisk 73-tags eksport: 5 anlegg, blandet rekkefølge
        var sb = new System.Text.StringBuilder("DateTime");
        var tags = new[]
        {
            "VIKESA_G1_GEN_P_PV", "STOLSKRAFT_G1_GEN_P_PV", "ORSDAL_G1_GEN_P_PV",
            "OGREY1_G1_GEN_P_PV", "OGREY2_G2_GEN_P_PV", "LOGJEN_G1_GEN_P_PV",
            "VIKESA_INNTAK_NIVA_OVERLOP_VF_PV", "LOGJEN_INNTAK_NIVA_OVERLOP_VF_PV",
            "OGREY1_INNTAK_NIVA_OVERLOP_VF_PV",
        };
        foreach (var t in tags)
        {
            sb.Append(";Value (Cluster1.").Append(t).Append(");Unit (Cluster1.").Append(t).Append(')');
        }
        sb.AppendLine();
        sb.Append("2026-04-01 00:00:00.000");
        foreach (var _ in tags)
        {
            sb.Append(";1.0;m3/s");
        }

        var parser = new ScadaMasterCsvParser();
        var result = parser.ParseMultiPlant(new StringReader(sb.ToString()), Tz, MapSignal);

        result.PerPlant.Select(p => p.PlantId).Should().BeEquivalentTo(new[]
        {
            "vikesa", "stolskraft", "orsdalen", "ogreyfoss", "logjen"
        });

        var byPlant = result.PerPlant.ToDictionary(p => p.PlantId);
        byPlant["vikesa"].SignalCount.Should().Be(2);         // GEN_P + OVERLOP
        byPlant["stolskraft"].SignalCount.Should().Be(1);     // bare GEN_P
        byPlant["orsdalen"].SignalCount.Should().Be(1);
        byPlant["ogreyfoss"].SignalCount.Should().Be(3);      // OGREY1_G1, OGREY2_G2, OGREY1_OVERLOP
        byPlant["logjen"].SignalCount.Should().Be(2);         // GEN_P + OVERLOP
    }
}
