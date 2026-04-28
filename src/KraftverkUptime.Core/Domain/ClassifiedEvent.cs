namespace KraftverkUptime.Core.Domain;

/// <summary>
/// En state-endring eller hendelse i et anleggs drift, med presis tidsstempel
/// (sekund-presisjon når kilden er operlog, time-presisjon ved klassifikator-aggregat).
///
/// Eksempler:
///   – Trip kl. 14:23:01 (fra operlog FEIL_AL)
///   – Restart kl. 14:47:33 (fra operlog STARTER_AL)
///   – Klassifikator-overgang InService → ForcedDerating ved time-grense
///
/// EventLog kompletterer time-aggregert klassifisering: KPI-tellere bruker
/// timer, men event-baserte KPI-er (MTBF, MTTR, antall trip) bruker disse
/// radene direkte.
/// </summary>
public sealed record ClassifiedEvent(
    long Id,
    string OwnerOrgId,
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    UnitState State,
    string? CauseCode,
    double Confidence,
    string SourcesJson,
    string? Rationale);
