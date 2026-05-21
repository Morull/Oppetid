using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Setter <c>NormalAarsproduksjonGwh</c> for de 11 Dalane-anleggene fra
/// drifts-leders kraftverkoversikt (Excel 2026-05-21, "Årlig Produksjon
/// GWh (2022)"-raden).
///
/// Idempotent: kun anlegg hvor verdien fortsatt er <c>NULL</c> oppdateres.
/// Hvis drifts-leder har endret verdien manuelt via PlantAdmin lar vi det
/// stå. Sum 210,9 GWh på tvers av porteføljen i specen.
///
/// Verdiene er per 2022 — fra et anlegg som ble bygd i 2024 (Liavatn) er
/// dette estimat fra konsesjonen, ikke faktiske tall. Drifts-leder kan
/// finjustere via UI.
/// </summary>
public static class NormalAarsproduksjonSeeder
{
    /// <summary>
    /// (plant_id → forventet årsproduksjon i GWh). Holdes synkronisert med
    /// "Egne Anlegg"-fanen i kraftverkoversikten.
    /// </summary>
    private static readonly (string PlantId, double Gwh)[] Defaults =
    [
        ("ogreyfoss",  65.0),
        ("honnefoss",  12.0),
        ("liavatn",     2.0),
        ("grodemfoss", 18.0),
        ("lindland",   43.0),
        ("haukland",   21.0),
        ("drivdal",     8.0),
        ("logjen",      2.0),
        ("orsdalen",   14.0),
        ("vikesa",     18.5),
        ("stolskraft",  7.4),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NormalAarsproduksjonSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var oppdatert = 0;
            foreach (var (plantId, gwh) in Defaults)
            {
                // Idempotent: kun rader hvor verdien er NULL får default-en.
                // Hvis drifts-leder har skrevet inn et tall (også 0) beholdes det.
                const string sql = """
                    UPDATE core.plants
                    SET normal_aarsproduksjon_gwh = @p0
                    WHERE id = @p1
                      AND normal_aarsproduksjon_gwh IS NULL;
                    """;
                var rows = await db.Database.ExecuteSqlRawAsync(
                    sql, new object[] { gwh, plantId }, ct).ConfigureAwait(false);
                oppdatert += rows;
            }

            if (oppdatert > 0)
            {
                logger.LogInformation(
                    "NormalAarsproduksjon: seedet default-verdier for {Count} anlegg fra " +
                    "kraftverkoversikten 2022. Total {Total:F1} GWh.",
                    oppdatert, Defaults.Sum(d => d.Gwh));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NormalAarsproduksjonSeeder feilet — anlegg kan ha null-verdi inntil "
                + "drifts-leder fyller inn manuelt via PlantAdmin.");
        }
    }
}
