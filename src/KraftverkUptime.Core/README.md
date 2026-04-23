# KraftverkUptime.Core

Kontraktsprosjektet for hele plattformen. Inneholder kun `interface`-er, `enum`-er og `record`-er – ingen konkret kode. Alle konkrete implementasjoner lever i `KraftverkUptime.Infrastructure` eller i moduler under `KraftverkUptime.Modules.*`.

## Formål

Modulene i plattformen refererer kun Core. Bytte av en konkret implementasjon (f.eks. `ChannelsJobQueue` → `ServiceBusJobQueue`) krever kun én linjes endring i composition root.

## Struktur

    DataSources/       IDataSource + stubs per kildetype (Settlement, Scada, Hydro, Cmms)
    Analysis/          IAnalyzer<TInput, TOutput>
    Reporting/         ReportRequest, IReportBuilder, IReportRenderer, IReportSink
    Domain/            AssetEvent, ClassifiedPeriod, DataQualityState, UnitState,
                       PlantType, IOwnedEntity, ISoftDeletable
    Security/          ICurrentUser, IQueryContext, IAuditLogger, AuthorizationPolicies
    Jobs/              IJobQueue, IJobHandler<TJob>
    Storage/           IFileStorage
    Configuration/     IPlantConfiguration
    Notifications/     INotificationService
    Events/            IDomainEvent, IEventHandler<T>, IEventPublisher
    Modularity/        IPlatformModule

## Låste designvalg

1. **Datakvalitet som førsteklasses-type.** `DataQualityState` er enum – ikke en bool-flagg. `InformationUnavailable` er aldri det samme som nedetid.
2. **Én fasade for events.** Moduler refererer kun `IEventPublisher`. In-proc dispatcher i Infrastructure ruter til `IEventHandler<T>`; V2 legger Event Hub-fan-out på toppen uten modulendringer.
3. **Eksplisitt tenant-tilgang.** `ICurrentUser.HasAllPlantsAccess` er et eget flagg; tom `AccessiblePlantIds` betyr "ingen", ikke "alle" (justering f).
4. **Alt async, med CancellationToken.** `IPlantConfiguration.GetAsync` (d), `IAuditLogger.LogAsync` (e), `IFileStorage.*` – ingen synkrone DB/IO-kall.
5. **Strukturerte rapport-forespørsler.** `ReportRequest`-record fremfor åpne parametere (justering a).

## Utvidelsespunkter

* **Ny datakilde** – arv fra `IDataSource`, plasser i egen modul-mappe, registrer via `IPlatformModule`.
* **Ny analyse** – implementer `IAnalyzer<TInput, TOutput>` med egne DTO-er i modulprosjektet. Core skal ikke vite om konkrete analyse-typer.
* **Ny rapportrenderer** – implementer `IReportRenderer` med unikt `Format`-navn. Composition root velger renderer ut fra forespurt format.

## Ikke-mål

* Ingen referanser til EF Core, ASP.NET Core eller Azure-SDK-er. Core skal kunne konsumeres fra et konsollprogram eller et bakgrunnsbibliotek.
* Ingen konkrete implementasjoner – `NoopQueryContext`, `SystemUserContext`, `ChannelsJobQueue` osv. lever i Infrastructure.
