using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder felles Dalane-månedsprofil for normalårsproduksjonen på alle anlegg
/// (SPEC-MAANEDSPROFIL-NORMALAAR, 2026-07-02). Profilen kommer fra driftsleders
/// kraftverkoversikt (middelproduksjon × månedsfordeling, sum 100 %).
///
/// Idempotent etter mønster fra <see cref="NormalAarsproduksjonSeeder"/>: kun
/// rader der kolonnen er <c>NULL</c> oppdateres — manuelt redigerte profiler
/// (via PlantAdmin) beholdes. Profilen er felles for alle verk inntil
/// driftsleder differensierer per felt.
/// </summary>
public static class MaanedsprofilSeeder
{
    /// <summary>
    /// Felles Dalane-profil (jan → des), sum 100,0. Holdes synkronisert med
    /// arket til driftsleder.
    /// </summary>
    private static readonly double[] DefaultProfil =
        [13.1, 11.0, 9.1, 8.1, 4.7, 2.3, 1.3, 4.9, 8.7, 10.4, 12.4, 14.0];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MaanedsprofilSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Idempotent: kun rader der profilen er NULL får default-en.
            const string sql = """
                UPDATE core.plants
                SET maanedsprofil_prosent = @p0
                WHERE maanedsprofil_prosent IS NULL;
                """;
            var rows = await db.Database.ExecuteSqlRawAsync(
                sql, new object[] { DefaultProfil }, ct).ConfigureAwait(false);

            if (rows > 0)
            {
                logger.LogInformation(
                    "Maanedsprofil: seedet felles Dalane-profil for {Count} anlegg " +
                    "(sum {Sum:F1} %).", rows, DefaultProfil.Sum());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "MaanedsprofilSeeder feilet — normalår-visninger faller tilbake til "
                + "flat pro-rata inntil profil settes via PlantAdmin.");
        }
    }
}
