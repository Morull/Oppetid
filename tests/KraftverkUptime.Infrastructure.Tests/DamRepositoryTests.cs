using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for <see cref="DbDamRepository"/>. Verifiserer Steg 4 i
/// SPEC-KASKADE-DAMMER:
///   - Hent alle dammer for plant
///   - GetTerminalDamAsync returnerer eneste IsTurbineIntake=true
///   - Plant uten dammer → tom liste / null
///   - Flytting av IsTurbineIntake mellom dammer
///   - Fersk insert via AddAsync stemples med OwnerOrgId
///
/// In-memory EF — den deferrable unique-constraint-en er Postgres-spesifikk
/// og verifiseres ikke her; den er lagt til via raw SQL i bootstrapperen.
/// </summary>
public class DamRepositoryTests
{
    private static KraftverkDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<KraftverkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new KraftverkDbContext(options, new TestQueryContext());
    }

    private static DbDamRepository NewRepo(KraftverkDbContext db)
        => new(db, new TestCurrentUser("dev-org"));

    [Fact]
    public async Task GetForPlantAsync_TomtAnlegg_ReturnererTomListe()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        var rows = await repo.GetForPlantAsync("ukjent", default);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTerminalDamAsync_AnleggUtenDammer_ReturnererNull()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        var dam = await repo.GetTerminalDamAsync("drivdal", default);
        dam.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_NyDam_LagrerMedOwnerOrgId()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        var dam = new Dam("drivdal", "drivdal_main", "Drivdal", 1, true, null, null, null);
        await repo.AddAsync(dam, default);

        var saved = await db.Dams.FirstAsync();
        saved.OwnerOrgId.Should().Be("dev-org");
        saved.PlantId.Should().Be("drivdal");
        saved.DamId.Should().Be("drivdal_main");
        saved.IsTurbineIntake.Should().BeTrue();
    }

    [Fact]
    public async Task GetTerminalDamAsync_FlereDammer_ReturnererTerminalDam()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        await repo.AddAsync(new Dam("haukland", "haukland_stolsvt", "Stølsvatn", 1, false, null, null, null), default);
        await repo.AddAsync(new Dam("haukland", "haukland_gjelevt", "Gjelevatn", 1, false, null, null, null), default);
        await repo.AddAsync(new Dam("haukland", "haukland_skrstmvt", "Skårstemmevatn", 2, false, null, null, null), default);
        await repo.AddAsync(new Dam("haukland", "haukland_stemmevt", "Stemmevatn", 3, true, null, null, null), default);

        var terminal = await repo.GetTerminalDamAsync("haukland", default);
        terminal.Should().NotBeNull();
        terminal!.DamId.Should().Be("haukland_stemmevt");
        terminal.CascadePosition.Should().Be(3);
    }

    [Fact]
    public async Task GetForPlantAsync_FlereDammer_SortertEtterCascadePosition()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        // Lagt til i upakket rekkefølge — repo skal sortere
        await repo.AddAsync(new Dam("haukland", "haukland_stemmevt", "Stemmevatn", 3, true, null, null, null), default);
        await repo.AddAsync(new Dam("haukland", "haukland_stolsvt", "Stølsvatn", 1, false, null, null, null), default);
        await repo.AddAsync(new Dam("haukland", "haukland_skrstmvt", "Skårstemmevatn", 2, false, null, null, null), default);

        var rows = await repo.GetForPlantAsync("haukland", default);
        rows.Should().HaveCount(3);
        rows[0].CascadePosition.Should().Be(1);
        rows[1].CascadePosition.Should().Be(2);
        rows[2].CascadePosition.Should().Be(3);
    }

    [Fact]
    public async Task UpdateAsync_FlytterIsTurbineIntake_TerminalEndres()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        await repo.AddAsync(new Dam("plant", "dam_a", "A", 1, false, null, null, null), default);
        await repo.AddAsync(new Dam("plant", "dam_b", "B", 2, true, null, null, null), default);

        // Flytt intake fra B til A — i én transaksjon (ingen unique-violation
        // i in-memory; Postgres-constraint er deferrable og ville håndtert begge
        // SaveChanges-kall i én transaksjon i prod).
        var b = await repo.GetTerminalDamAsync("plant", default);
        b!.DamId.Should().Be("dam_b");

        await repo.UpdateAsync(b with { IsTurbineIntake = false }, default);
        await repo.UpdateAsync(new Dam("plant", "dam_a", "A", 1, true, null, null, null), default);

        var newTerminal = await repo.GetTerminalDamAsync("plant", default);
        newTerminal!.DamId.Should().Be("dam_a");
    }

    [Fact]
    public async Task UpdateAsync_OppdatererHrvLrvVolum()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        await repo.AddAsync(new Dam("plant", "main", "Main", 1, true, null, null, null), default);

        var updated = new Dam("plant", "main", "Main", 1, true,
            HrvMoh: 450.5, LrvMoh: 432.0, VolumeMm3: 12.5);
        await repo.UpdateAsync(updated, default);

        var read = await repo.GetTerminalDamAsync("plant", default);
        read!.HrvMoh.Should().Be(450.5);
        read.LrvMoh.Should().Be(432.0);
        read.VolumeMm3.Should().Be(12.5);
    }

    [Fact]
    public async Task UpdateAsync_UkjentDam_KasterInvalidOperationException()
    {
        await using var db = NewDb();
        var repo = NewRepo(db);

        var act = () => repo.UpdateAsync(
            new Dam("plant", "ikke-finnes", "X", 1, true, null, null, null), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>No-op IQueryContext for in-memory testing — ingen tenant-filtering.</summary>
    private sealed class TestQueryContext : IQueryContext
    {
        public IQueryable<T> Apply<T>(IQueryable<T> source)
            where T : IOwnedEntity => source;
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public TestCurrentUser(string orgId) { OrgId = orgId; }
        public string UserId => "test-user";
        public string OrgId { get; }
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public bool HasAllPlantsAccess => true;
        public IReadOnlySet<string> AccessiblePlantIds { get; } = new HashSet<string>();
    }
}
