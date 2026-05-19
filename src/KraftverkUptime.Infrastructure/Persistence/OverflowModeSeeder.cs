using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Setter standard overflow-modus for spesifikke anlegg som krever proxy-
/// strategi istedenfor native SCADA-tag (Spec OVERFLOW-PROXY):
/// <list type="bullet">
///   <item>Stølskraft → <c>ProductionStateProxy</c> (drikkevannskraftverk, ingen INNTAK-tags).</item>
///   <item>Ørsdalen → <c>LevelProxy</c> med 10 cm terskel (ingen overflow-tag, men UpstreamLevel finnes).</item>
/// </list>
///
/// Idempotent: kun anlegg som fortsatt har default-verdien <c>NativeTag</c>
/// oppdateres. Hvis drifts-leder har endret modus manuelt via PlantAdmin
/// lar vi det stå. HRV/LRV må drifts-leder fylle inn selv via PlantAdmin
/// for at LevelProxy skal gi tall ut (uten HRV returnerer service-en
/// <c>DataAvailable = false</c> med advarsel i logg).
/// </summary>
public static class OverflowModeSeeder
{
    /// <summary>Default-terskel for level-proxy. Drifts-leder kan endre per dam.</summary>
    private const int DefaultLevelProxyThresholdCm = 10;

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

            const string updateOrsdalenPlantSql = """
                UPDATE core.plants
                SET overflow_mode = 'LevelProxy'
                WHERE id = 'orsdalen' AND overflow_mode = 'NativeTag';
                """;
            var orsdalenPlantUpdated = await db.Database
                .ExecuteSqlRawAsync(updateOrsdalenPlantSql, ct).ConfigureAwait(false);
            if (orsdalenPlantUpdated > 0)
            {
                logger.LogInformation(
                    "Ørsdalen: satte OverflowMode = LevelProxy (utleder fra UpstreamLevel + HRV).");
            }

            // Ørsdalen-dammen får default terskel 10cm hvis den ikke er satt.
            // HRV/LRV må drifts-leder fylle inn selv via PlantAdmin.
            const string updateOrsdalenDamSql = """
                UPDATE core.dams
                SET overflow_proxy_threshold_cm = @p0
                WHERE plant_id = 'orsdalen'
                  AND dam_id = 'orsdalen_main'
                  AND overflow_proxy_threshold_cm IS NULL;
                """;
            var orsdalenDamUpdated = await db.Database
                .ExecuteSqlRawAsync(updateOrsdalenDamSql, DefaultLevelProxyThresholdCm, ct)
                .ConfigureAwait(false);
            if (orsdalenDamUpdated > 0)
            {
                logger.LogInformation(
                    "Ørsdalen: satte default OverflowProxyThresholdCm = {Cm} cm på terminal-dammen. "
                    + "Husk å fylle inn HRV/LRV via PlantAdmin.",
                    DefaultLevelProxyThresholdCm);
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
