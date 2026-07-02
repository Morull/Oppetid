using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Api.Contracts;

/// <summary>
/// Wire-DTO for et nedetids-event slik det vises på /nedetid-siden.
/// Speiler <c>KraftverkUptime.Core.Domain.DowntimeEvent</c> men holder
/// API-kontrakten frikoblet fra domenet (kan bli versjonert separat).
/// </summary>
public sealed record NedetidEventDto(
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double VarighetTimer,
    string State,
    string Kategori,
    string? CauseCode,
    double TapMwh,
    double TapNok,
    int TimerSettlement,
    bool HarOperlogMatch,
    string? Rationale,
    // Detektert (SCADA-) start når en manuell start-korreksjon er anvendt;
    // null = StartUtc ER detektert. Override-nøkkel for dialogen.
    // SPEC-NEDETID-STARTTID-OVERRIDE.
    DateTimeOffset? DetectedStartUtc = null);

/// <summary>
/// Aggregat-respons med både event-listen og pre-beregnede totaler/grupperinger
/// som UI kan rendre uten ekstra LINQ-arbeid.
/// </summary>
public sealed record NedetidResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallEvents,
    double TotalNedetidTimer,
    double TotalTapMwh,
    double TotalTapNok,
    IReadOnlyList<NedetidKategoriSummary> KategoriSummaries,
    IReadOnlyList<NedetidEventDto> Events);

public sealed record NedetidKategoriSummary(
    string Kategori,
    int Antall,
    double TotalTimer,
    double TotalTapNok);

/// <summary>
/// Wire-DTO for Vakt-ROI per event. <see cref="OverflowTimerInCounterfactual"/>
/// og <see cref="OverflowDataMissing"/> kommer fra spec 2026-04-29 (overløp).
/// <see cref="ReddetProduksjon_NOK"/> og <see cref="ReddetUbalanse_NOK"/> er
/// fra spec v3 (ubalanse) og summerer til <see cref="ReddetNok"/>.
/// </summary>
public sealed record VaktRoiEventDto(
    NedetidEventDto Event,
    bool ErInnenforVakt,
    bool ErReddbar,
    DateTimeOffset? CounterfactualEndUtc,
    double EkstraTimerSpart,
    double ReddetMwh,
    double ReddetNok,
    double ReddetProduksjon_NOK,
    double ReddetUbalanse_NOK,
    int OverflowTimerInCounterfactual,
    bool OverflowDataMissing,
    bool PlanDataPartial,
    string Forklaring,
    // Spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md (2026-05-22):
    // Manuell overstyring av om vakta rykket ut. Default Auto for events
    // uten override-rad. UI viser dette i ny «Vakt utrykt»-kolonne.
    GuardResponseOverride GuardResponseOverride = GuardResponseOverride.Auto,
    // Spec VAKT-ROI-OVERLOP-V2: skill observert SCADA-overløp fra estimert
    // (tilsigsmodell). OverflowTimerInCounterfactual = sum av begge (uendret).
    int SavedOverflowHoursObserved = 0,
    int SavedOverflowHoursEstimated = 0,
    bool OverflowEstimateAvailable = false,
    double? OverflowEstimateHoursToFull = null,
    string? OverflowEstimateForklaring = null);

public sealed record VaktRoiResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double InstallertEffektMw,
    double SnittSpotprisNokMwh,
    double SnittUbalansetilleggNokMwh,
    string VaktStartLokal,
    string VaktSluttLokal,
    string OppmoteLokal,
    int AntallEventsTotalt,
    int AntallReddbareInnenforVakt,
    double TotalReddetMwh,
    double TotalReddetNok,
    double TotalReddetProduksjon_NOK,
    double TotalReddetUbalanse_NOK,
    double SnittEkstraTimerPerEvent,
    IReadOnlyList<VaktRoiEventDto> Events);
