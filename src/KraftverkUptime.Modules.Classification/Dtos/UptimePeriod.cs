using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Classification.Dtos;

/// <summary>
/// Analysens input: én plants parsede settlement-data + klassifiseringsparametre.
/// Byggs av caller (import-orkestrator) basert på <c>ParsedSettlement</c> +
/// <c>IPlantConfiguration</c>-oppslag.
/// </summary>
public sealed record UptimePeriod(
    ParsedSettlement Settlement,
    PlantClassificationConfig PlantConfig);
