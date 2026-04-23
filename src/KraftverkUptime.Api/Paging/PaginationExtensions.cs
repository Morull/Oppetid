using KraftverkUptime.Core.Paging;

namespace KraftverkUptime.Api.Paging;

public sealed class PaginationOptions
{
    public const string SectionName = "Pagination";
    public int DefaultPageSize { get; set; } = 50;
    public int MaxPageSize { get; set; } = 500;
}

public static class PaginationExtensions
{
    public static IServiceCollection AddKraftverkPagination(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PaginationOptions>()
            .Bind(configuration.GetSection(PaginationOptions.SectionName))
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Tvinger maks sidestørrelse. Kalles typisk i endepunkter før spørring utføres.
    /// </summary>
    public static int ClampPageSize(int? requested, PaginationOptions options)
    {
        if (requested is null || requested <= 0) return options.DefaultPageSize;
        return Math.Min(requested.Value, options.MaxPageSize);
    }

    public static PagedResult<T> ToPagedResult<T>(this IReadOnlyList<T> items, string? nextCursor, int pageSize)
        => new(items, nextCursor, pageSize);
}
