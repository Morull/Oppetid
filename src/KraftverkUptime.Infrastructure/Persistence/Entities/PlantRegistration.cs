using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// Anleggsregistrering. Minimal i v1 – utvides med enheter, settlement-mapping osv. i Prompt 2.
/// </summary>
public sealed class PlantRegistration : IOwnedEntity, ISoftDeletable
{
    public string Id { get; set; } = string.Empty;     // plantId
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId => Id;
    public string Name { get; set; } = string.Empty;
    public PlantType Type { get; set; }
    public double InstalledCapacityMw { get; set; }
    public string TimeZone { get; set; } = "Europe/Oslo";

    /// <summary>
    /// Day-ahead prisområde (NO1–NO5). Brukes til å plukke ut riktig spotpris-
    /// baseline for capture rate og ubalanse-beregninger. Default NO2 dekker
    /// hele Dalane Kraft-porteføljen (Sokndal/Egersund-området).
    /// </summary>
    public string PriceArea { get; set; } = "NO2";

    /// <summary>
    /// Strategi for hvordan Vakt-ROI bestemmer overløp i counterfactual-vinduet.
    /// Default <see cref="Core.Domain.OverflowMode.NativeTag"/> = bruk SCADA
    /// overflow-tag. <see cref="Core.Domain.OverflowMode.LevelProxy"/> = utled
    /// fra terminal-damens nivå vs HRV. <see cref="Core.Domain.OverflowMode.ProductionStateProxy"/> =
    /// utled fra produksjonshistorikk (for drikkevannskraftverk uten magasin).
    /// </summary>
    public OverflowMode OverflowMode { get; set; } = OverflowMode.NativeTag;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
