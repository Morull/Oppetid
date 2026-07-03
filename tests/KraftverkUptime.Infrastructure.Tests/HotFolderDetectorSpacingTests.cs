using FluentAssertions;
using KraftverkUptime.Infrastructure.HotFolder;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// SPEC-IMPORT-KONSOLIDERT-15MIN Endring A: oppløsning (15-min vs hourly)
/// detekteres på INNHOLD (median tidsavstand mellom rad-tidsstempler), ikke
/// filnavn-markører. Markørene beholdes kun som fallback for filer med
/// færre enn 3 tidsstempler.
/// </summary>
public sealed class HotFolderDetectorSpacingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HotFolderDetector _detector;

    public HotFolderDetectorSpacingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hotfolder-spacing-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _detector = new HotFolderDetector(new HotFolderOptions { RootPath = _tempDir });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteCsv(string fileName, string header, IEnumerable<string> rows)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllLines(path, new[] { header }.Concat(rows));
        return path;
    }

    private static IEnumerable<string> TrendRows(DateTime start, TimeSpan step, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var t = start + step * i;
            yield return $"{t:yyyy-MM-dd HH:mm:ss.fff};1.5;kW";
        }
    }

    private const string Header = "DateTime;Value (Cluster1.DRIVDAL_G1_GEN_P_PV);Unit (Cluster1.DRIVDAL_G1_GEN_P_PV)";

    [Fact]
    public void FemtenMinFil_UtenMarkorINavn_DetekteresSomFine()
    {
        // Akseptansekriterium §8.1: 15-min master-CSV UTEN filnavn-markør
        // skal likevel rutes til fine-banen (spacing-måling).
        var path = WriteCsv("drivdal_scada_eksport.csv", Header,
            TrendRows(new DateTime(2026, 6, 1, 0, 0, 0), TimeSpan.FromMinutes(15), 40));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.SourceType.Should().Be(SourceType.ScadaTrendsFine);
        result.PlantId.Should().Be("drivdal");
        result.Diagnostics!.Attempts.Should()
            .Contain(a => a.Contains("Innholdsmåling", StringComparison.Ordinal)
                && a.Contains("ScadaTrendsFine", StringComparison.Ordinal));
    }

    [Fact]
    public void HourlyFil_DetekteresFortsattSomHourly()
    {
        // Akseptansekriterium §8.2: gammel hourly-eksport importeres som før.
        var path = WriteCsv("drivdal_scada_gammel.csv", Header,
            TrendRows(new DateTime(2026, 2, 1, 0, 0, 0), TimeSpan.FromHours(1), 40));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.SourceType.Should().Be(SourceType.ScadaTrends);
        result.Diagnostics!.Attempts.Should()
            .Contain(a => a.Contains("Innholdsmåling", StringComparison.Ordinal)
                && a.Contains("legacy hourly", StringComparison.Ordinal));
    }

    [Fact]
    public void FemtenMinFil_MedMarkorMenHourlyInnhold_InnholdVinner()
    {
        // Innhold har forrang: «15min» i navnet men time-avstand i innholdet.
        var path = WriteCsv("drivdal_15min_feilmerket.csv", Header,
            TrendRows(new DateTime(2026, 2, 1, 0, 0, 0), TimeSpan.FromHours(1), 40));

        var result = _detector.Detect(new FileInfo(path));

        result.SourceType.Should().Be(SourceType.ScadaTrends);
    }

    [Fact]
    public void ToRadersFil_FallerTilbakeTilFilnavnMarkor()
    {
        // < 3 tidsstempler → kan ikke måle → markør avgjør.
        var pathFine = WriteCsv("drivdal_15min_kort.csv", Header,
            TrendRows(new DateTime(2026, 6, 1), TimeSpan.FromMinutes(15), 2));
        var pathHourly = WriteCsv("drivdal_kort.csv", Header,
            TrendRows(new DateTime(2026, 6, 1), TimeSpan.FromMinutes(15), 2));

        _detector.Detect(new FileInfo(pathFine)).SourceType
            .Should().Be(SourceType.ScadaTrendsFine, "filnavn markerer 15-min");
        _detector.Detect(new FileInfo(pathHourly)).SourceType
            .Should().Be(SourceType.ScadaTrends, "ingen markør og for få rader");
    }

    [Fact]
    public void MultiPlantFemtenMin_RutesTilFineMultiPlant()
    {
        const string multiHeader =
            "DateTime;Value (Cluster1.DRIVDAL_G1_GEN_P_PV);Unit (Cluster1.DRIVDAL_G1_GEN_P_PV);"
            + "Value (Cluster1.DRIVDAL_G1_TURB_VF_PV);Unit (Cluster1.DRIVDAL_G1_TURB_VF_PV);"
            + "Value (Cluster1.HAUKLAND_G1_GEN_P_PV);Unit (Cluster1.HAUKLAND_G1_GEN_P_PV);"
            + "Value (Cluster1.HAUKLAND_G1_TURB_VF_PV);Unit (Cluster1.HAUKLAND_G1_TURB_VF_PV)";
        var rows = Enumerable.Range(0, 40).Select(i =>
        {
            var t = new DateTime(2026, 6, 1).AddMinutes(15 * i);
            return $"{t:yyyy-MM-dd HH:mm:ss.fff};1.5;kW;0.8;m3/s;2.5;kW;1.1;m3/s";
        });
        var path = WriteCsv("samlet_eksport.csv", multiHeader, rows);

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.SourceType.Should().Be(SourceType.ScadaTrendsFineMultiPlant);
        result.PlantId.Should().Be("_multi_");
    }

    [Fact]
    public void IsoTidsstempler_MedZone_MaalesOgsaa()
    {
        // Nyere eksportformat: ISO-8601 med Z — spacing-målingen skal
        // håndtere begge tidsformatene parseren støtter.
        var rows = Enumerable.Range(0, 20).Select(i =>
        {
            var t = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(15 * i);
            return $"{t:yyyy-MM-ddTHH:mm:ss.fff}Z;1.5;kW";
        });
        var path = WriteCsv("drivdal_iso.csv", Header, rows);

        _detector.Detect(new FileInfo(path)).SourceType
            .Should().Be(SourceType.ScadaTrendsFine);
    }

    [Fact]
    public void SourceTypeKey_FineMapperTilScada()
    {
        // Endring C: én SCADA-kilde i completeness.
        SourceType.ScadaTrendsFine.ToSourceTypeKey().Should().Be("scada");
        SourceType.ScadaTrendsFineMultiPlant.ToSourceTypeKey().Should().Be("scada");
        SourceType.ScadaTrends.ToSourceTypeKey().Should().Be("scada");
    }
}
