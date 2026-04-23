namespace KraftverkUptime.Core.Paging;

/// <summary>
/// Standard envelope for listeresponser på API-et.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize);
