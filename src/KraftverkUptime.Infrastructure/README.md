# KraftverkUptime.Infrastructure

Default-implementasjoner av alle retrofit-sømmer i Core, pluss EF Core DbContext og telemetri.

## Seams og default-implementasjoner

| Core-kontrakt | V1-implementasjon |
| --- | --- |
| `IJobQueue` + `IJobHandler<T>` | `ChannelsJobQueue` + `JobLoopHostedService` (System.Threading.Channels) |
| `IFileStorage` | `LocalFileStorage` (dev) / `BlobFileStorage` (Azurite i dev, Azure Blob i prod) |
| `IPlantConfiguration` | `DbPlantConfiguration` (Postgres + IMemoryCache) |
| `INotificationService` | `LogNotificationService` (structured log) |
| `IEventPublisher` + `IEventHandler<T>` | `InProcEventPublisher` (egen ~50-linjers dispatcher) |
| `ICurrentUser` | `SystemUserContext` (alltid admin; erstattes av `EntraIdUserContext` i v2) |
| `IQueryContext` | `NoopQueryContext` (no-op i v1; filtrering per OwnerOrgId/AccessiblePlantIds i v2) |
| `IAuditLogger` | `DbAuditLogger` (skriver til `core.audit_log`) |

## Migreringer

Prosjektet bruker EF Core Migrations fra dag én. Ved første checkout finnes ingen migreringer – `DatabaseBootstrapper.ApplyMigrationsAsync` faller tilbake til `EnsureCreatedAsync` og logger en advarsel. **Før første commit**: generer initial migrering:

```sh
dotnet ef migrations add Initial \
    --project src/KraftverkUptime.Infrastructure \
    --startup-project src/KraftverkUptime.Api \
    --output-dir Persistence/Migrations
```

`DesignTimeDbContextFactory` leser `KRAFTVERK_DESIGN_CONNSTR`; sett denne eller bruk default (localhost Postgres).

## Observability

`TelemetryExtensions.AddKraftverkTelemetry` registrerer:
- Traces for AspNetCore + HttpClient + egen kilde "KraftverkUptime"
- Metrics for AspNetCore + HttpClient + runtime + egen meter "KraftverkUptime"
- Logs eksportert via OpenTelemetry

Eksporterer til Application Insights (når connection string er satt), OTLP-endpoint, og/eller console.

## Nøkkeloppslag

`KeyVaultConfigurationExtensions.AddKraftverkKeyVault` legger Key Vault som config-provider når `KEYVAULT_URL` er satt. `DefaultAzureCredential` faller tilbake til Azure CLI-pålogging lokalt (Managed Identity fungerer ikke i dev). Secrets caches 15 min for å unngå throttling.

## Utvidelse

Ny seam-implementasjon: lag klasse som implementerer Core-kontrakten, registrer den i `InfrastructureServiceCollectionExtensions` eller overstyr etter `AddKraftverkInfrastructure` i composition root.
