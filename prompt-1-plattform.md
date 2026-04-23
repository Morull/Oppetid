# Prompt 1 – Plattform og arkitektur for KraftverkUptime

**Bruk:** Dette er første av to prompter. Denne etablerer plattformen, kontraktene og sømmene. Domenelogikken (settlement-parsing, KPI-er, klassifisering) kommer i Prompt 2 etter at skjelettet er på plass.

---

## Systemprompt (rolle)

Du er en senior softwarearkitekt med spisskompetanse innen:
- .NET 8 og ASP.NET Core, Domain-Driven Design, hexagonal/clean architecture.
- Azure-plattformen: App Service, Functions, Container Apps, AKS, Key Vault, Managed Identity, Entra ID, API Management, Event Hubs, Service Bus, Blob Storage, PostgreSQL Flexible Server, Azure Data Explorer, Application Insights.
- Modulær monorepo-arkitektur med plugin-basert utvidelse og strenge modulgrenser.
- Sikkerhet: Zero Trust, OWASP ASVS, secret management, RBAC, audit logging.

Du svarer alltid med konkret, kjørbar kode og begrunnede arkitekturvalg. Du flagger fallgruver eksplisitt.

---

## Oppdrag

Bygg plattformen for en applikasjon kalt `KraftverkUptime` som senere skal gjennomføre oppetidsanalyse for vannkraftverk (første kunde: Dalane-Kraft, første anlegg: Drivdal magasinkraftverk). I denne prompten bygger du **ikke** domenelogikken – du bygger skjelettet med alle kontrakter, abstraksjoner og sømmer slik at domenemoduler kan plugges inn etterpå.

Målet er at domeneprompten som følger kun skal lage domenekoden, uten å måtte røre plattformkoden.

---

## Låste teknologivalg

Disse er bestemt og skal ikke reforhandles:

- **Språk/runtime:** .NET 8 + ASP.NET Core Minimal API.
- **Frontend:** Blazor WebAssembly (én .NET-stack, enklere deling av modeller).
- **Database, transaksjonell/konfig:** PostgreSQL Flexible Server.
- **Database, tidsserie:** PostgreSQL i v1 (ADX forberedes, ikke implementeres).
- **Message bus intern:** MediatR for in-proc events.
- **Message bus ekstern:** Event Hubs (forberedes via `IEventPublisher`-abstraksjon, ikke tilkoblet i v1).
- **Identitet:** Entra ID.
- **Runtime-vert:** Azure Container Apps.
- **Secret management:** Azure Key Vault + Managed Identity.
- **Observability:** OpenTelemetry → Application Insights.
- **IaC:** Bicep.
- **CI/CD:** GitHub Actions med OIDC federated credentials.

---

## Hovedprinsipper

### 1. Modul-nivå utskiftbarhet

Hver modul gjør én ting, enkelt, og skal kunne erstattes senere uten å endre andre moduler. Syv regler som må følges:

1. Stabil offentlig kontrakt – kallsteder ser kun interface, aldri konkret klasse.
2. Ingen delt mutabel tilstand mellom moduler.
3. Hver modul eier sin egen persistens (eget skjema eller tabell-prefiks).
4. Domain events er versjonert (`schemaVersion`).
5. DI-basert registrering – oppgradering er én linjes endring i composition root.
6. Feature flags for gradvis utrulling.
7. Hver modul er selvstendig testbar – tester avhenger kun av kjernens kontrakter.

**Valideringstest:** en PR som oppgraderer én modul skal bare berøre den modulens mappe pluss én linje i `Program.cs`. Hvis flere modulmapper må endres, har arkitekturen feilet prinsippet.

### 2. Additiv, ikke substitutiv

Alle utsatte valg skal kunne legges til *ved siden av* eksisterende kode, ikke erstatte den. Eksempler:
- MediatR in-proc events i v1 → Event Hub legges *i tillegg* i v2, MediatR-koden består.
- PostgreSQL for tidsserie → ADX legges *i tillegg* senere, PostgreSQL beholder transaksjonell data.

Hvis et valg ikke tilfredsstiller det additive prinsippet, skal det låses nå.

### 3. Data­kvalitet som førsteklasses borger

Manglende data skal aldri stille imputeres eller forveksles med nedetid. `DataQualityState` er en førsteklasses type, ikke et flagg.

---

## Kjernekontrakter som skal defineres (i `KraftverkUptime.Core`)

Disse interfaces og typene implementeres i Core-prosjektet. Konkrete implementasjoner av domenemoduler følger senere.

### Datakilder
```csharp
public interface IDataSource { string Name { get; } string Version { get; } }
public interface ISettlementDataSource : IDataSource { /* spesifiseres i Prompt 2 */ }
public interface IScadaDataSource : IDataSource { /* stub */ }
public interface IHydrologicalDataSource : IDataSource { /* stub */ }
public interface ICmmsDataSource : IDataSource { /* stub */ }
```

### Analyse
```csharp
public interface IAnalyzer<TInput, TOutput> { Task<TOutput> AnalyzeAsync(TInput input, CancellationToken ct); }
```

### Rapportering
```csharp
public interface IReportBuilder<TReport> { Task<TReport> BuildAsync(...); }
public interface IReportRenderer { string Format { get; } Task<Stream> RenderAsync(object report); }
public interface IReportSink { Task PublishAsync(string reportId, Stream content, string format); }
```

### Domenemodell – kjernetyper
```csharp
public record AssetEvent(
    string AssetId, DateTimeOffset TimestampUtc,
    string Source, string Measurement, object? Value,
    DataQualityState Quality, string? Unit,
    IReadOnlyDictionary<string, string> Tags,
    int SchemaVersion);

public record ClassifiedPeriod(
    string AssetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    UnitState State, string CauseCode,
    double Confidence, IReadOnlyList<string> Sources,
    DataQualityState Quality);

public enum DataQualityState {
    Good, Uncertain, Substituted,
    InformationUnavailable, Quarantined, Rejected
}

public enum UnitState {
    InService, ReserveShutdown, PlannedOutage, MaintenanceOutage,
    ForcedOutage, ForcedDerating, PlannedDerating,
    ResourceUnavailable, InformationUnavailable
}

public enum PlantType { Regulated, RunOfRiver, Mixed, Pumped }
```

### Sikkerhet og brukerkontekst
```csharp
public interface ICurrentUser {
    string UserId { get; }
    string OrgId { get; }
    IReadOnlySet<string> Roles { get; }
    IReadOnlySet<string> AccessiblePlantIds { get; } // tom = alle
}

public interface IQueryContext {
    IQueryable<T> Apply<T>(IQueryable<T> source) where T : IOwnedEntity;
}

public interface IOwnedEntity { string OwnerOrgId { get; } string? PlantId { get; } }

public interface IAuditLogger {
    Task LogAsync(string action, string entityType, string entityId, object? payload);
}
```

### Retrofit-sømmer (implementeres nå, enkle default)
```csharp
public interface IJobQueue {
    Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : notnull;
}

public interface IFileStorage {
    Task<string> PutAsync(string path, Stream content, CancellationToken ct = default);
    Task<Stream> GetAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);
}

public interface IPlantConfiguration {
    T? Get<T>(string plantId, string key);
    Task SetAsync<T>(string plantId, string key, T value);
}

public interface INotificationService {
    Task SendAsync(string topic, object payload, IEnumerable<string>? recipients = null);
}

public interface IEventPublisher {
    Task PublishAsync<T>(T domainEvent) where T : IDomainEvent;
}

public interface IDomainEvent {
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    string? CorrelationId { get; }
    int SchemaVersion { get; }
}
```

---

## Sømmer og standardimplementasjoner i v1

Hver retrofit-søm skal ha en enkel, kjørende default-implementasjon i v1:

| Kontrakt | v1-implementasjon | v2-utvidelse |
|---|---|---|
| `IJobQueue` | In-process basert på `System.Threading.Channels` | Azure Service Bus / Storage Queues |
| `IFileStorage` | Lokal filsystem i dev, Azure Blob i prod | Samme interface, begge miljøer |
| `IDistributedCache` | Microsofts `AddDistributedMemoryCache()` | Azure Redis Cache |
| `IPlantConfiguration` | PostgreSQL-tabell `plant_configuration` + cache | Admin-UI |
| `INotificationService` | Skriver til structured log | Azure Communication Services, Teams, webhook |
| `IEventPublisher` | MediatR in-proc | Event Hub fan-out i tillegg |
| `ICurrentUser` | `SystemUserContext` (alltid admin) | `EntraIdUserContext` (leser token) |
| `IAuditLogger` | Skriver til `audit_log`-tabell | Immutable storage / Log Analytics |
| `IQueryContext` | No-op filter | Beriket med `OwnerOrgId` fra `ICurrentUser` |

---

## Tverrgående krav

### API-design
- Alle endepunkter under `/api/v1/...`. Versjonsstyring via `Asp.Versioning.Http`.
- Standard response-envelope for list-endepunkter: `{ items: [...], nextCursor: "...", pageSize: N }`.
- Maks side-størrelse håndhevet i middleware.
- Policy-basert autorisasjon på alle endepunkter fra dag én, selv om policy i v1 returnerer "allow":
  - `PlantReader`, `PlantAnalyst`, `PlantAdmin`, `OrgAdmin`, `SystemAdmin`.

### Databasemigreringer
- EF Core Migrations fra dag én.
- Migreringer kjører ved oppstart i dev, som eget steg i CI/CD for prod.
- Ingen `ALTER TABLE` utenfor migrering.

### Multi-tenant skjema
- Alle domeneentiteter implementerer `IOwnedEntity` med `OwnerOrgId` og `PlantId`.
- Database indekserer på `OwnerOrgId` og `PlantId`.
- Global query filter i `DbContext` bruker `IQueryContext`.

### Soft delete
- Alle domeneentiteter har `DeletedAt` og `DeletedBy`.
- Global query filter ekskluderer slettede.
- Retensjonspolicy i konfig per datakategori.

### Konfigurasjon
- Sterkt typede `IOptions<T>` med `DataAnnotations`-validering.
- `ValidateOnStart()` på alle konfig-seksjoner.
- `Configuration["key"]` forbudt i modulkode.

### Observability
- OpenTelemetry-initialisering med `ownerOrgId`, `plantId`, `userId`, `correlationId` som baggage.
- Structured logging (JSON) med korrelasjon-ID gjennom hele forespørselskjeden.
- Helse- og readiness-endepunkter: `/health/live`, `/health/ready`.
- Metrikker: request-latency, job-eksekveringstid, feilrater per datakilde.

### Sikkerhet
- Managed Identity for all Azure-tilgang.
- Hemmeligheter kun via Key Vault, hentet med `DefaultAzureCredential`.
- Key Vault-oppslag cachet (throttling-beskyttelse).
- TLS 1.2+ overalt, CMK via Key Vault for data-at-rest.
- Secret scanning (Gitleaks) og SBOM (CycloneDX) i pipeline.

---

## Mappestruktur (anbefalt)

```
KraftverkUptime/
├── src/
│   ├── KraftverkUptime.Core/             # Kontrakter, domenetyper, abstraksjoner
│   ├── KraftverkUptime.Infrastructure/   # EF Core, Blob Storage, Key Vault, OTel
│   ├── KraftverkUptime.Api/              # ASP.NET Core Minimal API
│   ├── KraftverkUptime.Web/              # Blazor WASM frontend
│   ├── KraftverkUptime.Worker/           # Bakgrunnsjobber / IJobQueue-host
│   └── KraftverkUptime.Modules.<Name>/   # Én mappe per domenemodul
├── tests/
│   ├── KraftverkUptime.Core.Tests/
│   ├── KraftverkUptime.Infrastructure.Tests/
│   └── tests/fixtures/                   # Testdata
├── infra/
│   ├── main.bicep
│   └── modules/
├── .github/workflows/
│   ├── ci.yml
│   └── deploy.yml
├── docker-compose.yml
├── .env.example
└── README.md
```

---

## Leveranseformat – minst mulig arbeid for bruker

- **Komplette filer med full sti øverst.** Ikke snutter eller `// ...`-utelatelser.
- **Én kodeblokk per fil.**
- **`docker compose up` skal starte alt lokalt.** PostgreSQL, app, frontend, mocks.
- **`dotnet test` skal være grønn umiddelbart.**
- **`.env.example` med fungerende dev-verdier.**
- **Azure-deploy én kommando:** `azd up` etter preflight-sjekk.
- **README per prosjekt** med formål, kjør-lokalt, kjør-tester, utvidelsespunkter.
- **Feilsøkings­seksjon** for de 3–5 mest sannsynlige feilene.
- **Eksakte Azure-ressursnavn**, ikke placeholders.

---

## Rekkefølge for leveranse

Lever i denne rekkefølgen. Stopp etter hvert steg for korte bekreftelser, men fortsett med best effort hvis bruker ikke stopper deg:

1. **Arkitekturdiagram** (Mermaid C4 Context + Container, inkludert i README).
2. **Løsningsskjelett** – `dotnet new sln` + alle prosjekter, prosjektreferanser, `.csproj`-filer.
3. **`KraftverkUptime.Core`** – alle kontrakter, domenetyper, enum-er, events som definert over.
4. **`KraftverkUptime.Infrastructure`** – EF Core DbContext, migreringer, alle default-implementasjoner av retrofit-sømmer, OpenTelemetry-oppsett, Key Vault + Managed Identity, `SystemUserContext`.
5. **`KraftverkUptime.Api`** – Minimal API med `/health/live`, `/health/ready`, versjoneringsmiddelware, policy-registrering, paginering, feilhåndtering.
6. **`KraftverkUptime.Web`** – Blazor WASM-shell med `UserContextProvider`, routing, placeholder-sider.
7. **`KraftverkUptime.Worker`** – host for `IJobQueue`-konsumenter.
8. **`docker-compose.yml`** og **`.env.example`**.
9. **Bicep** – minimumsinfrastruktur (RG, Key Vault, Container Apps Env, Postgres, Storage, Log Analytics, Application Insights, Entra ID app registration).
10. **GitHub Actions** – `ci.yml` (build, test, secret scan, SBOM) og `deploy.yml` (OIDC, `azd deploy`).
11. **README** (rot) – arkitektur, kjør-lokalt, kjør-i-Azure, hvordan legge til ny modul (< 30 min).

---

## Akseptansekriterier

- Ny modul kan legges til ved å implementere kontrakter i Core, uten endring i eksisterende kode.
- Ingen hemmeligheter i kode, config eller logger.
- Alle Azure-ressurser bruker Managed Identity.
- Enhetstester med ≥ 80 % dekning på Core.
- `docker compose up` starter alt lokalt.
- `azd up` provisjonerer og deployer til Azure.
- Oppgradering fra `SystemUserContext` til `EntraIdUserContext` er én linjes endring i `Program.cs`.

---

## Fallgruver du skal flagge eksplisitt

- Tidssoner: UTC internt, Europe/Oslo ved I/O. DST-overganger i mars/oktober.
- Key Vault-throttling ved høy-frekvent secret-lookup – cache obligatorisk.
- Managed Identity fungerer ikke lokalt – bruk `DefaultAzureCredential` med Azure CLI-fallback.
- Event Hub partitioning er vanskelig å endre senere – forbered, men ikke velg antall partisjoner i v1.
- Global query filter i EF Core må aktiveres på hver entitetskonfigurasjon, ikke bare én gang.

---

## Start her

Før du begynner å skrive kode, bekreft:
1. Har du forstått at domenelogikken (settlement-parser, KPI-er, klassifisering) *ikke* skal implementeres nå – bare kontraktene?
2. Foreslår du justeringer i kontraktene over? Hvis ja, begrunn.
3. Er det noen av de låste teknologivalgene du mener bør revurderes før du begynner?

Etter bekreftelse starter du på leveranse 1 (arkitekturdiagram).
