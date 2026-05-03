# Overlevering: Import-completeness-dashboard (SPEC-IMPORT-COMPLETENESS)

**Dato:** 2026-05-03
**Spec:** `docs/SPEC-IMPORT-COMPLETENESS.md`
**Drift-leder-justering:** `Steg 8 (e-post-digest) droppet — kun in-app visning. Manuell import + status konsolidert til én side.`
**Test-status:** 290/290 grønne (282 før + 8 nye DataCompleteness-tester)
**Build-status:** 0 errors, 0 warnings

## Sammendrag

Hele SPEC-IMPORT-COMPLETENESS levert med følgende justeringer per drifts-leders ønsker 2026-05-03:

| Steg | Innhold | Status |
|---|---|---|
| 1 | Datamodell + migreringer + backfill av expectations | ✅ |
| 2 | Importør-utvidelser (settlement, multi-plant, SCADA, operlog) | ✅ |
| 3 | Backfill av `data_imports` fra historikk | ✅ |
| 4 | `IDataCompletenessQueryService` + 8 tester | ✅ |
| 5 | API-endepunkter | ✅ |
| 6 | Status-side med matrise | ✅ |
| 7 | PlantAdmin-utvidelse for kilde-toggle | ✅ |
| 8 | ~~Ukentlig e-post-digest~~ | ❌ Droppet (in-app i stedet) |
| **+** | **Konsolidert /data-import-side** | ✅ Lagt til |
| **+** | **Auto-import-infobar** | ✅ Lagt til |

## Justeringer fra spec'en

### 1. Steg 8 droppet — kun in-app visning

Per drifts-leders forespørsel: ingen e-post-utsendelse. Statusen monitoreres direkte i appen via:
- **Auto-import-infobar** (alltid synlig på toppen av `/data-import`)
- **Status-fane** med matrise og topp-overdue
- **Historikk-fane** med liste over siste importer

Implementasjons-besparelse: ~3-4 timer (ingen SMTP/Graph-config, ingen ukentlig job-scheduler, ingen e-post-template).

### 2. Konsolidering — én plass for alt

All manuell import + status er flyttet til **én side** (`/data-import`). Tidligere lå funksjonaliteten spredt over flere sider:

| Tidligere plass | Innhold | Status nå |
|---|---|---|
| `/upload` | Single-plant settlement-opplasting | Redirect til `/data-import?tab=manuell` |
| `/data-status` | Matrise (bygd i Steg 6) | Redirect til `/data-import?tab=status` |
| `/plants` toppen | Multi-plant settlement + multi-plant operlog drop-zoner | Flyttet til `/data-import?tab=manuell` |
| `/plants` per-kort | Drop-strip + "Last opp"-knapp per anlegg | Fjernet (deep-link til /data-import) |
| `/anlegg/{id}` header | "Last opp"-knapp | Lenker til `/data-import?tab=manuell&plant={id}` |
| Index "Hurtigvalg" | "Last opp fil"-kort | Endret til "Data-import"-kort |
| NavMenu | "Data-status" + "Last opp" | Konsolidert til "Data-import" |

## Den nye `/data-import`-siden

### Auto-import-infobar (alltid synlig)

```
[🔄 Sync] Siste 24 t: 3 importer mottatt — sist drivdal Settlement (KAIA) for feb 2026 (12 min siden)
                                            [✓ 7 komplett] [⚠ 2 delvis] [! 1 forfalt]  [⟳ Refresh]
```

Viser:
- Antall importer siste 24 timer
- Hvilken som var sist (anlegg + kilde + relativ tid)
- Status-pilles (komplett / delvis / venter / forfalt)
- Refresh-knapp

### Tab 1: Status

- 4 sammendrag-kort (Komplette / Delvise / Venter / Forfalt)
- Filter: "Vis kun forfalte" + kildetype-dropdown
- Matrise (anlegg × periode × kilde) med fargekodede symboler
- Topp 5 forfalte importer som tabell

### Tab 2: Manuell opplasting

4 kort i grid-layout:
1. **Enkelt-anleggs-avregning**: dropdown for anleggsvalg + xlsx-fil-velger
2. **Multi-anleggs-eksport**: drop-zone for xlsx med flere faner
3. **Multi-anleggs operlog**: drop-zone for csv med events fra alle stasjoner
4. **SCADA / operlog (per anlegg)**: csv-fil-velger som auto-detekterer master vs operlog basert på filnavn

### Tab 3: Historikk

- Velg vindu (24t / 3 dager / 7 dager)
- Tabell med: tidspunkt, anlegg, kilde, periode, filnavn, rader, dekning (fargekodet), bruker

## Data-modell + flyt

### Tabeller (Steg 1)

```
core.data_source_expectations
├── plant_id, source_type (PK)
├── cadence ("monthly")
├── expected_lag_days (default 7 settlement, 5 SCADA)
├── is_active + activated_at_utc / deactivated_at_utc
└── (drifts-leder toggler via PlantAdmin)

core.data_imports
├── import_id (UUID PK)
├── plant_id, source_type, period_from_utc, period_to_utc
├── imported_at_utc, file_name, file_hash
├── rows_imported, coverage_pct
└── user_id, notes
```

### Importør-flyt (Steg 2)

```
ParseSettlementJobHandler ─┐
                           ├─→ logger settlement + (optional) hydrogrid_plan
MultiPlantSettlements ─────┘

ScadaImportService.ImportMasterCsv ─→ logger scada
ScadaImportService.ImportOperlogCsv ─→ logger operlog (per plant)
ScadaImportService.ImportOperlogMultiPlant ─→ logger operlog per plant i loop
```

Coverage-beregning:
- Settlement: `rows / expected_hours_in_period`
- Hydrogrid_plan: `rows_with_plan / expected_hours_in_period`
- SCADA: `unique_time_slots / expected_hours`
- Operlog: alltid 1.0 (ingen "forventet antall events" å normalisere mot)

### Backfill (Steg 3)

`DataImportsBackfillSeeder` kjøres i `DatabaseBootstrapper`. Idempotent via `NOT EXISTS`. Backfill av historikk fra `core.settlement_imports`:
- Én settlement-rad per historisk import
- Én hydrogrid_plan-rad med konservativ coverage = 0.95

SCADA og operlog backfill droppet (manglende metadata; de fylles fremover).

### API-endepunkter (Steg 5 + utvidelse)

```
GET  /api/v1/data-status/matrix?from=&to=          (full matrise)
GET  /api/v1/data-status/overdue                   (overdue-rader sortert)
GET  /api/v1/data-status/summary                   (sammendrag forrige+denne mnd)
GET  /api/v1/data-status/recent?hours=24&limit=50  (NY: nylige importer for infobar)

GET  /api/v1/plants/{plantId}/data-source-expectations           (PlantReader)
PUT  /api/v1/plants/{plantId}/data-source-expectations/{source}  (PlantAdmin upsert)
```

Alle bak `RequireAuthorization(AuthorizationPolicies.PlantReader)` eller `PlantAdmin`. V1 returnerer fortsatt "allow" inntil Entra ID kobles til.

## Tester

8 nye tester i `DataCompletenessQueryServiceTests.cs` (alle grønne):

1. `FullCoverage_StatusComplete` — coverage 1.0 → COMPLETE
2. `LowCoverage_StatusPartial` — coverage 0.90 → PARTIAL
3. `NoImport_WithinLagWindow_StatusPending` — innenfor 7d lag → PENDING
4. `NoImport_LagExceeded_StatusOverdue` — 60d etter periode → OVERDUE
5. `InactiveSource_NotInMatrix` — `is_active=false` → ingen celler
6. `WeeklySummary_AggregatesByStatus` — telling per status
7. `GetOverdue_SortedByDaysOverdueDesc` — sortering bekreftet
8. `NewPlant_NoHistory_GetsExpectedStatus` — `activated_at` filtrerer riktig

Bruker in-memory EF + fast `TimeProvider` for deterministisk verifisering av status-grenser.

## Sikkerhet

Alle nye endepunkter krever `PlantReader`-policy for read og `PlantAdmin` for write (toggle).
Audit-logg via `IAuditLogger` på alle write-operasjoner:
- `data_source_expectation.created` / `data_source_expectation.updated`
- Eksisterende settlement/scada/operlog-import-loggene fanger import-events.

## Endrings-statistikk

```
Filer endret:        17
Nye filer:            7
Nye tester:          +8 (282 → 290, alle grønne)
Pages konsolidert:    3 → 1 (/upload + /data-status → /data-import)
Drop-zones flyttet:   3 (fra /plants til /data-import)
NavMenu-lenker:      -1 (Data-status og Last opp slått sammen)
```

## Verifikasjon

```powershell
# 1. Build + test
dotnet build --nologo
dotnet test --nologo --no-build
# Forventet: 290/290 grønne

# 2. Tabeller eksisterer
psql -d kraftverkuptime -c "\d core.data_source_expectations; \d core.data_imports"

# 3. Backfill ble kjørt
psql -d kraftverkuptime -c "SELECT COUNT(*) FROM core.data_source_expectations;"
# Forventet: ~22 rader (11 plants × settlement + hydrogrid_plan)

psql -d kraftverkuptime -c "SELECT COUNT(*) FROM core.data_imports WHERE user_id='system-backfill';"
# Forventet: > 0 (avhenger av eksisterende settlement-historikk)

# 4. UI-test
# http://localhost:5180/data-import           — landingsside
# http://localhost:5180/data-import?tab=manuell — direkte til opplasting
# http://localhost:5180/upload                 — redirect til /data-import?tab=manuell
# http://localhost:5180/data-status            — redirect til /data-import?tab=status

# 5. PlantAdmin-toggle
# http://localhost:5180/plants/lindland/admin → "Forventede datakilder"-seksjon
# Toggle SCADA på → settles inn ny rad i data_source_expectations
```

## Neste steg (utenfor denne overleveringen)

1. **Auto-import oppdaget**: per nå er "auto-import" bare bakgrunns-job-køen som behandler manuelle uploads. Reell auto-pull (KAIA-API, Hydrogrid-API) krever credentials og er en større egen oppgave.
2. **Job-queue-status i infobar**: kan utvides til å vise "X jobber i klassifiserings-køen akkurat nå" via `IJobQueue.PendingCount`-API.
3. **Filter-toggle på Nedetid/Produksjon** (utsatt fra SPEC-MVP-HARDENING tiltak C — krever endring i NedetidQueryService og ProduksjonAnalyseQueryService).
4. **Per-uke-granularitet for SCADA**: cadence != monthly fungerer ikke i query-service ennå.
5. **SLA-eskalering**: hvis OVERDUE > 14 dager, vis ekstra varsel.

## Referanser

- Spec: `docs/SPEC-IMPORT-COMPLETENESS.md`
- Konsolidering bekreftet: brukerinput Cowork-samtale 2026-05-03 (drop e-post-digest, samle alt på én side)
- Forrige overlevering: `OVERLEVERING-2026-05-03-MVP-HARDENING.md`
