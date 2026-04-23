using KraftverkUptime.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Kjører migreringer ved oppstart når konfigurasjonen tillater det. Brukes typisk i dev og staging.
/// I prod skal migreringer kjøres som eget steg i CI/CD – sett RunMigrationsOnStartup = false.
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task ApplyMigrationsAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrapper");
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        if (!options.RunMigrationsOnStartup)
        {
            logger.LogInformation("Database migrations are disabled at startup.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false);
            var list = pending.ToList();
            if (list.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending migrations.", list.Count);
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("No pending migrations. Ensuring database is created (first run).");
                // Ingen migreringer generert ennå (f.eks. helt nytt checkout) – fall tilbake til EnsureCreated
                // slik at dev-oppstarten ikke kræsjer. Fjern dette fallbacket når første migrering er lagt til.
                var created = await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
                if (created)
                {
                    logger.LogWarning("Database schema ble opprettet med EnsureCreated. Kjør 'dotnet ef migrations add Initial' og commit migreringen før prod.");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database bootstrap failed.");
            throw;
        }
    }
}
