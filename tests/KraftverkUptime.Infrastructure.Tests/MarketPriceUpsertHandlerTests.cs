using FluentAssertions;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Events;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for <see cref="MarketPriceUpsertHandler"/> mot in-memory EF.
/// Verifiserer happy path, idempotens (samme fil to ganger), null-pris-filtrering
/// og at riktig PriceArea hentes fra plant.
/// </summary>
public class MarketPriceUpsertHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static KraftverkDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<KraftverkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new KraftverkDbContext(options, new TestQueryContext());
    }

    private static SettlementHourlyRow Row(int hour, double? spot) =>
        new()
        {
            TimeUtc = T0.AddHours(hour),
            TimeLocal = T0.AddHours(hour),
            SpotprisNokMwh = spot,
            MwhElhub = 1,
        };

    private static UptimePeriod Period(string plantId, params SettlementHourlyRow[] rows) =>
        new(new ParsedSettlement
        {
            PlantName = plantId,
            SchemaVersion = "portal-v1",
            PeriodStartUtc = T0,
            PeriodEndUtc = T0.AddHours(24),
            Hourly = rows,
            Issues = Array.Empty<ValidationIssue>(),
        }, new PlantClassificationConfig
        {
            PlantId = plantId,
            PlantType = PlantType.Regulated,
            NominalPowerMw = 2.2,
        });

    [Fact]
    public async Task Handle_HappyPath_UpsertsAllaPrisRader()
    {
        await using var db = NewDb();
        db.Plants.Add(new PlantRegistration { Id = "drivdal", Name = "Drivdal", PriceArea = "NO2" });
        await db.SaveChangesAsync();

        var period = Period("drivdal", Row(0, 500), Row(1, 600), Row(2, 700));
        var provider = new FakePeriodProvider(period);
        var handler = new MarketPriceUpsertHandler(
            provider, db, TimeProvider.System, NullLogger<MarketPriceUpsertHandler>.Instance);

        await handler.HandleAsync(MakeEvent("drivdal"), default);

        var rows = await db.MarketPrices.OrderBy(x => x.TimeUtc).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.PriceArea == "NO2" && r.Source == "settlement");
        rows[0].PriceNokMwh.Should().Be(500);
        rows[2].PriceNokMwh.Should().Be(700);
    }

    [Fact]
    public async Task Handle_NullSpotpris_FilterresUt()
    {
        await using var db = NewDb();
        db.Plants.Add(new PlantRegistration { Id = "drivdal", Name = "Drivdal", PriceArea = "NO2" });
        await db.SaveChangesAsync();

        var period = Period("drivdal", Row(0, 500), Row(1, null), Row(2, 700));
        var handler = new MarketPriceUpsertHandler(
            new FakePeriodProvider(period), db, TimeProvider.System,
            NullLogger<MarketPriceUpsertHandler>.Instance);

        await handler.HandleAsync(MakeEvent("drivdal"), default);

        var rows = await db.MarketPrices.OrderBy(x => x.TimeUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().NotContain(r => r.TimeUtc == T0.AddHours(1));
    }

    [Fact]
    public async Task Handle_SammeFilToGanger_GirIngenDuplikater()
    {
        await using var db = NewDb();
        db.Plants.Add(new PlantRegistration { Id = "drivdal", Name = "Drivdal", PriceArea = "NO2" });
        await db.SaveChangesAsync();

        var period = Period("drivdal", Row(0, 500), Row(1, 600));
        var handler = new MarketPriceUpsertHandler(
            new FakePeriodProvider(period), db, TimeProvider.System,
            NullLogger<MarketPriceUpsertHandler>.Instance);

        await handler.HandleAsync(MakeEvent("drivdal"), default);
        await handler.HandleAsync(MakeEvent("drivdal"), default);

        var rows = await db.MarketPrices.ToListAsync();
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_PlantManglerPriceArea_BrukerNO2Default()
    {
        await using var db = NewDb();
        // PriceArea ikke satt eksplisitt → faller tilbake til "NO2" via property default
        db.Plants.Add(new PlantRegistration { Id = "drivdal", Name = "Drivdal" });
        await db.SaveChangesAsync();

        var period = Period("drivdal", Row(0, 500));
        var handler = new MarketPriceUpsertHandler(
            new FakePeriodProvider(period), db, TimeProvider.System,
            NullLogger<MarketPriceUpsertHandler>.Instance);

        await handler.HandleAsync(MakeEvent("drivdal"), default);

        var row = await db.MarketPrices.FirstAsync();
        row.PriceArea.Should().Be("NO2");
    }

    [Fact]
    public async Task Handle_UkjentPlant_HopperOver_UtenFeil()
    {
        await using var db = NewDb();
        var period = Period("ghost-plant", Row(0, 500));
        var handler = new MarketPriceUpsertHandler(
            new FakePeriodProvider(period), db, TimeProvider.System,
            NullLogger<MarketPriceUpsertHandler>.Instance);

        await handler.HandleAsync(MakeEvent("ghost-plant"), default);

        (await db.MarketPrices.AnyAsync()).Should().BeFalse();
    }

    private static SettlementImportedEvent MakeEvent(string plantId) => new()
    {
        PlantId = plantId,
        OwnerOrgId = "dev-org",
        BlobPath = "test",
        IdempotencyKey = "test-key",
        PeriodStartUtc = T0,
        PeriodEndUtc = T0.AddHours(24),
        HourCount = 24,
        IssueCount = 0,
    };

    private sealed class FakePeriodProvider : IUptimePeriodProvider
    {
        private readonly UptimePeriod _period;
        public FakePeriodProvider(UptimePeriod period) => _period = period;
        public Task<UptimePeriod> GetAsync(
            string assetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
            => Task.FromResult(_period);
    }

    /// <summary>No-op IQueryContext for in-memory testing — ingen tenant-filtering.</summary>
    private sealed class TestQueryContext : KraftverkUptime.Core.Security.IQueryContext
    {
        public IQueryable<T> Apply<T>(IQueryable<T> source)
            where T : KraftverkUptime.Core.Domain.IOwnedEntity => source;
    }
}
