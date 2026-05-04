using FluentAssertions;
using KraftverkUptime.Infrastructure.HotFolder;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Integrasjons-tester som kjører detektoren mot de faktiske filene som
/// havnet i karantene 2026-05-03. Hopper over hvis filene ikke finnes
/// (f.eks. CI-miljø der host-mappa ikke er mountet).
///
/// Hensikt: forsikre at fixet faktisk plukker opp Vikeså/Stølskraft-filer
/// som tidligere feilet — ikke bare syntetiske xlsx-er fra unit-test.
/// </summary>
public sealed class HotFolderDetectorRealFilesTests
{
    private const string QuarantineDir = @"C:\Morten\00 Oppetid\CSV Eksporter\quarantine\2026-05-03";

    public static TheoryData<string, string> KnownQuarantineFiles =>
        new()
        {
            { "dataeksport_20260429131218.xlsx", "vikesa" },
            { "dataeksport_20260429131222.xlsx", "stolskraft" },
            { "dataeksport_20260429131227.xlsx", "vikesa" },
            { "dataeksport_20260429131231.xlsx", "vikesa" },
            { "dataeksport_20260429131235.xlsx", "stolskraft" },
            { "dataeksport_20260429131236.xlsx", "stolskraft" },
        };

    [Theory]
    [MemberData(nameof(KnownQuarantineFiles))]
    public void Detect_RealQuarantineFile_ResolvesCorrectPlant(string fileName, string expectedPlantId)
    {
        var path = Path.Combine(QuarantineDir, fileName);
        if (!File.Exists(path))
        {
            // CI-miljø — hopp over
            return;
        }

        var detector = new HotFolderDetector(new HotFolderOptions());
        var result = detector.Detect(new FileInfo(path));

        result.Success.Should().BeTrue(
            $"karantene-fil '{fileName}' skal nå auto-detektere til {expectedPlantId} via R1");
        result.PlantId.Should().Be(expectedPlantId);
        result.SourceType.Should().Be(SourceType.Settlement);
    }
}
