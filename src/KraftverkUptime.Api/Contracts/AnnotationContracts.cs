using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Api.Contracts;

/// <summary>API-kontrakter for annoteringer. Slankere enn domain-typene — ingen multi-tenant-felter ut til klienten.</summary>
public sealed record DowntimeAnnotationDto(
    long Id,
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CategoryId,
    string? Comment,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    public static DowntimeAnnotationDto From(DowntimeAnnotation a) => new(
        a.Id, a.PlantId, a.StartUtc, a.EndUtc, a.CategoryId, a.Comment,
        a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.UpdatedBy);
}

public sealed record DowntimeCategoryDto(
    string Id,
    string DisplayName,
    string ColorHex,
    UnitState UnitStateOverride,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    string? Description)
{
    public static DowntimeCategoryDto From(DowntimeCategory c) => new(
        c.Id, c.DisplayName, c.ColorHex, c.UnitStateOverride, c.SortOrder, c.IsActive, c.IsSystem, c.Description);
}

/// <summary>
/// Body for POST /annotations. Tidspunkter må være time-presisjon (UTC).
/// <c>ReplaceIds</c> brukes for å eksplisitt løse overlapp: angitte annoteringer
/// blir soft-deletet før den nye opprettes. Hvis <c>ReplaceIds</c> ikke dekker
/// alle eksisterende overlapp, returneres 409 med listen.
/// </summary>
public sealed record CreateAnnotationRequest(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CategoryId,
    string? Comment,
    IReadOnlyList<long>? ReplaceIds);

public sealed record UpdateAnnotationRequest(
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string? CategoryId,
    string? Comment,
    IReadOnlyList<long>? ReplaceIds);

/// <summary>Returneres med 409 når overlapp-validering feiler.</summary>
public sealed record AnnotationOverlapConflict(
    string Message,
    IReadOnlyList<DowntimeAnnotationDto> Overlapping);
