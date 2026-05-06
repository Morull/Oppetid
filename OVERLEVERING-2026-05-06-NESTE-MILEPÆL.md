# Overlevering: 2026-05-06 — alle 5 ønsker levert + signal-mapping + smartere Vakt-ROI

**Dato:** 2026-05-06
**Status:** Levert. Kjørbar via `Start Oppetid.bat`. Test-status: 383/383 grønne.
**Hovedendring:** Alle 5 brukerønsker fra 2026-05-05 levert; signal-mapping
for 5 anlegg fullført (Drivdal/Haukland/Lindland/Grødemfoss/Honnefoss);
smartere overflow-modell + manuell override; driftslinje på Produksjon.

---

## TL;DR for ny chat-sesjon

1. Brukeren (Morten Ulland, drifts-leder Dalane Kraft, 11 anlegg) kjører appen via `Start Oppetid.bat` (Docker Compose). Hovedside: http://localhost:5180.
2. **Alle 5 ønsker fra 2026-05-05 er levert.** Detaljer i seksjon "Levert i denne sesjonen".
3. **Pågående arbeid (sesjon kontinuerer):** brukeren skal sette opp appen på en ekstra kontor-PC for å dele med kollegaer på LAN. Guide ligger i [OPPSETT-KONTOR-PC.md](OPPSETT-KONTOR-PC.md).
4. **Nytt fokus for kommende milepæl:** se "Brukerens nye prioriterte ønsker" — ingen er levert ennå (ny chat starter med å bekrefte prioritering).
5. **For mapping av de 6 gjenværende anleggene** (liavatn, logjen, ogreyfoss, orsdalen, stolskraft, vikesa) trenger vi SCADA-eksport per anlegg fra brukeren først.

---

## Levert i denne sesjonen (2026-05-05 → 2026-05-06)

### De 5 brukerønskene fra forrige overlevering

| # | Ønske | Status | Commit |
|---|---|---|---|
| 1 | Globalt anlegg + periode-velger i topp-baren | ✅ Levert | `537cb6b` |
| 2 | Redigerbare hendelser i Nedetid + sync med Rapport | ✅ Levert | `537cb6b` |
| 3 | Editerbar cause-tekst (cause-aliaser i Kategorier) | ✅ Levert | `537cb6b` |
| 4 | Konsolider terminologi: "Driftstilstand" vs "Årsak" | ✅ Levert | `537cb6b` |
| 5 | Vakt-ROI portefølje-dashboard | ✅ Levert | `537cb6b` |

### Signal-mapping (5 av 11 anlegg ferdig)

| Anlegg | Tags i sample_facts | Tags mappet i signal_map | Dam-topologi | Status |
|---|---|---|---|---|
| drivdal | 37 | 37 (utvidet fra 21) | 1 dam (Drivdalsvatn) | ✅ |
| grodemfoss | 20 | 20 (kun G2 — G1 havarert) | 1 dam (Smievatn) | ✅ |
| haukland | 195 | 195 | 4-dam-kaskade | ✅ |
| honnefoss | 113 | 113 | 4-dam-kaskade + REVSVT/NODLANDVT (dam_id=null) | ✅ |
| lindland | 117 | 117 | 4-dam-kaskade | ✅ |
| **liavatn** | 0 | 0 | 4 dammer (topologi seedet) | ❌ Mangler SCADA |
| **logjen** | 0 | 0 | 1 dam (Åvedalsvatn) | ❌ Mangler SCADA |
| **ogreyfoss** | 0 | 0 | 7 dammer | ❌ Mangler SCADA |
| **orsdalen** | 0 | 0 | 1 dam (Inntak) | ❌ Mangler SCADA |
| **stolskraft** | 0 | 0 | 1 dam (Stølsvatn IVAR) | ❌ Mangler SCADA |
| **vikesa** | 0 | 0 | 1 dam (Storrsheivatn) | ❌ Mangler SCADA |

### Smartere overflow-deteksjon (commit `e809f85`)

1. **Overflow-terskel hevet** fra 0.001 → 0.5 m³/s. Filtrerer sensor-glitches
   (verifisert mot Mars 9-eventet på Stemmevatn der 0.16-2.28 m³/s ga falsk
   "reddet produksjon"). 2 tester oppdatert.

2. **Manuell override per vakt-event** (`core.vakt_event_overrides`):
   - Drifts-leder kan tvinge "HaddeOverlop" eller "IkkeOverlop" på en
     spesifikk hendelse uavhengig av SCADA-data.
   - API: `PUT /api/v1/plants/{id}/vakt-overrides`
   - UI: dropdown per rad i Vakt-ROI-tabellen.
   - VaktRoiCalculator overstyrer savedOverflowHours basert på classification.
   - Forklaringen inkluderer "⚠ Manuelt overstyrt".

3. **Tilsig-basert "ville-vært-overflow"-modell:**
   - `InflowOverflowEstimator` (pure logic): tilsig = ΔVolum/Δt + utløp,
     antar tilsig-snitt holder seg + turbinen er av i counterfactual,
     beregner timer-til-fullt-magasin.
   - `InflowOverflowQueryService`: henter SCADA-samples for terminal-dam
     (volum, total-flow, turbin-flow, fyllgrad) over siste 24t lookback.
   - API: `GET /api/v1/plants/{id}/inflow-estimate?from=&to=`
   - **Ikke aktivt brukt i Vakt-ROI ennå** — eksponert for at drifts-leder
     kan sammenligne mot SCADA-direkte før modellen blir primær.
   - Verifisert mot Mars 9-eventet: modellen sier "ingen estimert overflow"
     (tilsig 0.31 m³/s ≤ utløp 2.16 m³/s), enig med drifts-leder.

### Driftslinje på Produksjon-siden (commit `e809f85`)

`ProduksjonTimeline.razor` (ny komponent): heatmap-kalender med 24 timer ×
N dager. Fargekoder per time:
- Grønn skala (mørkere = nær plan) → drift
- Cyan → overløp
- Lilla skala (mørkere = høyere) → ubalanse-kost
- **Rød ramme + glød** → stor ubalanse-kost (> 5 000 NOK terskel)
- Grå → ingen drift

Tooltip per time viser Plan/Elhub/Spot/RK/ubalanse-kost.

`ProduksjonHourlyPoint` utvidet med `RkPrisNokMwh` + `HarOverlop` +
`UbalanseKostNok` slik at backend leverer alt som trengs i én call.

### Reports + ReportDetail følger AppBar (commit `c6e8e49`)

- Reports.razor: fjernet lokal plant + dato-velger, lytter til
  `Filter.OnChange` og re-laster ved endring.
- ReportDetail.razor: når brukeren bytter anlegg i AppBar mens en
  spesifikk rapport vises, naviger til `/reports` (ikke samme rapport
  for nytt anlegg — det finnes neppe).
- Plants.razor: hele anlegg-kortet er nå klikkbart → `/anlegg/{plantId}`
  (commit `07110f4`). Knappene "Rapporter" og "Admin" har stopPropagation.

### Signal-katalog UI på Anlegg-siden (commits `c6e8e49`, `30c1b17`)

`PlantSignalList.razor` med tre tydelige seksjoner:
- 🟢 **Brukes i KPI-beregning** (grønn ramme): KPI-kritiske roller —
  GeneratorActivePower, TurbineWaterFlow, OverflowFlow, ReservoirFillFactor,
  ReservoirVolume, TotalDamFlow, UpstreamLevel, LowestRegulatedLevel,
  CommunicationAlarm + 1 til.
- 🟡 **Lagret for analyse** (gul ramme): Operative tags som lagres for
  traceability men ikke direkte konsumert i KPI-er.
- ⚪ **Ikke brukt — kan droppes** (stiplet ramme, kollapsbar): full liste
  over Other-rolle-tags som drifts-leder kan fjerne fra neste SCADA-eksport
  for å redusere fil-størrelse.

Mangler-banner øverst hvis dette anlegget mangler en rolle som andre
anlegg har konfigurert.

### KPI info-tooltips (commit `932e4e5`)

`KpiCard.razor` (ny gjenbrukbar komponent) med innebygd ⓘ-ikon.
Lagt til på Anlegg-sidens 6 KPI-kort:
- Tilgjengelighet (AF) — formel i tooltip
- FO-rate (FOR) — forklaring + formel
- Produksjon (MWh)
- Spotomsetning (NOK)
- Capture rate
- Merverdi vs spot

### Oppsett-guide for kontor-PC (commit `9798219`)

[OPPSETT-KONTOR-PC.md](OPPSETT-KONTOR-PC.md): komplett guide for å
kjøre Oppetid på en delt kontor-PC. Dekker Docker-installasjon, .env,
brannmur-port, auto-start, daglig backup, delt nettverks-mappe for
hot-folder, vedlikehold + feilsøking.

`.env.example` utvidet med HOT_FOLDER_HOST_PATH-eksempel.
`docker-compose.yml` har `restart: unless-stopped` på alle 4 tjenester.

---

## Brukerens nye prioriterte ønsker

Drifts-leder ba om disse i 2026-05-06-sesjonen. Ranger etter intuisjon
om verdi vs implementasjons-kost.

### 1. **Smartere overflow-modell PRIMÆR i Vakt-ROI**

Tilsig-modellen er bygget og verifisert (jf. Mars 9-eventet). Men den
brukes IKKE som primær — `VaktRoiCalculator` bruker fortsatt SCADA-direkte
overflow.

**Ønske:** Endre Vakt-ROI til å bruke modellens estimat når SCADA-data
mangler (overflowDataMissing) eller er stille (0 i hele vinduet).

**Teknisk plan:**
- Utvid `OverflowQueryService.GetOverflowDatasetAsync` til å returnere
  både `OverflowHours` (SCADA-direkte) og `EstimatedOverflowHours` (modell)
- Calculator bruker SCADA primært, faller tilbake til estimat hvis SCADA
  er tom og estimat > 0
- Forklaringen markerer "estimert" tydelig så brukeren ser kilden

**Estimat:** 3-4 timer.

### 2. **Multi-generator-støtte (Lindland G1+G2, Øgreyfoss G1+G2)**

Lindland har faktisk to generatorer (begge produserer). Øgreyfoss har
også to. I dag aggregeres de som én. Drifts-leder ville sett separat
KPI per generator.

**Ønske:** Generator-entity i datamodellen + per-generator-aggregering.

**Teknisk plan:** Spec-en finnes: `docs/SPEC-LINDLAND-MAPPING.md` Phase 2.
Krever:
- `core.generators (plant_id, generator_id, name, capacity_mw)`
- `signal_map.generator_id` kolonne (nullable)
- KPI-katalog utvidet med per-generator KPI-er
- UI: tabs eller fane-bytte mellom G1/G2 på Effektivitet/Produksjon

**Estimat:** 1-1.5 dager.

### 3. **Mapping for de 6 gjenværende anleggene**

Liavatn, Løgjen, Øgreyfoss, Ørsdalen, Stølskraft, Vikeså mangler SCADA-
data + signal_map. Når brukeren leverer SCADA-eksport per anlegg, kan
mapping bygges på samme mønster som Honnefoss (self-bootstrapping fra
sample_facts) eller Drivdal/Grødemfoss (eksplisitt Mappings-array).

**Avhengig av:** drifts-leder leverer SCADA-eksport per anlegg.

**Estimat:** ~30 min per anlegg når data er på plass.

### 4. **Met.no nedbør-forecast for tilsig-modell**

Dagens tilsig-estimat antar at tilsig holder seg ≈ siste 24t-snitt.
For værdrevne situasjoner (kraftig regn varslet) kan ekstern forecast
gi bedre prediksjon.

**Teknisk plan:**
- Plant.Latitude + Plant.Longitude på PlantRegistration
- `MetNoForecastClient` mot https://api.met.no/weatherapi/locationforecast/2.0/
- Konvertere mm/h → m³/s via nedslagsfelt-areal (Plant.CatchmentAreaKm2)
- Inflow-estimator får forecast-injection som overstyrer 24t-snittet

**Estimat:** 1 dag.

### 5. **Multi-user auth (Entra ID)**

Når appen flyttes til delt kontor-PC og senere VM, bør auth være på
plass før det eksponeres bredt.

**Teknisk plan:** Spec-en finnes: `docs/SPEC-MULTI-USER-DEPLOYMENT.md`.
Krever:
- MSAL-WASM auth-flyt frontend
- Bearer-token-validering på API-en
- Multi-tenant-tabeller (org-bytter må kobles inn på enheter)
- Per-bruker rolletilgang (PlantReader/PlantAnalyst/PlantAdmin er allerede
  modellert i `AuthorizationPolicies`)

**Estimat:** 1-2 dager.

---

## Status: pending arbeid på tvers av specs

### Klare for implementasjon

1. **Brukerens 5 nye ønsker** over (toppprioritet — ranger først)
2. **SPEC-LIAVATN-MAPPING** — venter på SCADA-eksport
3. **SPEC-LINDLAND-MAPPING phase 2** — multi-generator (samme spec som ønske #2)
4. **SPEC-CAPTURE-RATE** regresjonstest mot Excel-pivot for alle 11 anlegg

### Avhengig av brukerens input

5. **SCADA-eksport for 6 anlegg** (liavatn, logjen, ogreyfoss, orsdalen,
   stolskraft, vikesa). Når disse drypper inn, kjør samme mapping-mønster.
6. **HRV/LRV/Volum** per dam — drifts-leder skulle sende. Trengs for å
   pre-populere PlantAdmin-data og forbedre tilsig-modellen.
7. **Latitude/Longitude per anlegg** — for Met.no-forecast-integrasjon.

### Mindre forbedringer (lav prioritet)

- Per-måned override for completeness-expectation (permanent regel)
- Cadence-utvidelse (ukentlig SCADA, daglig settlement)
- HotFolder retry-policy (HTTP-uploads → 2-3 retries før karantene)
- ENTSO-E backfill-jobb for capture-rate (eksplisitt deferred i spec)
- Større datakvalitets-banner på Vakt-ROI når overflow-data mangler

### Større arbeider (krever planlegging)

- **SPEC-MULTI-USER-DEPLOYMENT** — Entra ID + multi-tenant (ønske #5)
- **SPEC-HYDROGRID-API** — API-integrasjon med Hydrogrid
- **SPEC-HYDROGRID-PORTEFOLJE-SAMMENLIGNING** — produksjon vs plan på portefølje-nivå

---

## Filstruktur — kjernefiler endret 2026-05-05 + 2026-05-06

### Nye komponenter / sider

```
src/KraftverkUptime.Web/
├── Components/
│   ├── AppBarPlantPeriodSelector.razor       (NY — ønske #1)
│   ├── KpiCard.razor                          (NY — info-tooltip)
│   ├── PlantSignalList.razor                  (NY — signal-katalog UI)
│   └── ProduksjonTimeline.razor               (NY — driftslinje med events)
├── Services/
│   ├── PeriodGranularity.cs                   (NY)
│   ├── PeriodCalculator.cs                    (NY — pure logic)
│   ├── CauseFormatter.cs                      (NY — alias-singleton)
│   └── FilterState.cs                         (utvidet med Granularity + persist)
└── Pages/
    └── 8 sider migrert til FilterState
```

### Ny API + persistens

```
src/KraftverkUptime.Api/Endpoints/
├── CauseAliasEndpoints.cs                     (NY — ønske #3)
├── VaktOverrideEndpoints.cs                   (NY — manuell override)
└── InflowEstimateEndpoints.cs                 (NY — tilsig-modell)

src/KraftverkUptime.Infrastructure/Persistence/
├── PlantTopologySeeder.cs                     (NY — 9 anlegg dam-topologi)
├── GrodemfossSignalMapSeeder.cs               (NY — 20 tags)
├── HonnefossSignalMapSeeder.cs                (NY — 113 tags, self-bootstrapping)
├── DrivdalSignalMapSeeder.cs                  (utvidet 21 → 37 tags)
├── CauseAliasSeeder.cs                        (NY — 21 default aliaser)
└── Entities/
    ├── CauseAliasEntry.cs                     (NY)
    └── VaktEventOverrideEntry.cs              (NY)
```

### Domene-utvidelser

```
src/KraftverkUptime.Modules.Reporting/
├── Nedetid/
│   ├── InflowOverflowEstimator.cs             (NY — pure logic)
│   ├── NedetidQueryService.cs                 (overlay før Classified-rader)
│   └── VaktRoiCalculator.cs                   (overrides-parameter)
├── Portefolje/
│   └── IPortfolioVaktRoiQueryService.cs       (NY — ønske #5)
└── Produksjon/
    └── IProduksjonAnalyseService.cs           (RkPrisNokMwh + HarOverlop + UbalanseKost)

src/KraftverkUptime.Infrastructure/Reporting/
├── PortfolioVaktRoiQueryService.cs            (NY — ønske #5)
└── InflowOverflowQueryService.cs              (NY — tilsig)
```

---

## Datamodell — viktige endringer

### Nye tabeller (idempotent skjema-bro i DatabaseBootstrapper)

| Tabell | Innhold |
|---|---|
| `core.cause_aliases` | Visnings-tekst per cause-kode (operlog:nodstopp → "Nødstopp utløst"). Idempotent backfill av 21 default-koder. |
| `core.vakt_event_overrides` | Drifts-leders manuelle override per (plant_id, event_start_utc): "Auto" / "HaddeOverlop" / "IkkeOverlop". |

### Utvidede tabeller

Ingen — alle utvidelser er på UI/service-laget.

### Idempotente seedere ved hver oppstart

```
DatabaseBootstrapper rekkefølge:
1. EnsureXSchemaAsync (idempotent DDL — utvidet med cause_aliases + vakt_event_overrides)
2. DowntimeCategorySeeder
3. PlantPortfolioSeeder        (11 anlegg)
4. DefaultDamSeeder            (én default-dam per anlegg uten dam fra før)
5. PlantTopologySeeder         (NY — 9 anlegg får riktige dam-navn fra SCADA-skjema)
6. DrivdalSignalMapSeeder      (37 tags)
7. GrodemfossSignalMapSeeder   (NY — 20 tags, kun G2)
8. HonnefossSignalMapSeeder    (NY — 113 tags, self-bootstrapping)
9. HauklandSignalMapSeeder     (195 tags)
10. LindlandSignalMapSeeder    (117 tags)
11. DataImportsBackfillSeeder
12. CauseAliasSeeder           (NY — 21 default-aliaser)
```

---

## Hvordan kjøre appen

```powershell
# Start
.\Start Oppetid.bat
# Bygger Docker images og starter postgres + azurite + api + worker + web
# Åpner http://localhost:5180 automatisk

# Stopp
.\Stopp Oppetid.bat
```

**Etter kode-endringer:** stopp + start (Start Oppetid.bat kjører `docker compose build` automatisk).

**Hard-refresh i browser:** Ctrl+Shift+R (Blazor WASM cacher aggressivt).

---

## Kjørbarhet — sjekkliste for ny sesjon

```powershell
# 1. Verifiser repo-state
cd "C:\Morten\00 Oppetid"
git log --oneline -10
# Skal vise minst: 9798219 docs: oppsett-guide for kontor-server + restart-policy

# 2. Bygg + test
dotnet build --nologo
dotnet test --nologo --no-build
# Skal vise 383/383 grønne (56 + 16 + 199 + 112)

# 3. Start appen
.\Start Oppetid.bat

# 4. Verifiser at nye seedere kjørte
docker exec 00oppetid-postgres-1 psql -U kraftverk -d kraftverk -c "SELECT plant_id, COUNT(*) AS signal_count FROM core.signal_map GROUP BY plant_id ORDER BY plant_id;"
# Skal vise: drivdal=37, grodemfoss=20, haukland=195, honnefoss=113, lindland=117

# 5. Verifiser cause_aliases + vakt_event_overrides
docker exec 00oppetid-postgres-1 psql -U kraftverk -d kraftverk -c "SELECT COUNT(*) FROM core.cause_aliases;"
# Skal vise: 21
docker exec 00oppetid-postgres-1 psql -U kraftverk -d kraftverk -c "\d core.vakt_event_overrides"
# Skal vise tabellen med plant_id + event_start_utc PK
```

---

## Kontekst for ny chat-agent

Brukeren er **Morten Ulland**, drifts-leder for Dalane Kraft (11 vannkraftanlegg
i Norge). Han bruker norsk i tilbakemeldinger. Han verdsetter:

- **Enkelhet over besparelser:** "jeg vil heller at det skal være enkelt enn at jeg sparer litt tid manuelt"
- **Konkrete steg-for-steg-instruksjoner** når noe går galt (særlig Windows-spesifikt — han trenger eksakte kommandoer)
- **Visuell verifikasjon** via UI heller enn å trekke tilbake til CLI
- **Rask iterasjon** uten å være redd for å re-implementere
- **Idempotente endringer** — han kan restarte og forvente samme tilstand
- **Auto mode på under utvikling** — bekrefter raskt og forventer at vi går videre uten for mange spørsmål

11 anlegg:
- drivdal, lindland, haukland, honnefoss (kaskade m/Liavatn-magasin)
- liavatn (separat fra Liavatn-kraftverket — SCADA-tag-prefiks `LIAVT` er Honnefoss-inntak)
- grodemfoss (kun G2 produserer — G1 havarert), ogreyfoss (G1+G2 kaskade m/7 dammer)
- logjen, orsdalen, vikesa, stolskraft

---

## Aktive specs i repo

Levert i denne eller tidligere sesjoner:

- `docs/SPEC-MVP-HARDENING.md` — auth, regresjonstest, datakvalitet, PlantType (2026-05-02)
- `docs/SPEC-IMPORT-COMPLETENESS.md` — completeness-matrise (2026-05-03)
- `docs/SPEC-AUTO-IMPORT-FOLDER.md` — auto-folder (2026-05-03)
- `docs/SPEC-CAPTURE-RATE.md` — kjernemodul ferdig (regresjon mot Excel-pivot ikke kjørt)
- `docs/SPEC-KASKADE-DAMMER.md` — Dam-entity + DamId på SignalMap (levert + utvidet til 9 anlegg topologi)
- `docs/SPEC-VAKT-ROI-OVERLOP.md` + `SPEC-VAKT-ROI-UBALANSE.md` — komponentene er implementert
- `docs/SPEC-LINDLAND-MAPPING.md` — phase 1 ferdig; phase 2 (multi-generator) gjenstår

Klar for implementasjon (ikke startet):

- `docs/SPEC-HONNEFOSS-MAPPING.md` — delvis dekket av HonnefossSignalMapSeeder; REVSVT/NODLANDVT-tilknytning til Liavatn-anlegget gjenstår
- `docs/SPEC-LIAVATN-MAPPING.md` — venter på SCADA-eksport
- `docs/SPEC-MULTI-USER-DEPLOYMENT.md` — Entra ID + multi-tenant (ønske #5 i ny milepæl)
- `docs/SPEC-HYDROGRID-API.md` + `SPEC-HYDROGRID-PORTEFOLJE-SAMMENLIGNING.md`

Ny i denne sesjonen (kunne vært skrevet som specs):

- Tilsig-basert overflow-estimat — implementert direkte uten formell spec
- Vakt-event-override — implementert direkte
- Driftslinje på Produksjon-siden — implementert direkte
- Signal-katalog-UI — implementert direkte

Foreslås som specs i ny chat hvis ønsket:
- **SPEC-INFLOW-OVERFLOW-MODEL** — utvide til primær Vakt-ROI-modell (ønske #1)
- **SPEC-MULTI-GENERATOR** — formell spec for ønske #2

---

## Anbefaling for første handling i ny chat

1. Les denne overlevering grundig
2. Bekreft med bruker at de nye 5 ønskene fortsatt er aktuelle
3. Avklar rekkefølge — sannsynlig start med #1 (gjør tilsig-modell primær i Vakt-ROI)
   siden #1 er en naturlig ferdigstillelse av modellen som allerede ble bygget
4. Hvis bruker har levert SCADA-eksport for et eller flere av de 6
   gjenværende anleggene siden 2026-05-06: kjør mapping per anlegg
   (~30 min hver) som "lavt-hengende frukt" først.

---

*Generert 2026-05-06.*
