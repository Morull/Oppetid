using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Modules.Reporting.DataCompleteness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for <see cref="DataCompletenessQueryService"/> — SPEC-IMPORT-
/// COMPLETENESS steg 4. Bruker in-memory EF og fastsatt
/// <see cref="TimeProvider"/> for å få deterministiske status-grenser.
///
/// Dekker spec-akseptansekriterium 15:
///   - Komplett dekning → COMPLETE
///   - 90 % dekning → PARTIAL
///   - Manglende uten lag → PENDING
///   - Manglende med lag overskredet → OVERDUE
///   - Inaktiv kilde returnerer ingen celler
///   - Cross-period-aggregering for summary
///   - Ukentlig digest sorterer overdue desc
///   - Edge case: nytt anlegg uten import-historikk
/// </summary>
public class DataCompletenessQueryServiceTests
{
    private static KraftverkDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<KraftverkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new KraftverkDbContext(options, new TestQueryContext());
    }

    private static FakeTimeProvider FixedTime(DateTimeOffset utcNow) => new(utcNow);

    private static DataCompletenessQueryService NewService(KraftverkDbContext db, DateTimeOffset utcNow)
        => new(db, FixedTime(utcNow), NullLogger<DataCompletenessQueryService>.Instance);

    private static async Task SeedExpectationAsync(
        KraftverkDbContext db, string plantId, string source, int lagDays, bool active = true,
        DateTimeOffset? activatedAt = null)
    {
        db.DataSourceExpectations.Add(new DataSourceExpectation
        {
            PlantId = plantId,
            SourceType = source,
            Cadence = "monthly",
            ExpectedLagDays = lagDays,
            IsActive = active,
            ActivatedAtUtc = activatedAt ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedImportAsync(
        KraftverkDbContext db, string plantId, string source,
        DateTimeOffset periodStart, double coverage, DateTimeOffset importedAt)
    {
        db.DataImports.Add(new DataImport
        {
            ImportId = Guid.NewGuid(),
            PlantId = plantId,
            SourceType = source,
            PeriodFromUtc = periodStart,
            PeriodToUtc = periodStart.AddMonths(1),
            ImportedAtUtc = importedAt,
            CoveragePct = coverage,
            UserId = "test",
        });
        await db.SaveChangesAsync();
    }

    [Fact(DisplayName = "Komplett dekning gir COMPLETE-status")]
    public async Task FullCoverage_StatusComplete()
    {
        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        await SeedImportAsync(db, "drivdal", "settlement", feb2026,
            coverage: 1.0, importedAt: feb2026.AddMonths(1).AddDays(5));

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(feb2026, feb2026.AddMonths(1), default);

        var cell = matrix.Cells[new(("drivdal"), "settlement", feb2026)];
        cell.Status.Should().Be("COMPLETE");
        cell.CoveragePct.Should().Be(1.0);
    }

    [Fact(DisplayName = "Dekning under 95 % gir PARTIAL-status")]
    public async Task LowCoverage_StatusPartial()
    {
        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        await SeedImportAsync(db, "drivdal", "settlement", feb2026,
            coverage: 0.90, importedAt: feb2026.AddMonths(1).AddDays(3));

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(feb2026, feb2026.AddMonths(1), default);

        var cell = matrix.Cells[new("drivdal", "settlement", feb2026)];
        cell.Status.Should().Be("PARTIAL");
    }

    [Fact(DisplayName = "Ingen import + innenfor lag-vindu → cellen er ikke med i matrisen")]
    public async Task NoImport_WithinLagWindow_NotInMatrix()
    {
        // Drifts-leders 2026-05-03-bekreftelse: PENDING-celler er irrelevante
        // og fjernes fra matrisen. Bare COMPLETE/PARTIAL/OVERDUE telles.
        var apr2026 = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        // Now = 3 dager etter periode-slutt (apr-slutt = 1. mai). Lag = 7 → ikke OVERDUE ennå.
        var now = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(apr2026, apr2026.AddMonths(1), default);

        matrix.Cells.Keys.Should().NotContain(new DataCompletenessKey("drivdal", "settlement", apr2026),
            "PENDING-celler skal ikke være med i matrisen");
    }

    [Fact(DisplayName = "Ingen import + lag overskredet → OVERDUE")]
    public async Task NoImport_LagExceeded_StatusOverdue()
    {
        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        // 60 dager etter mar-1 (periode-slutt) — godt over 7 dagers lag.
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "honnefoss", "settlement", 7);

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(feb2026, feb2026.AddMonths(1), default);

        matrix.Cells[new("honnefoss", "settlement", feb2026)].Status.Should().Be("OVERDUE");
    }

    [Fact(DisplayName = "Inaktiv kilde gir ingen celler i matrisen")]
    public async Task InactiveSource_NotInMatrix()
    {
        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "scada", 5, active: false);

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(feb2026, feb2026.AddMonths(1), default);

        matrix.Cells.Should().BeEmpty("inaktive expectations skal ikke produsere celler");
    }

    [Fact(DisplayName = "Cross-period aggregering: summary teller alle status-typer")]
    public async Task WeeklySummary_AggregatesByStatus()
    {
        var now = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        await SeedExpectationAsync(db, "honnefoss", "settlement", 7);
        // Drivdal: COMPLETE i apr, OVERDUE i mar
        await SeedImportAsync(db, "drivdal", "settlement", apr,
            coverage: 1.0, importedAt: apr.AddMonths(1).AddDays(3));
        // Honnefoss: PARTIAL i apr, ingen mar
        await SeedImportAsync(db, "honnefoss", "settlement", apr,
            coverage: 0.80, importedAt: apr.AddMonths(1).AddDays(3));

        var svc = NewService(db, now);
        var summary = await svc.GetWeeklySummaryAsync(default);

        // GetWeeklySummaryAsync vinduet er forrige + denne måneden — apr og mai 2026.
        // 2 expectations × 2 perioder = 4 forventede celler, men PENDING droppes.
        // - drivdal+apr: COMPLETE (vi seedet med coverage 1.0)
        // - honnefoss+apr: PARTIAL (vi seedet med 0.80)
        // - drivdal+mai + honnefoss+mai: PENDING → skjult (drifts-leders 2026-05-03-bekreftelse)
        summary.TotalExpected.Should().Be(2, "kun COMPLETE + PARTIAL er igjen i vinduet");
        summary.Complete.Should().Be(1);
        summary.Partial.Should().Be(1);
        summary.Pending.Should().Be(0, "PENDING droppes fra matrisen");
        summary.Overdue.Should().Be(0);
    }

    [Fact(DisplayName = "Overdue sortert etter days_overdue desc")]
    public async Task GetOverdue_SortedByDaysOverdueDesc()
    {
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "honnefoss", "settlement", 7);
        // GetOverdueAsync vinduet er 12 mnd bakover — alle perioder fra
        // 2025-05 og fremover blir OVERDUE siden vi ikke har imports.

        var svc = NewService(db, now);
        var overdue = await svc.GetOverdueAsync(default);

        overdue.Should().HaveCountGreaterThan(1);
        // Skal være sortert med eldst først (flest dager overdue).
        for (var i = 1; i < overdue.Count; i++)
        {
            overdue[i].DaysOverdue.Should().BeLessThanOrEqualTo(
                overdue[i - 1].DaysOverdue,
                "listen skal være sortert synkende på DaysOverdue");
        }
        overdue[0].PeriodFromUtc.Should().BeBefore(overdue[^1].PeriodFromUtc);
    }

    [Fact(DisplayName = "Edge case: nytt anlegg uten historikk får OVERDUE/PENDING per nivå")]
    public async Task NewPlant_NoHistory_GetsExpectedStatus()
    {
        var now = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        // Aktivert 2026-04-01 — perioder før det skal IKKE telles.
        await SeedExpectationAsync(db, "liavatn", "settlement", 7,
            activatedAt: apr);

        var svc = NewService(db, now);
        var matrix = await svc.GetMatrixAsync(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            default);

        // Ingen celler for jan/feb/mar (før activation)
        matrix.Cells.Keys.Should().NotContain(k =>
            k.PlantId == "liavatn" && k.Period < apr);
        // Apr må være OVERDUE (now > apr + 1mnd + 7d = 2026-05-08)
        matrix.Cells[new("liavatn", "settlement", apr)].Status.Should().Be("OVERDUE");
    }

    [Fact(DisplayName = "Manuell override forcer COMPLETE selv om dekning er lav")]
    public async Task Override_ForcesComplete()
    {
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "operlog", 5);
        await SeedImportAsync(db, "drivdal", "operlog", apr,
            coverage: 0.74, importedAt: apr.AddMonths(1).AddDays(2));

        var svc = NewService(db, now);

        // Før override: PARTIAL (men siden vi nå behandler operlog spesielt blir den
        // COMPLETE — så for å trigge en realistisk PARTIAL bruker vi settlement i stedet)
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        await SeedImportAsync(db, "drivdal", "settlement", apr,
            coverage: 0.50, importedAt: apr.AddMonths(1).AddDays(2));

        var before = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        before.Cells[new("drivdal", "settlement", apr)].Status.Should().Be("PARTIAL");

        // Sett override
        await svc.SetOverrideAsync("drivdal", "settlement", apr,
            "Sjekket KAIA manuelt — DST-overgang i april forklarer 50% timer", "test-user", default);

        var after = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        var cell = after.Cells[new("drivdal", "settlement", apr)];

        cell.Status.Should().Be("COMPLETE");
        cell.IsManuallyOverridden.Should().BeTrue();
        cell.OverrideReason.Should().Contain("DST-overgang");
        cell.OverriddenByUserId.Should().Be("test-user");
        cell.CoveragePct.Should().Be(0.50, "metadata om auto-beregnet dekning beholdes");
    }

    [Fact(DisplayName = "RemoveOverride returnerer cellen til auto-status")]
    public async Task RemoveOverride_RestoresAutoStatus()
    {
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        await SeedImportAsync(db, "drivdal", "settlement", apr,
            coverage: 0.60, importedAt: apr.AddMonths(1));

        var svc = NewService(db, now);
        await svc.SetOverrideAsync("drivdal", "settlement", apr, "Verifisert", "test-user", default);

        var withOverride = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        withOverride.Cells[new("drivdal", "settlement", apr)].Status.Should().Be("COMPLETE");

        await svc.RemoveOverrideAsync("drivdal", "settlement", apr, default);

        var afterRemove = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        var cell = afterRemove.Cells[new("drivdal", "settlement", apr)];
        cell.Status.Should().Be("PARTIAL", "auto-status er restored etter at override er fjernet");
        cell.IsManuallyOverridden.Should().BeFalse();
    }

    [Fact(DisplayName = "Override på OVERDUE-celle (ingen import) markerer som komplett")]
    public async Task Override_OnOverdueCell_BecomesComplete()
    {
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero); // > apr + 1mnd + lag

        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "operlog", 5);
        // Ingen import seedet → OVERDUE

        var svc = NewService(db, now);
        var before = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        before.Cells[new("drivdal", "operlog", apr)].Status.Should().Be("OVERDUE");

        await svc.SetOverrideAsync("drivdal", "operlog", apr,
            "Anlegget hadde planlagt vedlikehold — ingen alarmer forventet", "test-user", default);

        var after = await svc.GetMatrixAsync(apr, apr.AddMonths(1), default);
        var cell = after.Cells[new("drivdal", "operlog", apr)];
        cell.Status.Should().Be("COMPLETE");
        cell.IsManuallyOverridden.Should().BeTrue();
        cell.RowsImported.Should().BeNull("ingen import bak override-en");
    }

    [Fact(DisplayName = "RemoveOverride er idempotent (no-op hvis ingen finnes)")]
    public async Task RemoveOverride_Idempotent()
    {
        await using var db = NewDb();
        var svc = NewService(db, DateTimeOffset.UtcNow);

        // Kalle uten å kaste
        await svc.RemoveOverrideAsync("drivdal", "settlement",
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), default);

        db.DataCompletenessOverrides.Should().BeEmpty();
    }

    [Fact(DisplayName = "SetOverride er upsert — andre kall oppdaterer eksisterende")]
    public async Task SetOverride_Upserts()
    {
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        await using var db = NewDb();
        await SeedExpectationAsync(db, "drivdal", "settlement", 7);
        var svc = NewService(db, new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero));

        await svc.SetOverrideAsync("drivdal", "settlement", apr, "Første grunn", "user-a", default);
        await svc.SetOverrideAsync("drivdal", "settlement", apr, "Bedre grunn", "user-b", default);

        var overrides = await db.DataCompletenessOverrides.ToListAsync();
        overrides.Should().HaveCount(1);
        overrides[0].Reason.Should().Be("Bedre grunn");
        overrides[0].OverriddenByUserId.Should().Be("user-b");
    }

    /// <summary>No-op IQueryContext for in-memory testing.</summary>
    private sealed class TestQueryContext : IQueryContext
    {
        public IQueryable<T> Apply<T>(IQueryable<T> source)
            where T : IOwnedEntity => source;
    }

    /// <summary>
    /// Minimal TimeProvider med fast verdi. Brukes i stedet for
    /// FakeTimeProvider fra Microsoft.Extensions.TimeProvider.Testing
    /// for å unngå NuGet-pakke for kun denne testfila.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
