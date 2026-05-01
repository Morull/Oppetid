# Spec: Slett- og reset-funksjonalitet i UI

**Dato:** 2026-04-29
**Estimat:** 2-3 t
**Bakgrunn:** I dag er eneste måten å slette importert data å kjøre SQL/docker-kommandoer manuelt. Bruker (Morten) ber om UI-funksjonalitet for å rydde opp uten å gå utenfor appen.

## Mål

Tre nivåer av sletting tilgjengelig i UI:

1. **Slett enkelt settlement-import** (mest brukt) — søppelkasse-knapp på `/reports`
2. **Reset enkelt anlegg** — slett all importert data for ÉTT anlegg på `/plants/{id}/admin`
3. **Full system-reset** — slett ALLE settlement, SCADA, operlog, klassifiserte events, annoteringer, KPI-facts. Kun strukturen (plants, signal_map) beholdes. Skjult bak `/admin/system`.

Plant-rader og plant_configurations skal ALDRI slettes via disse endepunktene — bruk Plant-admin for å endre/deaktivere anlegg.

## Endepunkter

```
DELETE /api/v1/plants/{plantId}/settlements/{idempotencyKey}
DELETE /api/v1/plants/{plantId}/data            ← reset enkelt anlegg
DELETE /api/v1/admin/system/data                ← full reset
```

Alle krever bekreftelses-payload for å unngå utilsiktet sletting:

```json
{
  "confirmText": "drivdal"          // for plant-reset: må være plantId
  // for full reset: må være "SLETT ALT"
}
```

## Implementasjons-stegene

### Steg 1 — Slett enkelt settlement-import

**Backend:** `src/KraftverkUptime.Api/Endpoints/SettlementsEndpoints.cs`

Legg til:

```csharp
group.MapDelete("/{idempotencyKey}", DeleteSettlementAsync)
    .WithName("DeleteSettlement")
    .WithSummary("Sletter en spesifikk settlement-import + tilhørende rapport-blob.")
    .AllowAnonymous()
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound);
```

Implementasjon:
1. Slett blob-rapporten via `IUptimeReportStore.DeleteAsync` (legges til i interfacet — eksisterer ikke ennå)
2. Slett rad fra `core.settlement_imports`
3. Slett tilhørende `core.classified_events` om noen er knyttet til denne import-perioden (cascade ikke automatisk)

**Frontend:** `src/KraftverkUptime.Web/Pages/Reports.razor`

Legg til søppelkasse-ikon på hver rad. Klikk åpner `MudDialog` med:
- Plant-navn + periode + import-tidspunkt
- "Skriv 'slett' for å bekrefte" tekstfelt
- Slett-knapp (deaktivert til riktig tekst skrevet inn)

Etter sletting: refresh listen + toast-melding "Import slettet".

### Steg 2 — Reset enkelt anlegg

**Backend:** `src/KraftverkUptime.Api/Endpoints/PlantsEndpoints.cs`

Legg til:

```csharp
group.MapDelete("/{plantId}/data", ResetPlantDataAsync)
    .WithName("ResetPlantData")
    .WithSummary("Sletter ALL importert data for et anlegg. Plant-raden beholdes.")
    .AllowAnonymous()
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest);
```

Implementasjon — slett (per plant_id):
- `core.settlement_imports`
- `core.classified_events`
- `core.sample_facts`
- `core.kpi_facts`
- `core.downtime_annotations`
- Blob-rapporter via `IUptimeReportStore.DeleteAllForPlantAsync`

Behold:
- `core.plants` (plant-raden selv)
- `core.signal_map` (SCADA-tag-mapping)
- `core.plant_configurations`

**Frontend:** `src/KraftverkUptime.Web/Pages/PlantAdmin.razor`

Ny seksjon nederst: "Farezone — slett data".
- Ekspandbart panel (lukket som default)
- Tekstboks: "Skriv plant-id for å bekrefte"
- Slett-knapp (rød, deaktivert til match)

Etter sletting: redirect til `/plants` + toast "Data slettet".

### Steg 3 — Full system-reset

**Backend:** `src/KraftverkUptime.Api/Endpoints/AdminEndpoints.cs` (ny fil)

```csharp
group.MapDelete("/system/data", ResetSystemDataAsync)
    .WithName("ResetSystemData")
    .WithSummary("Sletter ALL data men beholder plants og signal_map. Bruk med varsomhet.")
    .AllowAnonymous()  // TODO(prod): RequireAuthorization(AdminPolicy)
    .Produces(StatusCodes.Status204NoContent);
```

Implementasjon: TRUNCATE alle tabeller utenom `core.plants`, `core.signal_map`, `core.plant_configurations`. Slett alle blob-rapporter.

**Frontend:** `src/KraftverkUptime.Web/Pages/AdminSystem.razor` (ny route `/admin/system`)

- Ikke i navigasjon (man må vite URL-en)
- Tekstboks: "Skriv 'SLETT ALT' for å bekrefte"
- To-trinns bekreftelse: trykk slett → ny dialog med tellerverdi (3 sek countdown) → reell sletting

### Steg 4 — Repository-utvidelser

**Fil:** `src/KraftverkUptime.Modules.Reporting/Storage/IUptimeReportStore.cs`

```csharp
Task DeleteAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct);
Task DeleteAllForPlantAsync(string ownerOrgId, string plantId, CancellationToken ct);
Task DeleteAllAsync(string ownerOrgId, CancellationToken ct);
```

**Fil:** `src/KraftverkUptime.Modules.Settlement.Persistence/ISettlementImportRecorder.cs`

```csharp
Task DeleteAsync(string plantId, string idempotencyKey, CancellationToken ct);
Task DeleteAllForPlantAsync(string plantId, CancellationToken ct);
```

Lignende for `IClassifiedEventRepository`, `IScadaSampleRepository`, `IKpiFactsRepository`, `IDowntimeAnnotationRepository`.

### Steg 5 — Tester

**Ny fil:** `tests/KraftverkUptime.Api.Tests/SettlementDeleteEndpointTests.cs`
- DELETE en eksisterende import → 204, raden borte fra DB, blob slettet
- DELETE en ikke-eksisterende → 404
- DELETE rensker også relaterte classified_events (verifiser i testbase)

**Ny fil:** `tests/KraftverkUptime.Api.Tests/PlantResetEndpointTests.cs`
- Reset plant → settlement, scada, operlog, kpi_facts, annotations alle slettet for plantet
- Andre plants påvirkes ikke
- Plant-raden beholdes

**Ny fil:** `tests/KraftverkUptime.Api.Tests/AdminResetEndpointTests.cs`
- Full reset → alle data-tabeller tomme
- plants og signal_map beholdes
- Test idempotens (kall to ganger → fortsatt OK)

## Verifisering

```powershell
# Last opp testdata først
curl.exe -X POST "http://localhost:5080/api/v1/settlements/multi-plant" -F "file=@CSV Eksporter/dataeksport_20260429103503.xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

# Test slett enkelt
$key = (curl.exe "http://localhost:5080/api/v1/plants/drivdal/settlements" | ConvertFrom-Json | Select-Object -First 1).idempotencyKey
curl.exe -X DELETE "http://localhost:5080/api/v1/plants/drivdal/settlements/$key" -H "Content-Type: application/json" -d '{"confirmText":"slett"}'

# Test reset plant
curl.exe -X DELETE "http://localhost:5080/api/v1/plants/drivdal/data" -H "Content-Type: application/json" -d '{"confirmText":"drivdal"}'

# Sjekk database
docker compose exec postgres psql -U kraftverk -d kraftverk -c "SELECT COUNT(*) FROM core.settlement_imports WHERE plant_id='drivdal';"
# Forventet: 0
```

## UI-mønstre

- Bruk MudBlazor's `MudDialog` med rød `Severity.Error`-styling
- Slett-knapper: `Color="Color.Error"`, `Variant="Variant.Filled"`
- Bekreftelses-tekstbokser: `Immediate="true"` så knappen kan oppdatere disabled-state live
- Etter sletting: `MudSnackbar` med suksess-melding
- Ikke autoredirect på Steg 1 (slett enkelt) — la brukeren se listen oppdatert

## Out-of-scope (kommer senere)

- Brukerroller (kun admin kan utføre full reset)
- Audit-logg av hvem som slettet hva
- "Soft delete" med 30-dagers angre-vindu
- Bulk-slett (slett alle imports for en periode)
