using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Setter anleggs-metadata (turbin-type, fallhøyde, energiekvivalent) for
/// de 11 Dalane-anleggene fra drifts-leders kraftverkoversikt 2026-05-21.
///
/// Idempotent per kolonne: kun rader hvor verdien fortsatt er NULL får
/// default. Hvis drifts-leder har skrevet inn et tall via PlantAdmin lar
/// vi det stå.
///
/// Verdiene kommer fra "Egne Anlegg"-fanen, radene:
///   R9  Turbin
///   R10 Fallhøyde m
///   R56 Energiekvivalent kWh/m³
/// </summary>
public static class PlantMetadataSeeder
{
    private sealed record Defaults(
        string PlantId,
        string TurbineType,
        double HeadM,
        double EnergyEquivalentKwhPerM3,
        int CommissioningYear);

    private static readonly Defaults[] Items =
    [
        new("ogreyfoss",  "Francis",  66,   0.15053445,           1905),
        new("honnefoss",  "Francis",  39.5, 0.09009258749999999,  1956),
        new("liavatn",    "Kaplan",   14,   0.031931549999999996, 2024),
        new("grodemfoss", "Francis",  59,   0.134568675,          1939),
        new("lindland",   "Francis",  98,   0.22352084999999997,  2002),
        new("haukland",   "Francis", 253,   0.577048725,          2013),
        new("drivdal",    "Francis", 100,   0.2280825,            2008),
        new("logjen",     "Francis",  34.7, 0.0791446275,         2007),
        new("orsdalen",   "Pelton",  336,   0.7663572,            2023),
        new("vikesa",     "Francis", 100,   0.2280825,            2003),
        new("stolskraft", "Francis", 107,   0.244048275,          2003),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PlantMetadataSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var oppdatert = 0;
            foreach (var d in Items)
            {
                // Oppdater hvert felt separat med IS NULL-filter, slik at
                // drifts-leder kan overstyre én verdi uten at de andre
                // tilbakestilles.
                const string sqlTurbin = """
                    UPDATE core.plants SET turbine_type = @p0
                    WHERE id = @p1 AND turbine_type IS NULL;
                    """;
                oppdatert += await db.Database.ExecuteSqlRawAsync(
                    sqlTurbin, new object[] { d.TurbineType, d.PlantId }, ct).ConfigureAwait(false);

                const string sqlHead = """
                    UPDATE core.plants SET head_m = @p0
                    WHERE id = @p1 AND head_m IS NULL;
                    """;
                oppdatert += await db.Database.ExecuteSqlRawAsync(
                    sqlHead, new object[] { d.HeadM, d.PlantId }, ct).ConfigureAwait(false);

                const string sqlEnergy = """
                    UPDATE core.plants SET energy_equivalent_kwh_per_m3 = @p0
                    WHERE id = @p1 AND energy_equivalent_kwh_per_m3 IS NULL;
                    """;
                oppdatert += await db.Database.ExecuteSqlRawAsync(
                    sqlEnergy, new object[] { d.EnergyEquivalentKwhPerM3, d.PlantId }, ct).ConfigureAwait(false);

                const string sqlYear = """
                    UPDATE core.plants SET commissioning_year = @p0
                    WHERE id = @p1 AND commissioning_year IS NULL;
                    """;
                oppdatert += await db.Database.ExecuteSqlRawAsync(
                    sqlYear, new object[] { d.CommissioningYear, d.PlantId }, ct).ConfigureAwait(false);
            }

            if (oppdatert > 0)
            {
                logger.LogInformation(
                    "PlantMetadata: seedet {Count} metadata-felter (turbin/fallhøyde/energiekv) " +
                    "for de 11 Dalane-anleggene fra kraftverkoversikten.",
                    oppdatert);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "PlantMetadataSeeder feilet — anlegg kan mangle metadata inntil "
                + "drifts-leder fyller inn manuelt via PlantAdmin.");
        }
    }
}
