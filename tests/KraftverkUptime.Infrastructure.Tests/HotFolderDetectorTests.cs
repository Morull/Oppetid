using ClosedXML.Excel;
using FluentAssertions;
using KraftverkUptime.Infrastructure.HotFolder;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester at <see cref="HotFolderDetector"/> håndterer KAIA-eksportens
/// strippet-norske-tegn-faner ("1 Vikes", "1 Stlskraft") via R1-celle-fallback.
///
/// Bakgrunn: 2026-05-03 satt 6 single-plant settlement-filer i karantene fordi
/// detektoren bare matchet på sheet-navn, og KAIA stripper norske tegn fra
/// fane-navn (Vikeså → "1 Vikes"). Cell A1 har derimot fullt navn ("Vikeså 01.04.2026 - 30.04.2026")
/// som vi nå bruker som primær-kilde.
/// </summary>
public sealed class HotFolderDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HotFolderDetector _detector;
    private readonly List<string> _tempFiles = new();

    public HotFolderDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hotfolder-detector-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _detector = new HotFolderDetector(new HotFolderOptions { RootPath = _tempDir });
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("1 Vikes", "Vikeså 01.04.2026 - 30.04.2026", "vikesa")]
    [InlineData("1 Stlskraft", "Stølskraft 01.04.2026 - 30.04.2026", "stolskraft")]
    [InlineData("1 Lgjen", "Løgjen 01.02.2026 - 28.02.2026", "logjen")]
    [InlineData("3 Grdemfoss", "Grødemfoss 01.03.2026 - 31.03.2026", "grodemfoss")]
    [InlineData("7 greyfoss", "Øgreyfoss 01.04.2026 - 30.04.2026", "ogreyfoss")]
    [InlineData("8 rsdalen", "Ørsdalen 01.04.2026 - 30.04.2026", "orsdalen")]
    public void Detect_KaiaStrippedSheetWithCanonicalA1_ResolvesPlant(
        string sheetName, string a1Content, string expectedPlantId)
    {
        // Arrange — bygg xlsx med "Summering" + 1 plant-fane (single-plant flyt)
        var path = CreateWorkbook(
            "dataeksport_test.xlsx",
            ("Summering", null),
            (sheetName, a1Content));

        // Act
        var result = _detector.Detect(new FileInfo(path));

        // Assert
        result.Success.Should().BeTrue("R1 har kanonisk navn med æøå");
        result.PlantId.Should().Be(expectedPlantId);
        result.SourceType.Should().Be(SourceType.Settlement);
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.ResolvedPlantId.Should().Be(expectedPlantId);
        result.Diagnostics.Attempts.Should().NotBeEmpty();
    }

    [Fact]
    public void Detect_MultiPlantWorkbook_ReturnsMultiRouting()
    {
        // KAIA multi-plant: Summering + 9 plant-faner (samme som de tre filene
        // i done/ som lyktes 2026-05-03)
        var path = CreateWorkbook(
            "dataeksport_multi.xlsx",
            ("Summering", null),
            ("1 Lgjen", "Løgjen 01.04.2026 - 30.04.2026"),
            ("2 Drivdal", "Drivdal 01.04.2026 - 30.04.2026"),
            ("3 Grdemfoss", "Grødemfoss 01.04.2026 - 30.04.2026"));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.PlantId.Should().Be("_multi_");
        result.SourceType.Should().Be(SourceType.SettlementMultiPlant);
    }

    [Fact]
    public void Detect_SinglePlantWithCleanSheetName_StillWorks()
    {
        // Manuelle eksporter med ren fane-navn skal fortsatt fungere
        var path = CreateWorkbook(
            "drivdal_settlement.xlsx",
            ("Summering", null),
            ("Drivdal", "Drivdal 01.04.2026 - 30.04.2026"));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.PlantId.Should().Be("drivdal");
    }

    [Fact]
    public void Detect_FilenameContainsPlantId_PrefersFilename()
    {
        // Filename-regex skal slå før workbook-content-sniff
        var path = CreateWorkbook(
            "drivdal_export.xlsx",
            ("Summering", null),
            ("1 Vikes", "Vikeså 01.04.2026")); // misvisende fane

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.PlantId.Should().Be("drivdal", "filnavn-regex skal vinne over workbook-content");
    }

    [Fact]
    public void Detect_UnknownPlantInA1_ReturnsUnknown_WithDiagnostics()
    {
        // Anlegg som ikke finnes i KnownPlantSlugs → ikke autoriserer
        var path = CreateWorkbook(
            "dataeksport_test.xlsx",
            ("Summering", null),
            ("1 Annet", "AnnetVerk 01.04.2026 - 30.04.2026"));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Klarte ikke identifisere");
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.SheetTitleCells.Should().ContainKey("1 Annet");
        result.Diagnostics.Attempts.Should()
            .Contain(s => s.Contains("matcher ikke", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_StrippedSheetWithoutCanonicalA1_FailsGracefully()
    {
        // Verste tilfelle: "1 Vikes" som fane-navn, og A1 er tom (eldre format)
        var path = CreateWorkbook(
            "dataeksport_legacy.xlsx",
            ("Summering", null),
            ("1 Vikes", null)); // ingen A1-tittel

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.SheetNames.Should().Contain("1 Vikes");
    }

    [Fact]
    public void Detect_DiagnosticsIncludeSheetNamesAndTrace_ForXlsx()
    {
        var path = CreateWorkbook(
            "dataeksport_test.xlsx",
            ("Summering", null),
            ("1 Vikes", "Vikeså 01.04.2026 - 30.04.2026"));

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.SheetNames.Should().BeEquivalentTo(new[] { "Summering", "1 Vikes" });
        result.Diagnostics.SheetTitleCells.Should().ContainKey("1 Vikes");
        result.Diagnostics.SheetTitleCells["1 Vikes"].Should().StartWith("Vikeså");
    }

    [Fact]
    public void Detect_OperlogCsv_PopulatesStationDiagnostics()
    {
        var path = Path.Combine(_tempDir, "operlog-test.csv");
        File.WriteAllLines(path,
        [
            "timestamp;station;username;tag;text;value",
            "2026-04-29T10:00:00;Drivdal;system;TAG1;event;1",
            "2026-04-29T10:01:00;Drivdal;system;TAG2;event;1",
            "2026-04-29T10:02:00;Haukland;system;TAG3;event;1",
        ]);
        _tempFiles.Add(path);

        var result = _detector.Detect(new FileInfo(path));

        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.OperlogStations.Should().ContainKey("Drivdal");
        result.Diagnostics.OperlogStations!["Drivdal"].Should().Be(2);
    }

    [Fact]
    public void Detect_ScadaCsvWithKnownPrefix_PopulatesPrefixCounts()
    {
        var path = Path.Combine(_tempDir, "export-tags-MASTER.csv");
        File.WriteAllText(path,
            "DateTime;Cluster1.HONNE_GEN1_MW;Cluster1.HONNE_GEN1_FLOW;Cluster1.HONNE_DAM_LEVEL\n" +
            "2026-04-29T10:00:00;1.5;0.8;100.2\n");
        _tempFiles.Add(path);

        var result = _detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue();
        result.PlantId.Should().Be("honnefoss");
        result.SourceType.Should().Be(SourceType.ScadaTrends);
        result.Diagnostics!.ScadaPrefixCounts.Should().ContainKey("HONNE");
        result.Diagnostics.ScadaPrefixCounts!["HONNE"].Should().Be(3);
    }

    /// <summary>
    /// Lager en ekte xlsx-fil med gitte (sheetName, a1Content)-par. A1-content
    /// settes til null hvis det skal være tom celle. Filen registreres for
    /// opprydding etter testen.
    /// </summary>
    private string CreateWorkbook(string fileName, params (string Name, string? A1)[] sheets)
    {
        var path = Path.Combine(_tempDir, fileName);
        using (var wb = new XLWorkbook())
        {
            foreach (var (name, a1) in sheets)
            {
                var ws = wb.AddWorksheet(name);
                if (!string.IsNullOrEmpty(a1))
                {
                    ws.Cell(1, 1).Value = a1;
                }
            }
            wb.SaveAs(path);
        }
        _tempFiles.Add(path);
        return path;
    }
}
