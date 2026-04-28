namespace KraftverkUptime.Modules.Classification.Dtos;

/// <summary>
/// Én KPI-verdi med metadata. Value er nullable – en KPI som ikke kan beregnes
/// (manglende kolonne, divisjon på null) rapporteres som null, aldri som 0
/// eller NaN.
///
/// <see cref="Category"/> brukes til å gruppere i rapportbygg:
///   "drift"       – ServiceHours, ForcedOutageHours, OutOfServiceHours,
///                   InformationUnavailable_Hours, AvailabilityFactor
///   "marked"      – BidVolume, BidDelivery
///   "okonomi"     – TotalProduction, Spotomsetning, RkNetto, RkBruttoSalg/Kjop,
///                   Ubalanseresultat, Oppgjor
/// </summary>
public sealed record KpiResult(
    string Name,
    double? Value,
    string Unit,
    int HoursBasis,
    double Confidence,
    string Category,
    string Definition);
