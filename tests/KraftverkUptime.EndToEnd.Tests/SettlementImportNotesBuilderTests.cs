using FluentAssertions;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Quality;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Tester for <see cref="SettlementImportNotesBuilder"/>. Verifiserer at notes-
/// teksten som havner i <c>data_imports.notes</c> er menneske-lesbar og
/// inneholder all info drifts-leder trenger for å forstå avvik.
/// </summary>
public sealed class SettlementImportNotesBuilderTests
{
    private static ParsedSettlement Make(
        int hourlyCount,
        int planRows = 0,
        params ValidationIssue[] issues)
    {
        var hourly = new List<SettlementHourlyRow>();
        var start = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < hourlyCount; i++)
        {
            var t = start.AddHours(i);
            hourly.Add(new SettlementHourlyRow
            {
                TimeUtc = t,
                TimeLocal = t,
                ProduksjonplanMwh = i < planRows ? 1.5 : null,
            });
        }
        return new ParsedSettlement
        {
            PlantName = "Drivdal",
            PlantId = "drivdal",
            SchemaVersion = "portal-v1",
            PeriodStartUtc = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEndUtc = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            Hourly = hourly,
            Issues = issues,
        };
    }

    [Fact]
    public void Build_IngenIssuesIngenPlan_ReturnererNull()
    {
        var notes = SettlementImportNotesBuilder.Build(Make(720));
        notes.Should().BeNull("ingenting meningsfullt å rapportere");
    }

    [Fact]
    public void Build_KunHydrogridPlan_ViserAntall()
    {
        var notes = SettlementImportNotesBuilder.Build(Make(720, planRows: 720));
        notes.Should().Contain("Hydrogrid-plan: 720/720 timer");
    }

    [Fact]
    public void Build_MedIssues_ListerKodeOgMelding()
    {
        var notes = SettlementImportNotesBuilder.Build(Make(720, planRows: 0,
            new ValidationIssue(IssueSeverity.Warning, "DST_NONEXISTENT",
                "25.03.2026 02:30 eksisterer ikke pga DST", AffectedRows: 1),
            new ValidationIssue(IssueSeverity.Warning, "ELHUB_ESETT_MISMATCH",
                "10 timer har MWh-Elhub ≠ MWh-eSett", AffectedRows: 10)));

        notes.Should().Contain("2 avvik");
        notes.Should().Contain("DST_NONEXISTENT");
        notes.Should().Contain("ELHUB_ESETT_MISMATCH");
        notes.Should().Contain("[WARN]");
    }

    [Fact]
    public void Build_DupliksIssues_KollapsesMedTeller()
    {
        var dup = new ValidationIssue(IssueSeverity.Warning, "DST_NONEXISTENT",
            "Rad eksisterer ikke pga DST", AffectedRows: 1);

        var notes = SettlementImportNotesBuilder.Build(Make(720, 0, dup, dup, dup));

        notes.Should().Contain("3 avvik");
        notes.Should().Contain("DST_NONEXISTENT (3×)",
            "duplikate koder kollapses til én linje med teller");
    }

    [Fact]
    public void Build_BlandetSeverity_SortererErrorFørst()
    {
        var notes = SettlementImportNotesBuilder.Build(Make(720, 0,
            new ValidationIssue(IssueSeverity.Info, "INFO_CODE", "info melding"),
            new ValidationIssue(IssueSeverity.Error, "ERR_CODE", "error melding"),
            new ValidationIssue(IssueSeverity.Warning, "WARN_CODE", "warn melding")));

        notes.Should().NotBeNull();
        var errIdx = notes!.IndexOf("[ERR]", StringComparison.Ordinal);
        var warnIdx = notes.IndexOf("[WARN]", StringComparison.Ordinal);
        var infoIdx = notes.IndexOf("[INFO]", StringComparison.Ordinal);
        errIdx.Should().BeLessThan(warnIdx, "error skal komme før warning");
        warnIdx.Should().BeLessThan(infoIdx, "warning skal komme før info");
    }

    [Fact]
    public void Build_VeldigLangeMeldinger_TrunkeresTil2000Tegn()
    {
        var manyIssues = Enumerable.Range(0, 50)
            .Select(i => new ValidationIssue(IssueSeverity.Warning,
                $"CODE_{i}", new string('x', 200)))
            .ToArray();

        var notes = SettlementImportNotesBuilder.Build(Make(720, 0, manyIssues));

        notes.Should().NotBeNull();
        notes!.Length.Should().BeLessThanOrEqualTo(SettlementImportNotesBuilder.MaxNotesLength);
        notes.Should().EndWith("…", "truncation-markering");
    }
}
