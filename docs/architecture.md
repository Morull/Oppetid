# KraftverkUptime – Arkitektur

Sist oppdatert: 2026-04-20. Versjon: v1 plattformskjelett.

## C4 Context

```mermaid
C4Context
    title KraftverkUptime – systemkontekst
    Person(analyst, "Anleggsanalytiker", "Dalane-Kraft driftsingeniør. Kjører oppetidsanalyser og leser rapporter.")
    Person(admin, "Org-admin", "Administrerer brukere, anlegg og konfigurasjon hos kunden.")
    Person(sysadmin, "System-admin", "KraftverkUptime-leverandør. Drift, observability, tenant-oppsett.")

    System(kraftverkuptime, "KraftverkUptime", "Modulær oppetidsanalyse-plattform for vannkraftverk.")

    System_Ext(entra, "Microsoft Entra ID", "Identitetsleverandør. Utsteder tokens.")
    System_Ext(settlement, "Avregningsrapporter", "Settlement-filer (CSV/XLSX) fra kundens avregningssystem. v1: opplasting.")
    System_Ext(scada, "SCADA-historian", "Kraftverkenes tidsseriedata. v2: webhook/OPC-UA.")
    System_Ext(cmms, "CMMS", "Vedlikeholdsordresystem. v2: REST-pull.")
    System_Ext(hydro, "Hydrologi", "Tilsig og magasinnivå. v2: fil/REST.")
    System_Ext(teams, "Teams / e-post", "Varslingsmål via INotificationService. v2: tilkoblet.")

    Rel(analyst, kraftverkuptime, "Utfører analyser, leser rapporter", "HTTPS / Blazor WASM")
    Rel(admin, kraftverkuptime, "Administrerer konfigurasjon", "HTTPS")
    Rel(sysadmin, kraftverkuptime, "Drift og observability", "HTTPS / Azure Portal")

    Rel(kraftverkuptime, entra, "Autentisering (OIDC)", "HTTPS")
    Rel(settlement, kraftverkuptime, "Opplasting av avregningsfil", "HTTPS upload")
    Rel(kraftverkuptime, scada, "Henter tidsserier (v2)", "HTTPS / OPC-UA")
    Rel(kraftverkuptime, cmms, "Henter vedlikeholdsordrer (v2)", "HTTPS")
    Rel(kraftverkuptime, hydro, "Henter hydrologi (v2)", "HTTPS")
    Rel(kraftverkuptime, teams, "Sender varsler (v2)", "HTTPS webhook")
```

## C4 Container

```mermaid
C4Container
    title KraftverkUptime – containere (v1)
    Person(user, "Bruker", "Analytiker eller admin")

    System_Boundary(platform, "KraftverkUptime") {
        Container(web, "Blazor WASM", ".NET 10, Blazor WebAssembly", "Single-page applikasjon. Laster ned fra CDN/static-hosting. Snakker kun med API via /api/v1.")
        Container(api, "Web API", ".NET 10, ASP.NET Core Minimal API", "Autentisering, autorisasjon (policies), kommando- og spørringshåndtering, pagination, ProblemDetails.")
        Container(worker, "Worker", ".NET 10 HostedService", "Konsumerer IJobQueue og dispatcher til IJobHandler-implementasjoner. Kjører analyser og rapportbygging i bakgrunnen.")

        ContainerDb(db, "PostgreSQL 16", "Azure Postgres Flexible Server", "Transaksjonell data, konfig, tidsserier (v1). Per-modul skjema.")
        ContainerDb(blob, "Blob Storage", "Azure Storage / Azurite i dev", "Opplastede filer, genererte rapporter.")
        Container(cache, "Distribuert cache", "In-memory v1 / Azure Redis v2", "Sesjoner, Key Vault-secret-cache, idempotency.")
        Container(kv, "Key Vault", "Azure Key Vault", "Hemmeligheter, sertifikater. Tilgang via Managed Identity.")
        Container(obs, "Observability", "Application Insights + Log Analytics", "Traces, metrics, logs, korrelasjons-ID.")
    }

    System_Ext(entra, "Entra ID")

    Rel(user, web, "Bruker UI", "HTTPS")
    Rel(web, api, "Kaller API", "HTTPS JSON /api/v1")
    Rel(web, entra, "Logger inn", "OIDC redirect")
    Rel(api, entra, "Validerer token", "JWKS")

    Rel(api, db, "Leser/skriver", "EF Core 10")
    Rel(api, cache, "Cacher oppslag", "")
    Rel(api, kv, "Henter secrets", "Managed Identity")
    Rel(api, blob, "Leser/skriver filer", "IFileStorage")
    Rel(api, worker, "Legger i jobbkø", "IJobQueue (Channels v1)")
    Rel(worker, db, "Leser/skriver", "EF Core 10")
    Rel(worker, blob, "Leser/skriver filer", "IFileStorage")
    Rel(worker, cache, "Cacher oppslag", "")

    Rel(api, obs, "Telemetri", "OpenTelemetry OTLP")
    Rel(worker, obs, "Telemetri", "OpenTelemetry OTLP")
    Rel(web, obs, "Browser-telemetri", "App Insights JS SDK")
```

## C4 Component – Api (utsnitt)

```mermaid
C4Component
    title API – interne komponenter
    Container_Boundary(api, "KraftverkUptime.Api") {
        Component(endpoints, "Endpoint groups", "Minimal API", "/api/v1/plants, /api/v1/reports, /health/*.")
        Component(policies, "Autorisasjonspolicies", "PlantReader, PlantAnalyst, PlantAdmin, OrgAdmin, SystemAdmin")
        Component(versioning, "API-versjonering", "Asp.Versioning.Http")
        Component(pagination, "Paginering", "Middleware + envelope { items, nextCursor, pageSize }")
        Component(errors, "Feilhåndtering", "ProblemDetails, exception mapper")
        Component(otel, "Telemetri", "OpenTelemetry middleware, baggage: orgId/plantId/userId/correlationId")

        Component(dispatcher, "In-proc dispatcher", "IEventPublisher + egen kode (~50 linjer)", "Publiserer IDomainEvent til registrerte handlere. Erstatter MediatR.")
    }

    Container_Boundary(core, "KraftverkUptime.Core") {
        Component(contracts, "Kontrakter", "IDataSource, IAnalyzer, IReportBuilder, IJobQueue, IJobHandler, IFileStorage, IEventPublisher, IAuditLogger, ICurrentUser, IQueryContext, IPlantConfiguration, INotificationService")
        Component(domain, "Domenetyper", "AssetEvent, ClassifiedPeriod, DataQualityState, UnitState, PlantType, IOwnedEntity, IDomainEvent")
    }

    Container_Boundary(infra, "KraftverkUptime.Infrastructure") {
        Component(efcore, "EF Core", "DbContext med global query filter via IQueryContext, soft-delete filter")
        Component(seams, "Retrofit-defaults", "ChannelsJobQueue, LocalFileStorage/BlobFileStorage, MemoryPlantConfiguration+DB, MemoryCache, LogNotificationService, AuditLogger")
        Component(identity, "Identitet", "SystemUserContext (v1) / EntraIdUserContext (v2)")
    }

    Container_Boundary(modules, "Moduler (placeholder i v1)") {
        Component(modSettlement, "Settlement-modul", "Implementerer ISettlementDataSource i Prompt 2")
        Component(modClass, "Klassifisering", "IAnalyzer<Events, ClassifiedPeriods>")
        Component(modReport, "Rapport", "IReportBuilder, IReportRenderer")
    }

    Rel(endpoints, policies, "Beskyttes av")
    Rel(endpoints, versioning, "Rutes via")
    Rel(endpoints, pagination, "Responderer via")
    Rel(endpoints, errors, "Feil oversettes i")
    Rel(endpoints, otel, "Instrumenteres av")

    Rel(endpoints, contracts, "Kaller kontrakter fra")
    Rel(endpoints, modules, "Eksekverer via DI")
    Rel(modules, contracts, "Implementerer")
    Rel(modules, domain, "Bruker")
    Rel(efcore, contracts, "Implementerer IQueryContext og repositories via")
    Rel(seams, contracts, "Implementerer")
    Rel(identity, contracts, "Implementerer ICurrentUser")
    Rel(dispatcher, contracts, "Implementerer IEventPublisher")
```

## Dataflyt – analysejobb (v1)

```mermaid
sequenceDiagram
    autonumber
    actor U as Analytiker
    participant W as Blazor WASM
    participant A as API
    participant Q as IJobQueue (Channels)
    participant K as Worker
    participant D as Postgres
    participant B as Blob

    U->>W: Last opp settlement-fil
    W->>A: POST /api/v1/settlements (multipart)
    A->>B: PutAsync(raw/{runId}.xlsx)
    A->>D: INSERT AnalysisRun (status=Queued)
    A->>Q: EnqueueAsync(new AnalyzeRunJob(runId))
    A-->>W: 202 Accepted { runId }

    K->>Q: ReadAsync
    K->>B: GetAsync(raw/{runId}.xlsx)
    K->>K: IAnalyzer.AnalyzeAsync (Prompt 2)
    K->>D: INSERT ClassifiedPeriod rows
    K->>D: UPDATE AnalysisRun (status=Completed)
    K->>B: PutAsync(reports/{runId}.xlsx)

    U->>W: Hent rapport
    W->>A: GET /api/v1/reports/{runId}
    A->>B: GetAsync(reports/{runId}.xlsx)
    A-->>W: 200 OK (stream)
```

## Modulgrensediagram

```mermaid
flowchart LR
    subgraph Core[KraftverkUptime.Core]
        direction TB
        contracts[Kontrakter]
        domain[Domenetyper]
        events[IDomainEvent-ark]
    end

    subgraph Infra[KraftverkUptime.Infrastructure]
        direction TB
        efcore[EF Core DbContext]
        seams[Defaults: Channels, LocalFile/Blob, Memory, LogNotif, AuditLog]
        dispatch[InProcEventPublisher]
        identity[SystemUserContext]
    end

    subgraph Api[KraftverkUptime.Api]
        direction TB
        endpoints[Minimal API endpoints]
        policies[Policies]
        health[/health/live /health/ready]
    end

    subgraph Worker[KraftverkUptime.Worker]
        jobloop[JobLoopService]
    end

    subgraph Web[KraftverkUptime.Web]
        blazor[Blazor WASM shell]
    end

    subgraph Modules[Moduler Prompt 2]
        direction TB
        mSettlement[Settlement]
        mClass[Klassifisering]
        mReport[Rapport]
    end

    Api --> Core
    Infra --> Core
    Worker --> Core
    Web --> Core
    Modules --> Core
    Api --> Infra
    Worker --> Infra
    Api --> Modules
    Worker --> Modules
    Web --> Api
```

## Legende for additive valg

```mermaid
flowchart TB
    subgraph v1[v1 – levert nå]
        direction LR
        a1[Channels IJobQueue]
        a2[Local/Blob IFileStorage]
        a3[Memory IDistributedCache]
        a4[In-proc IEventPublisher]
        a5[SystemUserContext]
        a6[Postgres alle data]
    end

    subgraph v2[v2 – additivt senere]
        direction LR
        b1[Service Bus IJobQueue]
        b2[Azure Blob kun]
        b3[Azure Redis]
        b4[Event Hub fan-out]
        b5[EntraIdUserContext]
        b6[ADX for tidsserie]
    end

    a1 -. tillegg .-> b1
    a2 -. samme interface .-> b2
    a3 -. tillegg .-> b3
    a4 -. tillegg .-> b4
    a5 -. én linje i Program.cs .-> b5
    a6 -. tillegg, Postgres beholder transaksjonell .-> b6
```

## Fallgruver som er bygget inn i plattformen

1. **UTC internt, Europe/Oslo ved I/O.** `DateTimeOffset` overalt, konverteres kun i rendering-laget. DST-sjekker i tester (mars/oktober).
2. **Key Vault-throttling.** `IConfiguration`-provider cacher secrets i 15 min; `DefaultAzureCredential` med CLI-fallback for lokal dev.
3. **Managed Identity fungerer ikke lokalt.** Lokalt brukes brukerens Azure CLI-pålogging gjennom `DefaultAzureCredential`; ingen connection strings med passord i repo.
4. **Global query filter i EF Core må aktiveres per entitet.** Gjøres i `ModelBuilderExtensions.ApplyOwnedEntityFilters`, som kalles i `OnModelCreating`. Nye entiteter som arver `OwnedEntity` får filter automatisk.
5. **Event Hub partisjonering er vanskelig å endre.** Ingen partisjonering valgt i v1. `IEventPublisher`-fasaden skjuler dette; partisjonsvalg tas når Event Hub kobles til.
6. **`AssetEvent.Value` som `object?` er EF-uvennlig.** Domene-record-en forblir som spesifisert, men lagres i Postgres som to kolonner (`value_numeric double precision null`, `value_json jsonb null`). Mapping skjer i Infrastructure.
7. **Soft-delete + global filter + `Include()`.** Include respekterer ikke alltid filter – bruk `IgnoreQueryFilters()` eksplisitt ved behov, aldri implisitt.
