namespace KraftverkUptime.Api.Contracts;

/// <summary>
/// Wire-DTO for én nedetidshendelse på tvers av anlegg, slik den vises på
/// Oversikt-siden (driftsleder-blokken). Holder bare feltene Oversikt trenger —
/// en slankere projeksjon enn <see cref="NedetidEventDto"/> som er per-anlegg.
/// <see cref="PlantName"/> er med fordi listen blander anlegg og brukeren må se
/// hvilket anlegg hver hendelse gjelder uten ekstra oppslag.
/// </summary>
public sealed record OversiktNedetidEventDto(
    string PlantId,
    string PlantName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double VarighetTimer,
    string Kategori,
    string? CauseCode,
    double TapNok,
    bool HarOperlogMatch,
    string? Rationale);

/// <summary>
/// Aggregat-respons for <c>GET /api/v1/oversikt/nedetid-hendelser</c>:
/// de nyeste nedetidshendelsene på tvers av hele porteføljen, sortert nyeste
/// først og begrenset til <paramref name="AntallEvents"/> rader.
/// </summary>
public sealed record OversiktNedetidResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallEvents,
    IReadOnlyList<OversiktNedetidEventDto> Events);
