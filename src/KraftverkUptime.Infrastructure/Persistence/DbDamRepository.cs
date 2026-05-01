using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// EF-implementasjon av <see cref="IDamRepository"/>. Multi-tenant via
/// <see cref="ICurrentUser"/> for OwnerOrgId-stempling ved insert.
///
/// Constraint <c>one_intake_per_plant</c> er deferrable i SQL-skjemaet —
/// EF kjører UPDATE-er som flytter <c>IsTurbineIntake</c> mellom dammer
/// som én transaksjon, og constraint sjekkes ved commit.
/// </summary>
public sealed class DbDamRepository : IDamRepository
{
    private readonly KraftverkDbContext _db;
    private readonly ICurrentUser _user;

    public DbDamRepository(KraftverkDbContext db, ICurrentUser user)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public async Task<IReadOnlyList<Dam>> GetForPlantAsync(string plantId, CancellationToken ct)
    {
        var rows = await _db.Dams
            .AsNoTracking()
            .Where(d => d.PlantId == plantId)
            .OrderBy(d => d.CascadePosition)
            .ThenBy(d => d.DamId)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(ToDomain).ToList();
    }

    public async Task<Dam?> GetTerminalDamAsync(string plantId, CancellationToken ct)
    {
        var row = await _db.Dams
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.PlantId == plantId && d.IsTurbineIntake, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async Task AddAsync(Dam dam, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dam);
        var entry = new DamEntry
        {
            PlantId = dam.PlantId,
            DamId = dam.DamId,
            Name = dam.Name,
            CascadePosition = dam.CascadePosition,
            IsTurbineIntake = dam.IsTurbineIntake,
            HrvMoh = dam.HrvMoh,
            LrvMoh = dam.LrvMoh,
            VolumeMm3 = dam.VolumeMm3,
            OwnerOrgId = _user.OrgId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _db.Dams.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Dam dam, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dam);
        var existing = await _db.Dams
            .FirstOrDefaultAsync(d => d.PlantId == dam.PlantId && d.DamId == dam.DamId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Dam ({dam.PlantId}, {dam.DamId}) finnes ikke — kan ikke oppdatere.");
        }
        existing.Name = dam.Name;
        existing.CascadePosition = dam.CascadePosition;
        existing.IsTurbineIntake = dam.IsTurbineIntake;
        existing.HrvMoh = dam.HrvMoh;
        existing.LrvMoh = dam.LrvMoh;
        existing.VolumeMm3 = dam.VolumeMm3;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static Dam ToDomain(DamEntry e)
        => new(e.PlantId, e.DamId, e.Name, e.CascadePosition,
               e.IsTurbineIntake, e.HrvMoh, e.LrvMoh, e.VolumeMm3);
}
