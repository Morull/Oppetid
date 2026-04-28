using KraftverkUptime.Api.Authorization;
using KraftverkUptime.Api.Endpoints;
using KraftverkUptime.Api.Errors;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Api.Paging;
using KraftverkUptime.Api.Versioning;
using KraftverkUptime.Core.Modules;
using KraftverkUptime.Infrastructure;
using KraftverkUptime.Infrastructure.KeyVault;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Telemetry;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- Konfigurasjon: miljø + Key Vault (når KEYVAULT_URL er satt) ---
builder.Configuration.AddEnvironmentVariables(prefix: "KRAFTVERK_");
builder.Configuration.AddKraftverkKeyVault(builder.Configuration);

// --- Observability ---
builder.Services.AddKraftverkTelemetry(builder.Configuration);

// --- Infrastructure + moduler ---
builder.Services.AddKraftverkInfrastructure(builder.Configuration);
builder.Services.AddPlatformModules(
    new KraftverkUptime.Modules.Settlement.SettlementModule(),
    new KraftverkUptime.Modules.Classification.ClassificationModule(),
    new KraftverkUptime.Modules.Reporting.ReportingModule(),
    new KraftverkUptime.Modules.Annotations.AnnotationsModule(),
    new KraftverkUptime.Modules.Scada.ScadaModule());

// --- Autentisering + autorisasjon ---
// V1: ingen autentisering koblet til (SystemUserContext). V2: builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)...
builder.Services.AddAuthentication().AddJwtBearer("Bearer", o =>
{
    // Placeholder. I v2: o.Authority = Entra ID issuer, o.Audience = API client-id.
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddKraftverkAuthorization();

// --- API-versjonering, pagination, problem details ---
builder.Services.AddKraftverkVersioning();
builder.Services.AddKraftverkPagination(builder.Configuration);
builder.Services.AddKraftverkProblemDetails();

// JSON-serialisering: enums som navngitte strenger (ellers blir UnitState.InService
// returnert som 0 og klienten klarer ikke å deserialisere til string).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// --- Settlements-opplasting ---
builder.Services.AddOptions<SettlementUploadOptions>()
    .Bind(builder.Configuration.GetSection(SettlementUploadOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SettlementUploadHandler>();

// V1: ChannelsJobQueue er in-proc. Worker-containeren har sin egen tomme kø
// og plukker aldri jobber lagt på av API. Kjør derfor JobLoop også i API-prosessen
// slik at settlement-jobber behandles. TODO(V2): fjern når distribuert kø er på plass
// (Azure Service Bus / Storage Queues) og Worker kan leve som separat prosess.
builder.Services.AddKraftverkJobLoop();

// --- OpenAPI (innebygd i .NET 10; Swashbuckle 7.2.0 er inkompatibel) ---
builder.Services.AddOpenApi();

// --- Helse ---
var connStr = builder.Configuration.GetSection(KraftverkUptime.Infrastructure.Options.DatabaseOptions.SectionName)["ConnectionString"]
    ?? throw new InvalidOperationException("Database:ConnectionString er påkrevd.");
builder.Services.AddHealthChecks()
    .AddNpgSql(connStr, name: "postgres", tags: new[] { "ready" }, failureStatus: HealthStatus.Unhealthy);

// --- Rate limiting (enkel default) ---
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// --- CORS (for Blazor WASM) ---
builder.Services.AddCors(o =>
{
    o.AddPolicy("kraftverkuptime-web", p => p
        .SetIsOriginAllowed(_ => builder.Environment.IsDevelopment())
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// --- Kjør migreringer i dev ---
if (app.Environment.IsDevelopment())
{
    await DatabaseBootstrapper.ApplyMigrationsAsync(app.Services);
}

// --- Middleware-rekkefølge ---
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // OpenAPI JSON serveres på /openapi/v1.json
    app.MapOpenApi();
}

app.UseCors("kraftverkuptime-web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// --- Endpoints ---
var apiV1 = app.NewApiVersionSet()
    .HasApiVersion(new Asp.Versioning.ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

app.MapKraftverkHealth();
app.MapPlantsV1(apiV1);
app.MapSettlementsV1(apiV1);
app.MapAnnotationsV1(apiV1);
app.MapScadaV1(apiV1);

app.MapGet("/", () => Results.Redirect("/openapi/v1.json"));

await app.RunAsync();

public partial class Program; // For WebApplicationFactory i tester
