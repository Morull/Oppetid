# Overlevering: data-import konsolidering + auto-folder + per-(plant, source)-fleksibilitet

**Dato:** 2026-05-03
**Status:** Levert. Kjørbar via `Start Oppetid.bat`. Test-status: 290/290 grønne.
**Hovedendring:** SPEC-IMPORT-COMPLETENESS + SPEC-AUTO-IMPORT-FOLDER implementert. Brukerflyt forenklet: én side (`/data-import`) for både status, manuell opplasting, og historikk. Auto-import-watcher som scanner hot-folder og prosesserer filer automatisk.

---

## TL;DR for ny chat-sesjon

1. Brukeren (Morten, drifts-leder Dalane Kraft) kjører appen via `Start Oppetid.bat` (Docker Compose: postgres + azurite + api + worker + web).
2. Auto-import overvåker mappa `C:\Morten\00 Oppetid\CSV Eksporter\` (mountet til `/imports` i API-containeren). Filer som droppes der prosesseres innen 30 sek.
3. UI-en `/data-import` har 3 tabs: **Status** (matrise), **Manuell opplasting**, **Historikk**.
4. Tre kilde-typer: `settlement` (KAIA-eksport), `scada` (master-CSV trender), `operlog` (alarmer/events).
5. Per-(plant, source)-konfigurasjon i PlantAdmin → "Forventede datakilder": aktiv/inaktiv, lag-dager, komplett-grense %.
6. Hvis bruker rapporterer "delvis"-status: tooltip viser `coverage_pct`. Justér terskel i PlantAdmin hvis SCADA er for streng.

---

## Det som er bygget i denne sesjonen

### 1. SPEC-IMPORT-COMPLETENESS (steg 1–7)

| Steg | Beskrivelse | Commit |
|---|---|---|
| 1 | Datamodell: `core.data_source_expectations` + `core.data_imports` + idempotent backfill | `2dc8bdd` |
| 2 | Importør-logging: settlement, multi-plant, SCADA master, operlog (single + multi) skriver til `data_imports` | `8531004` |
| 3 | Backfill av historikk fra eksisterende `settlement_imports`-tabell | `26d9a82` |
| 4 | `IDataCompletenessQueryService` + 8 in-memory EF tester | `55b0da0` |
| 5 | API-endepunkter: `/api/v1/data-status/{matrix,overdue,summary,recent}` | `103f7f5` + `189f5c4` |
| 6 | Status-matrise i UI med fargekodete celler | `a95a104` |
| 7 | PlantAdmin-toggle for å aktivere/deaktivere kilder per anlegg | `4872700` |
| 8 | ~~Ukentlig e-post-digest~~ | DROPPET (drifts-leder ba om kun in-app) |

### 2. Konsolidert UI (`/data-import`-side)

Per drifts-leders ønske er all manuell import + status samlet på én side:

- **Tab 1 – Status:** sammendrag-kort + matrise (anlegg × periode × kilde)
- **Tab 2 – Manuell opplasting:** single-plant, multi-plant settlement, multi-plant operlog, per-anleggs SCADA/operlog drop-zoner
- **Tab 3 – Historikk:** liste over siste importer (24t/3d/7d)
- **Auto-import-banner** alltid synlig på toppen med live status

Fjernet duplikater:
- `/upload` → redirect til `/data-import?tab=manuell`
- `/data-status` → redirect til `/data-import?tab=status`
- `/plants`-multi-drop-zoner og per-kort drop-strip → fjernet
- NavMenu: "Last opp" + "Data-status" → ett "Data-import"-element

Commit: `90ae7f2`

### 3. SPEC-AUTO-IMPORT-FOLDER (lettvektsversjon)

`HotFolderWatcher` (BackgroundService) i `Modules.Infrastructure/HotFolder/`:

- **Polling hvert 30. sek** med fil-stabilitets-sjekk (5 sek etter siste skrive)
- **Detektor** identifiserer filtype (xlsx/csv) + plant via:
  - Filnavn-regex
  - Content-sniff for SCADA (Cluster1.PREFIKS_-tags i header)
  - Content-sniff for operlog (station-kolonnen)
  - Content-sniff for single-plant xlsx (fane-navn → plant-id)
  - Multi-plant xlsx-detect (≥ 2 plant-faner → ruter til `/settlements/multi-plant`)
- **Routing til riktig importør:**
  - Settlement → POST `/api/v1/plants/{id}/settlements`
  - SettlementMultiPlant → POST `/api/v1/settlements/multi-plant`
  - SCADA trender → `IScadaImportService.ImportMasterCsvAsync` (in-process)
  - SCADA alarmer → `IScadaImportService.ImportOperlogCsvAsync`
  - Operlog multi-plant → POST `/api/v1/operlog/multi-plant`
- **Filhåndtering:**
  - Suksess → `<root>/done/<YYYY-MM>/<plantId>_<source>_<timestamp>_<original>`
  - Feil → `<root>/quarantine/<YYYY-MM-DD>/<original>` + `.error.txt`
- **API:**
  - `GET /api/v1/hot-folder/queue` — kø + recent
  - `POST /api/v1/hot-folder/scan-now` — manuell scan ("Skann nå"-knapp)
  - `POST /api/v1/hot-folder/retry-quarantine` — flytt karantene-filer tilbake
- **Konfig:**
  - `HotFolder:Enabled` (bool)
  - `HotFolder:RootPath` (`/imports` i container)
  - `HotFolder:PollIntervalSeconds` (30)
  - `HotFolder:FileStabilitySeconds` (5)
  - `HotFolder:ManualOnly` (false — sett true for kun manuell scan)
  - `HotFolder:ExcludePatterns` (default: README, .lock, ~$)
  - `HotFolder:PlantPrefixMap` (DRIVDAL→drivdal osv.)
- **Plant-prefiks-mapping** for SCADA-tag-prefikser:
  - DRIVDAL/DRIV → drivdal
  - LINDLAND/LIND → lindland
  - HAUKLAND/HAUK → haukland
  - HONNE → honnefoss
  - LIAVT → honnefoss (NB: LIAVT-tags i Honnefoss-eksport tilhører Honnefoss)
  - LIAVATN → liavatn (separat anlegg)
  - GRODEM/GRODEMFOSS → grodemfoss
  - OGREY/OGREYFOSS → ogreyfoss
  - LOGJEN/LOG → logjen
  - ORSDAL/ORSDALEN → orsdalen
  - VIKE/VIKESA → vikesa
  - STOLS/STOLSKRAFT → stolskraft

Commits: `d24e128`, `f712bbc`, `b8856fd`, `fa94abc`, `6b3fa65`, `ca61f49`

### 4. Docker-integrasjon

`docker-compose.yml` mounter Windows-mappa inn i API-containeren:

```yaml
volumes:
  - "${HOT_FOLDER_HOST_PATH:-./CSV Eksporter}:/imports"
environment:
  HotFolder__Enabled: "true"
  HotFolder__RootPath: "/imports"
  HotFolder__UploadBaseUrl: "http://localhost:8080/"
```

Test-fixture `dataeksport_20260429103503.xlsx` flyttet til `tests/_fixtures/` slik at den ikke blir auto-importert. `.gitignore` ekskluderer `/CSV Eksporter/` fra git.

Commit: `69d9e54`

### 5. Per-(plant, source)-fleksibilitet

`data_source_expectations`-tabellen har nå disse konfigurasjons-feltene per (plant, source):

| Felt | Type | Default | Beskrivelse |
|---|---|---|---|
| `is_active` | bool | true | Slå av forventning midlertidig |
| `expected_lag_days` | int | 7 (settlement), 5 (scada/operlog) | Dager etter periode-slutt før OVERDUE |
| `cadence` | varchar | "monthly" | Granularitet (kun monthly i v1) |
| `completion_threshold_pct` | double | 0.95 (settlement/operlog), 0.80 (scada) | Dekning som regnes som COMPLETE |
| `activated_at_utc` | timestamptz | 2024-01-01 | Tidligste periode som forventes |
| `deactivated_at_utc` | timestamptz | null | Når deaktivert (kan være null) |

UI: PlantAdmin → "Forventede datakilder"-tabellen lar drifts-leder endre alle felt utenom activated_at.

Auto-aktivering: ved første import for en (plant, source)-kombinasjon opprettes expectation-rad automatisk i `DbDataImportLogger.LogAsync`.

Commits: `b8856fd`, `d98ce62`

### 6. Per-måned-dekning (overlap-justert)

Kritisk fix i `GetMatrixAsync`: en import som dekker flere måneder (eks. SCADA Jan 15 → Mar 14) splittes nå til en celle per måned, og hver celle får dekning som **overlap-andel × import-dekning**:

| Måned | Overlap | Beregning | Coverage |
|---|---:|---|---:|
| Jan | 16/31 | 0.52 × 1.0 | **52 %** |
| Feb | full | 1.0 × 1.0 | **100 %** |
| Mar | 14/31 | 0.45 × 1.0 | **45 %** |

Hvis flere imports dekker samme måned: bruker den med høyest per-måned-dekning (eks. en månedlig snapshot vinner over en bred eksport som bare delvis dekker).

ScadaImportService bruker nå **faktisk min/max** som `period_from`/`period_to`, ikke avrundet til måneds-grenser. Det er kritisk for at overlap-beregningen skal være riktig.

Commits: `518ed0c`, `0460594`, `b3477c9`

---

## Kjent atferd og kalibrering

### "Hvorfor er SCADA delvis?"

Bruker hover over en delvis-celle i status-matrisen → tooltip viser actual coverage_pct.

Mulige årsaker:
1. **Boundary-måned:** importen dekker bare deler av måneden. Fix: ingen, dette er korrekt atferd.
2. **Hull i SCADA-fila:** uniqueHours / actualSpanHours < 1.0 i ScadaImportService. Vurder om eksporten hadde sensor-feil/manglende data.
3. **For streng terskel:** drifts-leder kan senke `completion_threshold_pct` for SCADA i PlantAdmin.

Default-terskler er smarte (settlement 95 %, SCADA 80 %), men kan justeres.

### Multi-plant Honnefoss / Liavatn-kollisjon

`HONNE_LIAVT_*`-tags er Honnefoss-anlegget (ikke Liavatn-kraftverket). Mappingen reflekterer dette:
- `LIAVT` → honnefoss
- `LIAVATN` → liavatn

Hvis bruker laster opp en Honnefoss-eksport som også har REVSVT/NODLANDVT-tags, må PlantDetector vurderes å detektere flere stations og rute til multi-plant. **Ikke implementert** — er forberedt på i `SPEC-HONNEFOSS-MAPPING.md`.

### Auto-aktivering vs manuell aktivering

Hvis en import kommer for et anlegg som ikke har en expectation-rad, **opprettes den automatisk** med smarte defaults. Det betyr at status-matrisen vokser etter hvert som data faktisk kommer inn. Hvis drifts-leder ikke vil at en kilde skal forventes, må de aktivt deaktivere via PlantAdmin etter første import.

---

## Hvordan kjøre appen

```powershell
# Start
Start Oppetid.bat
# Bygger Docker images, starter postgres + azurite + api + web
# Åpner http://localhost:5180 automatisk

# Stopp
Stopp Oppetid.bat
```

**Etter kode-endringer:** stopp + start (Start Oppetid.bat kjører `docker compose build` automatisk).

**Hard-refresh i browser** etter restart: Ctrl+Shift+R (Blazor WASM cacher aggressivt).

---

## Aktive specs i repo

Levert i tidligere sesjoner (denne sesjon brukte ikke disse direkte):

- `docs/SPEC-MVP-HARDENING.md` — auth, regresjonstest, datakvalitet, PlantType (LEVERT 2026-05-02, commit `26beded` mfl.)
- `docs/SPEC-IMPORT-COMPLETENESS.md` — denne sesjonens hovedspec (LEVERT 2026-05-03)
- `docs/SPEC-AUTO-IMPORT-FOLDER.md` — auto-folder (LEVERT 2026-05-03 i lettvektsversjon)

Klar for implementasjon (ikke startet):

- `docs/SPEC-HONNEFOSS-MAPPING.md` — oppdatert kaskade-modell, 4 dammer (ikke 6); REVSVT/NODLANDVT er Liavatn-kraftverk
- `docs/SPEC-LIAVATN-MAPPING.md` — nytt anlegg, venter på dedikert SCADA-eksport
- `docs/SPEC-LINDLAND-MAPPING.md` — SCADA-mapping for Lindland (8.9 MW)
- `docs/SPEC-CAPTURE-RATE.md` — fra tidligere
- `docs/SPEC-KASKADE-DAMMER.md` — fra tidligere
- `docs/SPEC-HYDROGRID-API.md` — fra tidligere

Tilhørende `NESTE-CHAT-*.md`-filer i rot.

---

## Filstruktur (kjernefiler)

### Backend

```
src/
├── KraftverkUptime.Core/
│   └── DataCompleteness/
│       └── IDataImportLogger.cs          (interface for å skrive data_imports)
├── KraftverkUptime.Modules.Reporting/
│   └── DataCompleteness/
│       └── IDataCompletenessQueryService.cs   (interface + DTOs)
├── KraftverkUptime.Infrastructure/
│   ├── HotFolder/
│   │   ├── HotFolderOptions.cs           (config)
│   │   ├── HotFolderWatcher.cs           (BackgroundService med polling)
│   │   ├── HotFolderDetector.cs          (filtype + plant-detect)
│   │   └── HotFolderQueue.cs             (in-memory kø + recent buffer)
│   ├── Persistence/
│   │   ├── Entities/
│   │   │   ├── DataSourceExpectation.cs
│   │   │   └── DataImport.cs
│   │   ├── KraftverkDbContext.cs         (DbSets + EF-mapping)
│   │   ├── DatabaseBootstrapper.cs       (idempotent skjema-bro + backfill)
│   │   └── DataImportsBackfillSeeder.cs  (historikk fra settlement_imports)
│   └── Reporting/
│       ├── DataCompletenessQueryService.cs   (matrise-bygging)
│       └── DbDataImportLogger.cs             (auto-aktivering inkludert)
└── KraftverkUptime.Api/
    └── Endpoints/
        ├── DataStatusEndpoints.cs        (matrix, overdue, summary, recent)
        ├── DataSourceExpectationsEndpoints.cs   (PlantAdmin upsert)
        └── HotFolderEndpoints.cs         (queue, scan-now, retry-quarantine)
```

### Frontend

```
src/KraftverkUptime.Web/
├── Pages/
│   ├── DataImport.razor                  (HOVED-side med 3 tabs + banner)
│   ├── Upload.razor                      (redirect til /data-import?tab=manuell)
│   ├── DataStatus.razor                  (redirect til /data-import?tab=status)
│   ├── PlantAdmin.razor                  (utvidet med "Forventede datakilder")
│   ├── Plants.razor                      (forenklet — ingen drop-zoner)
│   └── Anlegg.razor                      (lenker til /data-import)
├── Layout/
│   └── NavMenu.razor                     ("Data-import" som eneste import-link)
└── Services/
    └── NedetidApi.cs                     (alle nye API-klient-metoder + DTOs)
```

### Tester

```
tests/
├── _fixtures/                            (nytt: test-data adskilt fra live)
│   └── dataeksport_20260429103503.xlsx
├── KraftverkUptime.Infrastructure.Tests/
│   └── DataCompletenessQueryServiceTests.cs   (8 tester)
└── KraftverkUptime.EndToEnd.Tests/        (uendret — alle grønne)
```

---

## Kjente begrensninger og oppfølgings-arbeid

### Tekniske begrensninger

1. **Cadence er hardkodet "monthly"** i query-logikken. Ukentlig/daglig granularitet er ikke implementert. DB-feltet finnes men brukes ikke.
2. **Per-måned-dekning antar jevnt fordelte hull.** Hvis raw coverage er 0.8 og en månedsoverlap er 50%, regnes per-måned som 40 %. Faktisk kunne 100% av hullet vært i én måned, men vi har ikke per-time-data tilgjengelig.
3. **In-memory kø i HotFolder.** Ved API-restart mister vi kø-/recent-bufferen, men filer er fortsatt på disk og scannes på nytt. data_imports-tabellen er kilden til sannhet for "har dette blitt importert".
4. **HttpClient-routing til `localhost:8080` for settlement.** Dette er intern container-IP. Hvis API skal kjøre i scale-out (multiple containers) må dette refaktoreres.
5. **Ingen retry-policy** for failede HTTP-uploads i HotFolderWatcher. Filer havner i karantene umiddelbart ved feil.

### Brukbare neste steg (foreslås for ny chat)

1. **Per-måned override for expectation:** "for honnefoss SCADA jan 2026 forventes bare 50 % dekning" — krever ny tabell + UI.
2. **Cadence-utvidelse:** ukentlig SCADA, daglig settlement. Krever endring i `GetMatrixAsync`.
3. **SLA-eskalering:** OVERDUE > 14 dager → annet ikon / annen advarsel.
4. **"Prøv på nytt"-knapp per fil:** i stedet for "alle filer i karantene", la bruker velge én fil.
5. **Real-time SignalR-oppdatering** av banneren (i stedet for polling).
6. **HONNEFOSS-mapping:** REVSVT/NODLANDVT må flyttes til Liavatn-kraftverk-anlegget når det er opprettet (per `SPEC-HONNEFOSS-MAPPING.md`).
7. **LIAVATN-anlegg:** opprett som nytt plant + seed signal-map (per `SPEC-LIAVATN-MAPPING.md`).
8. **LINDLAND-mapping:** SCADA tag-mapping (per `SPEC-LINDLAND-MAPPING.md`).

---

## Kjørbarhet — sjekkliste for ny sesjon

```powershell
# Verifiser repo-state
cd "C:\Morten\00 Oppetid"
git log --oneline -5
# Skal vise b3477c9 som siste commit

# Bygg + test
dotnet build --nologo
dotnet test --nologo --no-build
# Skal vise 290/290 grønne

# Start appen
.\Start Oppetid.bat
# Eller direkte:
docker compose up -d --build

# Verifiser API kjører
curl http://localhost:5080/api/v1/data-status/summary
# Skal returnere JSON

# Verifiser Web kjører
# Åpne http://localhost:5180 i browser
# Hard-refresh (Ctrl+Shift+R)

# Stopp
.\Stopp Oppetid.bat
```

---

## Kontekst for ny chat-agent

Brukeren er **Morten Ulland**, drifts-leder for Dalane Kraft (11 vannkraftanlegg i Norge). Han bruker norsk i tilbakemeldinger. Han verdsetter:
- **Enkelhet over besparelser:** "jeg vil heller at det skal være enkelt enn at jeg sparer litt tid manuelt"
- **Konkrete steg-for-steg-instruksjoner** når noe går galt
- **Visuell verifikasjon** via UI heller enn å trekke tilbake til CLI

Hovedarbeidsflyt: drifts-leder eksporterer SCADA-data + KAIA-settlement til `CSV Eksporter`-mappa, appen plukker opp automatisk. Han stopper/starter appen via `Start Oppetid.bat`.

11 anlegg:
- drivdal, lindland, haukland, honnefoss (kaskade m/Kydland + Spjodevatn-magasin), liavatn (separat fra Liavatn-kraftverket — SCADA-tag-prefiks `LIAVT` er Honnefoss-inntak)
- grodemfoss, ogreyfoss, logjen, orsdalen, vikesa, stolskraft

PlantType per anlegg er bekreftet 2026-05-02 (commit `26beded`):
- Regulated: drivdal, haukland, honnefoss, liavatn, ogreyfoss, logjen, grodemfoss
- RunOfRiver: lindland (kaskade m/24t-lag), orsdalen
- Mixed: vikesa (lite magasin), stolskraft (vannforbruks-styrt)

---

## Siste commits referanse-tabell

```
b3477c9  fix(scada): faktisk min/max periode-grenser
0460594  feat: per-måned overlap-dekning
d98ce62  feat: per-(plant, source) konfig-grense
518ed0c  fix: matrise viser flerårige imports korrekt
ca61f49  fix: single-plant xlsx + redusert default-vindu
69d9e54  fix(docker): mount CSV Eksporter
f669fcf  fix: SCADA coverage + fjern dato-velger
6b3fa65  fix: operlog plant-detect + retry-knapp
fa94abc  fix: GRODEM-prefiks + multi-plant detect
095f79e  chore: HotFolder-config i appsettings.Dev
b8856fd  feat: auto-aktiver expectation
f712bbc  feat: 'Skann nå'-knapp + ManualOnly
d24e128  feat: 3 source types + auto-import-mappe
90ae7f2  feat: konsolidert /data-import (3 tabs)
189f5c4  feat: GET /data-status/recent
6d24d57  docs: nye specs
4872700  feat: PlantAdmin-toggle (steg 7)
a95a104  feat: status-side med matrise (steg 6)
103f7f5  feat: API-endepunkter (steg 5)
55b0da0  feat: query-service + 8 tester (steg 4)
26d9a82  feat: backfill historikk (steg 3)
8531004  feat: importør-logging (steg 2)
2dc8bdd  feat: datamodell (steg 1)
```
