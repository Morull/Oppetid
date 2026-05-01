using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Classification.Config;

/// <summary>
/// Anleggs-spesifikke terskelverdier brukt av proxy-klassifiseringen (Nivå 0).
/// Hentes fra <c>IPlantConfiguration</c> per plantId; default-verdier nedenfor
/// er fornuftige for mindre norske magasinverk.
///
/// Terskelverdiene er bevisst konservative: de skal heller overklassifisere
/// <c>ForcedOutage</c> enn å maskere faktisk nedetid. Når SCADA kobles inn
/// (Nivå 3) blir disse terskler mindre viktige fordi faktisk driftstilstand
/// er kjent.
/// </summary>
public sealed record PlantClassificationConfig
{
    /// <summary>Kanonsk plant-ID (f.eks. "Drivdal").</summary>
    public required string PlantId { get; init; }

    /// <summary>Hydro-anleggstype. Styrer klassifiseringsregel-sett.</summary>
    public required PlantType PlantType { get; init; }

    /// <summary>Installert effekt i MW. Brukes som nevner i CF/OF.</summary>
    public required double NominalPowerMw { get; init; }

    /// <summary>
    /// Elhub &lt; Plan × denne → ForcedDerating (avvik fra produksjonsplan teller
    /// som feil). Default 0.80 (20 % under plan = feil). Drifts-leder kan
    /// justere per anlegg via PlantAdmin-UI når et verk har høyere/lavere
    /// toleranse for plan-avvik.
    /// </summary>
    public double DeratingThreshold { get; init; } = 0.80;

    /// <summary>
    /// Sammenhengende 0-produksjonstimer ≥ denne → PlannedOutage-kandidat.
    /// Default 24 t (ett døgn).
    /// </summary>
    public int SustainedStopHours { get; init; } = 24;

    /// <summary>
    /// Omtrentlig marginalkostnad i NOK/MWh. Spotpris &lt; denne indikerer
    /// markedsstyrt stopp (ReserveShutdown). Default 100 NOK/MWh (konservativ
    /// for småkraft; tilpasses per verk).
    /// </summary>
    public double MarginalCostNokMwh { get; init; } = 100.0;
}
