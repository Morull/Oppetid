using KraftverkUptime.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// EF-implementasjon av <see cref="IPlantOverflowConfigProvider"/>. Slår opp
/// <see cref="Entities.PlantRegistration.OverflowMode"/> via <see cref="KraftverkDbContext"/>.
/// Bypasser global query filter med <c>IgnoreQueryFilters</c> fordi denne
/// brukes av analyse-pipeline som ikke nødvendigvis har et brukerkontekst —
/// multi-tenant-sikkerhet håndteres av callerne.
/// </summary>
public sealed class DbPlantOverflowConfigProvider : IPlantOverflowConfigProvider
{
    private readonly KraftverkDbContext _db;

    public DbPlantOverflowConfigProvider(KraftverkDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<OverflowMode> GetOverflowModeAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        var mode = await _db.Plants
            .IgnoreQueryFilters()
            .Where(p => p.Id == plantId)
            .Select(p => (OverflowMode?)p.OverflowMode)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return mode ?? OverflowMode.NativeTag;
    }
}
