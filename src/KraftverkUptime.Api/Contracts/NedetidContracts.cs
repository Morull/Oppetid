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
    string? Rationale);

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
/// og <see cref="OverflowDataMissing"/> kommer fra spec 2026-04-29: ROI gjelder
/// kun timer der det var overløp i magasinet i counterfactual-perioden.
/// </summary>
public sealed record VaktRoiEventDto(
    NedetidEventDto Event,
    bool ErInnenforVakt,
    bool ErReddbar,
    DateTimeOffset? CounterfactualEndUtc,
    double EkstraTimerSpart,
    double ReddetMwh,
    double ReddetNok,
    int OverflowTimerInCounterfactual,
    bool OverflowDataMissing,
    string Forklaring);

public sealed record VaktRoiResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double InstallertEffektMw,
    double SnittSpotprisNokMwh,
    double Kapasitetsfaktor,
    int AntallEventsTotalt,
    int AntallReddbareInnenforVakt,
    double TotalReddetMwh,
    double TotalReddetNok,
    double SnittEkstraTimerPerEvent,
    IReadOnlyList<VaktRoiEventDto> Events);
