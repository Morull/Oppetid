using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Classification.Config;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Configuration;

/// <summary>
/// Bygger <see cref="PlantClassificationConfig"/> ved å kombinere anleggs-
/// grunndata fra <c>core.plants</c> (type, installert effekt) med klassifi-
/// seringsparametre fra <c>core.plant_configuration</c>. Hver parameter lagres
/// som egen rad i plant_configuration for å støtte separat redigering og
/// endringshistorikk per parameter; defaults fra PlantClassificationConfig-
/// record-en brukes når en parameter ikke er overstyrt.
///
/// Caching: <see cref="IPlantConfiguration"/> har allerede 5-min memory cache,
/// så vi trenger ikke egen cache her.
/// </summary>
public sealed class PlantClassificationConfigProvider
{
    /// <summary>Konfigurasjonsnøkkel for <c>PlantClassificationConfig.DeratingThreshold</c>.</summary>
    public const string KeyDeratingThreshold = "classification.deratingThreshold";

    /// <summary>Konfigurasjonsnøkkel for <c>PlantClassificationConfig.SustainedStopHours</c>.</summary>
    public const string KeySustainedStopHours = "classification.sustainedStopHours";

    /// <summary>Konfigurasjonsnøkkel for <c>PlantClassificationConfig.MarginalCostNokMwh</c>.</summary>
    public const string KeyMarginalCostNokMwh = "classification.marginalCostNokMwh";

    private readonly KraftverkDbContext _db;
    private readonly IPlantConfiguration _plantConfig;

    public PlantClassificationConfigProvider(
        KraftverkDbContext db,
        IPlantConfiguration plantConfig)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _plantConfig = plantConfig ?? throw new ArgumentNullException(nameof(plantConfig));
    }

    public async Task<PlantClassificationConfig> GetAsync(string plantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plantId))
        {
            throw new ArgumentException("plantId mangler.", nameof(plantId));
        }

        var plant = await _db.Plants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == plantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Anlegg '{plantId}' er ikke registrert i core.plants.");

        // Default-verdier kommer fra PlantClassificationConfig-record-en selv.
        var defaults = new PlantClassificationConfig
        {
            PlantId = plantId,
            PlantType = plant.Type,
            NominalPowerMw = plant.InstalledCapacityMw,
        };

        var derating = await _plantConfig.GetAsync<double?>(plantId, KeyDeratingThreshold, ct).ConfigureAwait(false);
        var sustained = await _plantConfig.GetAsync<int?>(plantId, KeySustainedStopHours, ct).ConfigureAwait(false);
        var marginalCost = await _plantConfig.GetAsync<double?>(plantId, KeyMarginalCostNokMwh, ct).ConfigureAwait(false);

        return defaults with
        {
            DeratingThreshold = derating ?? defaults.DeratingThreshold,
            SustainedStopHours = sustained ?? defaults.SustainedStopHours,
            MarginalCostNokMwh = marginalCost ?? defaults.MarginalCostNokMwh,
        };
    }
}
