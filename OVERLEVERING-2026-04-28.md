# Overlevering — Annoterings-feature (Steg B–E)

Dato: 2026-04-28
Forrige sesjon: 2026-04-26 (Phase A — klassifikator-forenkling, deployt)

## Hva ble gjort

Implementert hele annoterings-stacken slik den er beskrevet i forrige overlevering:
backend (B), read-time overlay (C), tidslinje-graf (D), og dialog (E).

### Implementasjonsvalg som ble lukket denne sesjonen

| Valg | Beslutning | Begrunnelse |
|---|---|---|
| Migrasjon | EF migrasjon | Ren historikk; tooling lagt inn i `.config/dotnet-tools.json`. |
| Modul-plassering | Nytt `KraftverkUptime.Modules.Annotations` | Renere skille fra Classification. Lettere å versjonere senere. |
| Overlay-strategi | Read-time merge | Worker-kontrakten uendret; rapporten merges når den hentes. |

### Nye filer

```
src/KraftverkUptime.Modules.Annotations/
  KraftverkUptime.Modules.Annotations.csproj
  AnnotationsModule.cs
  Repositories/
    IDowntimeAnnotationRepository.cs
    IDowntimeCategoryRepository.cs
  Overlay/
    AnnotationOverlayService.cs

src/KraftverkUptime.Core/Domain/
  DowntimeAnnotation.cs                                (record)
  DowntimeCategory.cs                                  (record, med UnitStateOverride)

src/KraftverkUptime.Infrastructure/Persistence/Entities/
  DowntimeAnnotationEntry.cs
  DowntimeCategoryEntry.cs
src/KraftverkUptime.Infrastructure/Persistence/Repositories/
  EfDowntimeAnnotationRepository.cs
  EfDowntimeCategoryRepository.cs
src/KraftverkUptime.Infrastructure/Persistence/
  DowntimeCategorySeeder.cs                             (idempotent seed for de 7 system-kategoriene)

src/KraftverkUptime.Api/
  Contracts/AnnotationContracts.cs
  Endpoints/AnnotationsEndpoints.cs

src/KraftverkUptime.Web/
  Services/AnnotationsApi.cs
  Components/DriftTimeline.razor
  Components/AnnotationDialog.razor

.config/dotnet-tools.json                               (dotnet-ef 10.0.4)
Generer migrasjoner.bat                                 (helper for migrasjons-generering)
```

### Endrede filer

```
KraftverkUptime.sln                                     (la til Modules.Annotations)
src/KraftverkUptime.Infrastructure/KraftverkUptime.Infrastructure.csproj  (ref Modules.Annotations)
src/KraftverkUptime.Infrastructure/InfrastructureServiceCollectionExtensions.cs  (DI for repositories)
src/KraftverkUptime.Infrastructure/Persistence/KraftverkDbContext.cs       (DbSets + entity config)
src/KraftverkUptime.Infrastructure/Persistence/DatabaseBootstrapper.cs     (kall seeder)
src/KraftverkUptime.Api/KraftverkUptime.Api.csproj                          (ref Modules.Annotations)
src/KraftverkUptime.Api/Program.cs                                          (registrer modul + map endpoints)
src/KraftverkUptime.Api/Endpoints/SettlementsEndpoints.cs                   (overlay i Get(Xlsx)Report)
src/KraftverkUptime.Worker/KraftverkUptime.Worker.csproj                    (ref Modules.Annotations)
src/KraftverkUptime.Worker/Program.cs                                       (registrer modul)
src/KraftverkUptime.Web/Program.cs                                          (DI for AnnotationsApi)
src/KraftverkUptime.Web/Pages/ReportDetail.razor                            (DriftTimeline + dialog)
src/KraftverkUptime.Web/wwwroot/css/app.css                                 (timeline heatmap-stiler)
```

### Datamodell

`core.downtime_categories` (global lookup, IS_SYSTEM=true for de 7 default)
- `id` (PK, slug som "scheduled_service")
- `display_name`, `color_hex`, `unit_state_override` (string-konvertert UnitState)
- `sort_order`, `is_active`, `is_system`

`core.downtime_annotations` (multi-tenant, soft-delete)
- `id` (BIGINT identity), `owner_org_id`, `plant_id`
- `start_utc`, `end_utc` (eksklusiv), `category_id` (FK Restrict)
- `comment` (≤2000 chars), `created_at/by`, `updated_at/by`, `deleted_at/by`
- Index: `(plant_id, start_utc, end_utc) WHERE deleted_at IS NULL`

### API-overflate (alle på `/api/v{version:apiVersion}`)

| Metode | Path | Body | Respons |
|---|---|---|---|
| GET | `/plants/{plantId}/annotations?from=&to=` | – | `200 List<AnnotationDto>` |
| POST | `/plants/{plantId}/annotations` | `CreateAnnotationRequest` | `201 AnnotationDto` / `409 OverlapConflict` |
| PATCH | `/plants/{plantId}/annotations/{id}` | `UpdateAnnotationRequest` | `200 AnnotationDto` / `404` / `409` |
| DELETE | `/plants/{plantId}/annotations/{id}` | – | `204` / `404` |
| GET | `/annotations/categories` | – | `200 List<CategoryDto>` |

Auth-modellen er `AllowAnonymous` i v1 (matcher Settlements). Bytt til
`PlantReader`/`PlantAnalyst` når Entra ID kobles til.

### Overlay-mekanikk (Steg C)

1. Klient kaller `GET /settlements/{key}/report` (eller `/xlsx`).
2. Endpointet laster base-rapport fra blob.
3. `AnnotationOverlayService.ApplyAsync` henter aktive annoteringer og kategorier, og for hver
   `ClassifiedHourlyRow` som dekkes av en annotering: overstyrer `State`, `CauseCode`,
   `Confidence` (= 1.0) og `Rationale`.
4. `UptimeKpiCalculator.Compute` rekjøres på det justerte settet — alle KPI-er reflekterer overlayet.
5. Hvis ingen annoteringer dekker perioden returneres rapporten uendret (referanselikhet).

Worker-kontrakten er uendret — base-rapporten i blob er fortsatt rådata fra klassifikatoren.

### UI (Steg D + E)

`DriftTimeline.razor` rendrer en CSS-grid heatmap på `ReportDetail`:
- Én rad per dag, 24 ruter per rad
- Farge per UnitState (eller kategori-farge for annoterte timer)
- Klikk på time → `AnnotationDialog`

`AnnotationDialog.razor` håndterer både opprett, rediger og slett:
- Time-presisjon via separate `MudDatePicker` + `MudSelect` (timer 0–24)
- Kategori-velger med fargeprikker
- Overlapp-håndtering: når API returnerer 409 vises eksisterende rader, bruker bekrefter med
  «Lagre og erstatt» som soft-deleter de overlappende.

## Verifisering — gjør dette først når du er tilbake

### 1. Generer migrasjon

Phase A bygde fortsatt på EnsureCreated-fallbacket i `DatabaseBootstrapper`. Denne sesjonen har lagt
nye tabeller — disse blir ikke automatisk lagt til på en eksisterende dev-DB. Anbefalt rekkefølge:

```powershell
# 1. Reset dev-DB (mister importerte avregninger; OK i dev)
docker compose down -v

# 2. Restorer dotnet-ef + generer Initial + AddAnnotations
.\"Generer migrasjoner.bat"

# 3. Bygg og start (DatabaseBootstrapper kjører Migrate på oppstart)
.\"Tving Web rebuild.bat"

# 4. Re-importer Drivdal-feb-2025 for å få ny rapport (jobben kjører klassifisering på import)
#    /upload → velg fil → /reports → ReportDetail
```

Hvis du ikke vil reset-e DB-en:

```powershell
# Bare lag AddAnnotations som migrasjon (ikke Initial). EnsureCreated fallbacket har allerede
# laget de gamle tabellene; men da må du legge en idempotent CREATE TABLE for de nye selv.
# Enklere: gjør reset-en ovenfor.
```

### 2. Forventet build-utfall

`dotnet build` skal gå grønt. Endepunkter som `/api/v1/annotations/categories` og
`/api/v1/plants/{plantId}/annotations` skal vises i OpenAPI på `/openapi/v1.json`.

### 3. Manuell røyktest

1. Åpne en eksisterende rapport.
2. Driftstidslinjen skal vises som heatmap nederst — én rad per dag, 24 ruter per rad.
3. Klikk på en hvilken som helst time → dialog åpnes med valg av kategori.
4. Lagre → snackbar "Annotering opprettet", tidslinjen oppdateres med kategori-fargen,
   og KPI-er øverst på siden reflekterer overlay (f.eks. flere `MaintenanceOutage`-timer).
5. Klikk på samme time igjen → dialogen åpner med eksisterende verdier; "Slett" fjerner.
6. Lag en overlappende annotering uten å bruke replaceIds → 409 vises i dialogen som varsel
   med liste over overlappende rader; "Lagre og erstatt" gjør jobben.

### 4. Tester

`dotnet test` skal være grønt. Ingen eksisterende tester ble endret. (Eventuelle nye unit-tester
for `AnnotationOverlayService` og repositoriene kan legges til i neste iterasjon.)

## Status pr. 2026-04-28

| Punkt | Status |
|---|---|
| Phase A — klassifikator + KPI (forrige sesjon) | ✅ Live |
| Annoteringer — domain types | ✅ Skrevet |
| Annoteringer — DB-migrasjon | ⚠️ Ikke generert (kjør `Generer migrasjoner.bat`) |
| Annoteringer — API-endpoints | ✅ Skrevet |
| Annoteringer — overlay i KPI-kalkulator | ✅ Skrevet |
| Annoteringer — driftstidslinje-graf | ✅ Skrevet |
| Annoteringer — markerings-dialog | ✅ Skrevet |
| Build-verifisering | ⚠️ Ikke kjørt (gjør `Tving Web rebuild.bat`) |
| OpenTelemetry 1.15.3-bump | ❌ Fortsatt suppressed midlertidig |

## Kjente begrensninger / oppfølging

1. **Migrasjons-historikk er ikke commitet ennå.** Initial + AddAnnotations må genereres.
   `Generer migrasjoner.bat` gjør jobben. Etter at de er commitet, fjern EnsureCreated-fallbacket
   i `DatabaseBootstrapper` (ref linje 41–47 — kommentaren der er allerede på plass).
2. **Auth er anonym.** Endepunktene har TODO-kommentarer for `PlantReader`/`PlantAnalyst`-policy
   som aktiveres når Entra ID kobles til.
3. **Audit-logg er bevisst skrudd av** for annoteringer (per spec). Hvis dette endres senere,
   inject `IAuditLogger` i repository og logg på Create/Update/SoftDelete.
4. **Performance:** overlay er O(N×M) (timer × annoteringer). Overlay-validering kan optimaliseres
   til interval-tree om vi ser DB-trykk; i praksis er volumene små.
5. **KPI-recompute:** overlay rekjører `UptimeKpiCalculator` på read-time. For store rapporter
   med mange annoteringer kan dette legges i memory-cache med `(reportKey, annotation-revision)`
   som nøkkel.

## Forslag til commit-grupper

```
1. chore: dotnet-tools manifest + Generer migrasjoner.bat
2. feat(annotations): backend (Modules.Annotations + EF + endpoints + seed)
3. feat(annotations): read-time overlay i Get(Xlsx)Report
4. feat(web): driftstidslinje-heatmap + annoterings-dialog
5. docs: overlevering 2026-04-28
```

## Kommandoer for morgenen

```powershell
git status

# Reset DB + generer migrasjoner + bygg
docker compose down -v
.\"Generer migrasjoner.bat"
.\"Tving Web rebuild.bat"

# Smoke test
# - Last opp Drivdal-feb-2025 på /upload
# - Åpne rapporten på /reports
# - Klikk i driftstidslinjen for å lage en annotering
# - Verifiser at KPI-kortene reflekterer overstyringen
```
