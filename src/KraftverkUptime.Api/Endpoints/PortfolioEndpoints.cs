using System.Globalization;
using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Reporting.Portefolje;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Portefølje-endepunkt:
///   GET /api/v1/portfolio/kpis?from=&amp;to=
///
/// Returnerer KPI-er for alle anlegg som har en settlement-import som dekker
/// perioden. Anlegg-uavhengig — alle 11 anlegg listes i samme respons.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/portfolio")
            .WithTags("Portfolio")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/kpis", GetKpisAsync)
            .WithName("GetPortfolioKpis")
            .WithSummary("Henter KPI-er på tvers av alle anlegg for en gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<PortfolioKpiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/vakt-roi", GetVaktRoiAsync)
            .WithName("GetPortfolioVaktRoi")
            .WithSummary("Aggregert Vakt-ROI på tvers av alle anlegg for en gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<PortfolioVaktRoiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Portefølje-vakt-kost — global innstilling lagret i plant_configuration
        // med plant_id="_portfolio_". Spec NESTE-CHAT-VAKTROI-OG-UI-FIKS.md Del B:
        // verdien skal persisteres i DB så den er felles for alle brukere og
        // overlever reload.
        group.MapGet("/vakt-kost", GetVaktKostAsync)
            .WithName("GetPortfolioVaktKost")
            .WithSummary("Lagret portefølje-vakt-kost (NOK/år).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<PortfolioVaktKostResponse>(StatusCodes.Status200OK);

        group.MapPut("/vakt-kost", SetVaktKostAsync)
            .WithName("SetPortfolioVaktKost")
            .WithSummary("Setter portefølje-vakt-kost (NOK/år). Krever PlantAdmin.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<PortfolioVaktKostResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private const string VaktKostPlantId = "_portfolio_";
    private const string VaktKostKey = "vakt_kost_nok_per_aar";
    private const double VaktKostDefault = 360_000;

    private static async Task<IResult> GetVaktKostAsync(
        IPlantConfiguration config, CancellationToken ct)
    {
        var stored = await config.GetAsync<double?>(VaktKostPlantId, VaktKostKey, ct)
            .ConfigureAwait(false);
        return Results.Ok(new PortfolioVaktKostResponse(stored ?? VaktKostDefault));
    }

    private static async Task<IResult> SetVaktKostAsync(
        PortfolioVaktKostRequest body,
        IPlantConfiguration config,
        CancellationToken ct)
    {
        if (body is null || body.NokPerAar < 0)
        {
            return Results.Problem(
                title: "Ugyldig vakt-kost",
                detail: "NokPerAar må være ≥ 0.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        await config.SetAsync(VaktKostPlantId, VaktKostKey, body.NokPerAar, ct).ConfigureAwait(false);
        return Results.Ok(new PortfolioVaktKostResponse(body.NokPerAar));
    }

    public sealed record PortfolioVaktKostResponse(double NokPerAar);
    public sealed record PortfolioVaktKostRequest(double NokPerAar);

    private static async Task<IResult> GetVaktRoiAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? topN,
        string? vaktStartLokal,
        string? vaktSluttLokal,
        string? oppmoteLokal,
        IPortfolioVaktRoiQueryService service,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(
                title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(
                title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var top = topN ?? 10;

        if (!TryParseVaktOptions(vaktStartLokal, vaktSluttLokal, oppmoteLokal,
            out var vaktOptions, out var vaktProblem))
        {
            return vaktProblem!;
        }

        var response = await service.GetAsync(fromUtc, toUtc, top, vaktOptions, ct)
            .ConfigureAwait(false);
        return Results.Ok(response);
    }

    private static bool TryParseVaktOptions(
        string? vaktStartLokal, string? vaktSluttLokal, string? oppmoteLokal,
        out VaktTidsmodellOptions? options, out IResult? problem)
    {
        options = null;
        problem = null;
        if (vaktStartLokal is null && vaktSluttLokal is null && oppmoteLokal is null)
        {
            return true;
        }
        var def = VaktTidsmodellOptions.Default;
        if (!TryParseHm(vaktStartLokal, def.EttermiddagStart, out var start)
            || !TryParseHm(vaktSluttLokal, def.MorgenCutoff, out var slutt)
            || !TryParseHm(oppmoteLokal, def.OppmoteTidspunkt, out var oppmote))
        {
            problem = Results.Problem(
                title: "Ugyldig vakt-vindu",
                detail: "Forventer HH:mm-format.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        options = def with
        {
            EttermiddagStart = start,
            MorgenCutoff = slutt,
            OppmoteTidspunkt = oppmote,
        };
        return true;
    }

    private static bool TryParseHm(string? raw, TimeSpan fallback, out TimeSpan parsed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = fallback;
            return true;
        }
        if (TimeSpan.TryParseExact(raw.Trim(), [@"hh\:mm", @"h\:mm"],
            CultureInfo.InvariantCulture, out var ts))
        {
            parsed = ts;
            return true;
        }
        parsed = default;
        return false;
    }

    private static async Task<IResult> GetKpisAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IPortfolioQueryService service,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(
                title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(
                title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await service.GetKpisAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(response);
    }
}
