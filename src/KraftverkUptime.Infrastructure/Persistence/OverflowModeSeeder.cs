using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Setter standard overflow-modus for spesifikke anlegg som krever proxy-
/// strategi istedenfor native SCADA-tag (Spec OVERFLOW-PROXY):
/// <list type="bullet">
///   <item>Stølskraft → <c>ProductionStateProxy</c> (drikkevannskraftverk, ingen INNTAK-tags).</item>
///   <item>Ørsdalen → <c>ProductionStateProxy</c> (SPEC-IMPORT-KONSOLIDERT-15MIN
///   Endring D pkt. 3: ~0 inntaksmagasin — overløp straks maskinen ikke
///   produserer; minstevannføring 100 l/s berører ikke proxyen. Krever kun
///   <c>ORSDAL_G1_GEN_P_PV</c>. Avløser tidligere LevelProxy-valg.)</item>
/// </list>
///
/// Idempotent: kun anlegg som fortsatt har seed-verdier (<c>NativeTag</c>,
/// eller for Ørsdalen den tidligere seedede <c>LevelProxy</c>) oppdateres.
/// Hvis drifts-leder har endret modus manuelt via PlantAdmin til noe annet
/// lar vi det stå.
/// </summary>
public static class OverflowModeSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OverflowModeSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Bruker raw SQL for å bypass'e global query filter + være idempotent
            // (kun oppdaterer rader som fortsatt har default-verdien).
            const string updateStolskraftSql = """
                UPDATE core.plants
                SET overflow_mode = 'ProductionStateProxy'
                WHERE id = 'stolskraft' AND overflow_mode = 'NativeTag';
                """;
            var stolskraftUpdated = await db.Database
                .ExecuteSqlRawAsync(updateStolskraftSql, ct).ConfigureAwait(false);
            if (stolskraftUpdated > 0)
            {
                logger.LogInformation(
                    "Stølskraft: satte OverflowMode = ProductionStateProxy (drikkevannskraftverk).");
            }

            // Ørsdalen: ProductionStateProxy. 'LevelProxy' i WHERE-lista migrerer
            // databaser som fikk den tidligere seed-verdien — LevelProxy var aldri
            // funksjonell for Ørsdalen (HRV ble aldri fylt inn) og var uansett
            // feil modell for et elvekraftverk uten magasin.
            const string updateOrsdalenPlantSql = """
                UPDATE core.plants
                SET overflow_mode = 'ProductionStateProxy'
                WHERE id = 'orsdalen' AND overflow_mode IN ('NativeTag', 'LevelProxy');
                """;
            var orsdalenPlantUpdated = await db.Database
                .ExecuteSqlRawAsync(updateOrsdalenPlantSql, ct).ConfigureAwait(false);
            if (orsdalenPlantUpdated > 0)
            {
                logger.LogInformation(
                    "Ørsdalen: satte OverflowMode = ProductionStateProxy (overløp straks "
                    + "maskinen ikke produserer — ~0 inntaksmagasin).");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "OverflowModeSeeder feilet — anlegg kan ha feil overflow-modus inntil "
                + "drifts-leder konfigurerer dem manuelt via PlantAdmin.");
        }
    }
}
