using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// CRUD for <c>core.data_source_expectations</c> (SPEC-IMPORT-COMPLETENESS
/// steg 7). Drifts-leder bruker disse via PlantAdmin-siden til å:
///   - Aktivere/deaktivere en kilde per anlegg (toggle is_active)
///   - Endre forventet lag (eks. fra 7 til 5 dager når en ny SLA er på plass)
///   - Legge til en ny kilde-type for et anlegg (eks. SCADA for Lindland
///     når en eksport-flow er etablert) — PUT er upsert.
///
/// Read er PlantReader (alle kan se konfigurasjonen). Write er PlantAdmin.
/// </summary>
public static class DataSourceExpectationsEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceExpectationsV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/data-source-expectations")
            .WithTags("DataStatus")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", ListAsync)
            .WithName("ListDataSourceExpectations")
            .WithSummary("Lister forventede datakilder for et anlegg.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DataSourceExpectationDto>>(StatusCodes.Status200OK);

        group.MapPut("/{sourceType}", UpsertAsync)
            .WithName("UpsertDataSourceExpectation")
            .WithSummary("Oppretter eller oppdaterer en forventning for et anlegg + kilde-type.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DataSourceExpectationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string plantId,
        KraftverkDbContext db,
        CancellationToken ct)
    {
        var rows = await db.DataSourceExpectations
            .AsNoTracking()
            .Where(e => e.PlantId == plantId)
            .OrderBy(e => e.SourceType)
            .ToListAsync(ct).ConfigureAwait(false);

        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> UpsertAsync(
        string plantId,
        string sourceType,
        UpsertDataSourceExpectationRequest body,
        KraftverkDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null)
        {
            return Results.Problem(title: "Mangler body", statusCode: StatusCodes.Status400BadRequest);
        }
        if (!IsKnownSourceType(sourceType))
        {
            return Results.Problem(
                title: "Ukjent kilde-type",
                detail: $"'{sourceType}' er ikke en kjent type. Tillatt: settlement, scada, operlog, hydrogrid_plan.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body.ExpectedLagDays < 0 || body.ExpectedLagDays > 90)
        {
            return Results.Problem(
                title: "Ugyldig expectedLagDays",
                detail: "Må være mellom 0 og 90 dager.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await db.DataSourceExpectations
            .FirstOrDefaultAsync(e => e.PlantId == plantId && e.SourceType == sourceType, ct)
            .ConfigureAwait(false);

        DataSourceExpectation entity;
        string action;
        object? payloadBefore = null;
        if (existing is null)
        {
            entity = new DataSourceExpectation
            {
                PlantId = plantId,
                SourceType = sourceType,
                Cadence = string.IsNullOrWhiteSpace(body.Cadence) ? "monthly" : body.Cadence!,
                ExpectedLagDays = body.ExpectedLagDays,
                IsActive = body.IsActive,
                ActivatedAtUtc = body.IsActive ? DateTimeOffset.UtcNow : null,
                DeactivatedAtUtc = body.IsActive ? null : DateTimeOffset.UtcNow,
            };
            db.DataSourceExpectations.Add(entity);
            action = "data_source_expectation.created";
        }
        else
        {
            payloadBefore = new
            {
                existing.IsActive, existing.Cadence, existing.ExpectedLagDays,
                existing.ActivatedAtUtc, existing.DeactivatedAtUtc
            };
            // Toggle-overgang: oppdater activated/deactivated-tidspunkt.
            if (existing.IsActive != body.IsActive)
            {
                if (body.IsActive)
                {
                    existing.ActivatedAtUtc = DateTimeOffset.UtcNow;
                    existing.DeactivatedAtUtc = null;
                }
                else
                {
                    existing.DeactivatedAtUtc = DateTimeOffset.UtcNow;
                }
            }
            existing.IsActive = body.IsActive;
            existing.ExpectedLagDays = body.ExpectedLagDays;
            if (!string.IsNullOrWhiteSpace(body.Cadence))
            {
                existing.Cadence = body.Cadence;
            }
            entity = existing;
            action = "data_source_expectation.updated";
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: action,
            entityType: "DataSourceExpectation",
            entityId: $"{plantId}/{sourceType}",
            payload: new
            {
                Before = payloadBefore,
                After = new
                {
                    entity.IsActive, entity.Cadence, entity.ExpectedLagDays,
                    entity.ActivatedAtUtc, entity.DeactivatedAtUtc
                }
            },
            ct).ConfigureAwait(false);

        return Results.Ok(ToDto(entity));
    }

    private static bool IsKnownSourceType(string sourceType) =>
        sourceType is "settlement" or "scada" or "operlog";

    private static DataSourceExpectationDto ToDto(DataSourceExpectation e) => new(
        e.PlantId, e.SourceType, e.Cadence, e.ExpectedLagDays,
        e.IsActive, e.ActivatedAtUtc, e.DeactivatedAtUtc);
}

public sealed record DataSourceExpectationDto(
    string PlantId,
    string SourceType,
    string Cadence,
    int ExpectedLagDays,
    bool IsActive,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc);

public sealed record UpsertDataSourceExpectationRequest(
    bool IsActive,
    int ExpectedLagDays,
    string? Cadence);
