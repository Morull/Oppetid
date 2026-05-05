# Overlevering: neste milepæl — UX-konsolidering + redigering på tvers + Vakt-ROI-dashboard

**Dato:** 2026-05-05
**Status:** Levert. Kjørbar via `Start Oppetid.bat`. Test-status: 383/383 grønne.
**Hovedendring:** SPEC-LINDLAND-MAPPING phase 1 + Vakt-ROI dedup + branding + persistent blob-storage.

---

## TL;DR for ny chat-sesjon

1. Brukeren (Morten Ulland, drifts-leder Dalane Kraft, 11 anlegg) kjører appen via `Start Oppetid.bat` (Docker Compose). Hovedside: http://localhost:5180.
2. **5 nye ønsker fra brukeren** står i seksjon "Brukerens prioriterte ønsker" — disse er hovedfokus for ny chat.
3. **For overflow-tag-mapping** mangler 8 av 11 anlegg fortsatt. Drivdal/Haukland/Lindland er pre-seedet. Brukeren skal sende tag-listen.
4. **For logo**: bruker må manuelt laste ned PNG fra dalane-kraft.no og legge i `wwwroot/img/dalane-kraft-logo.png` (instruks i [LOGO-INSTALL.md](src/KraftverkUptime.Web/wwwroot/img/LOGO-INSTALL.md)). Tekst-fallback fungerer hvis ikke.
5. **Etter restart**: bruker må trykke "Re-importer alt"-knappen på `/data-import` siden blob-storage var nullstilt — etter dette regenereres rapporter for alle 16 imports.

---

## Brukerens prioriterte ønsker (hovedfokus for neste sesjon)

Drifts-leder ba om disse i 2026-05-05-sesjonen. Ranger etter intuisjon
om verdi vs implementasjons-kost:

### 1. **Globalt anlegg + periode-velger i topp-baren**

**Problem:** I dag må bruker navigere til hvert anleggs egen URL og velge
periode på hver side. For en operativ bruker som vil sjekke "samme
periode for ulike anlegg" eller "ulike måneder for samme anlegg" er
dette friksjon.

**Ønske:**
- **Anlegg-dropdown** i topp-baren ved siden av logo. Valgt anlegg gjelder
  for ALLE sider som tar PlantId som parameter (Nedetid, Rapport,
  Vakt-ROI, Capture-rate, Produksjon, Effektivitet, PlantAdmin).
- **Periode-pil** "← forrige | neste →" som flytter periode med ett
  trinn av valgt granularitet.
- **Default-periode**: forrige måned (gjeldende minus 1).
- **Hurtigknapper**: "Måned" (default), "Hittil i år", evt. "År", "Kvartal".

**Teknisk plan:**
- Ny `IPlantPeriodContext`-tjeneste (Singleton i WASM-prosessen) som holder
  `(PlantId, FromUtc, ToUtc, Granularity)`. Lokal-storage-persistert per bruker.
- `MainLayout.razor` rendrer kontroll i `MudAppBar`. CascadingValue til alle
  sider.
- Hver `@page`-side abonnerer på endringer og re-loader når context endres.
- URL-parametere (eks. `/nedetid/drivdal`) overstyrer context når satt
  eksplisitt, men context oppdateres ved navigering.

**Estimat:** 1-2 dager (kontroll + state-service + integrasjons-arbeid på 7 sider).

---

### 2. **Redigerbare hendelser i Nedetid-siden + sync med Rapport**

**Problem:** I dag kan brukeren redigere klassifiserte hendelser via Rapport
(annoteringer overstyrer klassifikator-output). Nedetid-siden viser
samme hendelser men er read-only. Når en endring gjøres på én side
forventes det at den synkroniseres til den andre.

**Ønske:**
- **Edit-knapp** per hendelse i Nedetid-tabellen som åpner samme dialog
  som i Rapport.
- **Sync**: endringer i annoteringer reflekteres umiddelbart i begge sider
  (samme datakilde — `IDowntimeAnnotationRepository`).

**Teknisk plan:**
- Eksisterende `AnnotationOverlayService` brukes allerede av Rapport.
  Nedetid-siden henter events via `INedetidQueryService.ListEventsAsync`
  som kanskje IKKE går gjennom annotation-overlay. Sjekk og rett.
- UI: gjenbruk `AnnotationDialog`-komponenten fra `ReportDetail.razor`.
  Trekk ut til delt komponent `Components/AnnotationEditDialog.razor`.
- Ved lagring: refresh begge siders datakilder (cache-invalidering).

**Estimat:** 4-6 timer.

**Bivirkning å sjekke:** påvirker også Vakt-ROI (som leser samme events
via NedetidQueryService) og Effektivitet-siden. Tester må verifisere at
en annotation som flytter et event fra "TripFeil" til "PlanlagtVedlikehold"
gir oppdatert Vakt-ROI ved neste request.

---

### 3. **Editerbar cause-tekst i Kategorier-siden**

**Problem:** Når en hendelse vises i Nedetid/Rapport står cause-koden som
intern streng (eks. `operlog:nodstopp`). Drifts-leder vil endre vist
tekst (eks. "Nødstopp utløst") uten å endre koden bak.

**Ønske:**
- **Tabell over cause-aliaser** på `/admin/kategorier`-siden med kolonnene:
  intern kode | brukervennlig tekst | kategori
- Når en hendelse vises, brukes alias-teksten hvis den finnes; ellers
  fall-back til intern kode.

**Teknisk plan:**
- Ny tabell `core.cause_aliases (cause_code, display_text, owner_org_id)`.
- Idempotent backfill: pre-populer kjente koder
  (`operlog:nodstopp` → "Nødstopp", `operlog:fault` → "Feil/havari", etc.)
- API: `GET/PUT /api/v1/admin/cause-aliases`.
- UI: utvide `Kategorier.razor` med ny seksjon. Reuse-mønster fra
  `DowntimeCategoryEntry`.
- Web: lookup-funksjon `CauseFormatter.Display(causeCode)` brukt i alle
  rapport/nedetid-tabeller.

**Estimat:** 4-5 timer.

---

### 4. **Konsolider terminologi: "Tilstand" vs "Cause"**

**Problem:** I Rapport står det "Tilstand" (UnitState). I Nedetid står det
muligens samme, men i andre tabeller bruker vi "Cause" (cause_code).
Drifts-leder synes dette er forvirrende.

**Diagnose først:** Tilstand og Cause er FORSKJELLIGE konsepter:
- **Tilstand** (UnitState) = _hva_ enheten er i akkurat nå (InService,
  ForcedOutage, MaintenanceOutage, InformationUnavailable, ...)
- **Cause** (CauseCode) = _hvorfor_ den er i den tilstanden
  (operlog:nodstopp, scada:com_alarm, manual:annotert, ...)

De henger sammen men er ikke synonymer.

**Anbefaling:**
- BEHOLD den tekniske distinksjonen — den er nyttig for analyse.
- ENDRE etiketter for å gjøre forskjellen klar:
  - "Tilstand" → "Driftstilstand" (tydeligere at det er enhet-state)
  - "Cause" → "Årsak" (norsk, og inkluder en tooltip som forklarer)
- I tabeller hvor begge vises sammen, sett dem ved siden av hverandre
  med klar overskrift.
- Egen seksjon i header eller hovedside som forklarer modellen
  (kanskje i `/admin/kategorier`).

**Spør brukeren** først om de vil ha distinksjonen forklart, eller om
de heller vil at vi kollapser til ett felt og bruker "best effort" — men
det taper analyse-presisjon.

**Estimat:** 2-3 timer for omdøping + 30 min for forklarings-tooltip.

---

### 5. **Vakt-ROI portefølje-dashboard**

**Problem:** I dag er `/vakt-roi/{plantId}` per-anlegg. Drifts-leder vil
ha en oversikt på tvers — "topp 10 viktigste hendelser for siste måned"
+ "total reddet for hele porteføljen".

**Ønske:**
- **`/vakt-roi`-side** (uten plantId i URL) viser dashboard:
  - **Topp 5-10 hendelser** sortert på reddet beløp (NOK)
  - **Total reddet** — sum NOK på tvers av alle 11 anlegg
  - **Grafer**: månedlig totalbeløp over siste 12 mnd, fordelt på cause-type
  - **Klikkbar tabell**: klikk hendelse → naviger til per-anlegg detaljer

**Teknisk plan:**
- Ny endpoint `GET /api/v1/portfolio/vakt-roi?from=&to=` som aggregerer
  alle plants. Bruker `VaktRoiCalculator` per plant og samler resultatene.
- Caching: per-plant-resultater kan caches 5 min siden de er deterministiske
  for en gitt periode.
- UI: `VaktRoi.razor` får dual-modus (med eller uten PlantId-parameter).
  - Uten plantId: dashboard
  - Med plantId: eksisterende detaljvisning
- Reused med periode-velger fra ønske #1 — naturlig integrasjons-punkt.

**Estimat:** 1-1.5 dager.

---

## Det som er bygget i denne sesjonen (2026-05-04 + 2026-05-05)

### Phase 1 — Hot-folder/import-fixes (2026-05-04)

| Commit | Beskrivelse |
|---|---|
| `6512f9a` | fix(hot-folder): detector støtter KAIA-strippede sheet-navn + diag-trase |
| `daf3a20` | feat(hot-folder): content-hash dedup + persistert diag-fil |
| `a2081b2` | feat(data-import): diagnose-modal + duplikat-tabell + fargete matrise-celler |
| `c423c5b` | feat(data-import): klikkbar celle viser hva som mangler i delvis import |
| `167476c` | fix(data-completeness): operlog er hendelse-basert, ikke tids-overlap |
| `f2a9b70` | feat(data-completeness): manuell overstyring av celle-status |
| `d834f92` | fix(operlog): logg data_imports basert på rå rader, ikke bare state-events |
| `df3de70` | feat(operlog): MapEvent fanger NODSTOPP/HURTIGSTOPP/HH/LL og alarmType=alarm |
| `30c7e8c` | feat(settlement): persistere issues til data_imports.notes + ukjente kolonner |
| `c2fa907` | feat(scada): skip-telemetri og ukjent-prefiks-deteksjon i master-CSV |
| `2d6270a` | feat(dams): default-dam-seeder + dam CRUD + overflow-tag-mapping i UI |
| `022ed76` | fix(vakt-roi): grupper events i samme vakt-vindu — ikke dobbelt-tell |
| `5e296ef` | feat(branding): Dalane Kraft farge-palett + logo-plass i AppBar |
| `3ebfdf3` | fix(blob-storage): persistent azurite volume + recovery-endpoint |

### Phase 2 — Lindland mapping (2026-05-05)

| Commit | Beskrivelse |
|---|---|
| `bfa2f0d` | feat(lindland): seeder for 4-dam-kaskade + 117-tag signal-map (Phase 1) |

### Bekreftede atferder via test-låsing

- **Vakt-ROI**: event som starter i arbeidstid (08-15 hverdag) gir aldri ROI
  selv om varer inn i vakt-vinduet (drifts-leders 2026-05-05-presisering).
  Test: `Trip_Starter_I_Arbeidstid_Varer_Inn_I_Vakt_Vindu_Gir_Ingen_ROI`.

---

## Status: pending arbeid på tvers av specs

### Klare for implementasjon

1. **Brukerens 5 ønsker** over (toppprioritet)
2. **SPEC-HONNEFOSS-MAPPING** — 4-dam kaskade + REVSVT/NODLANDVT-flytting
   til Liavatn-kraftverk-anlegget (samme mønster som Lindland)
3. **SPEC-LIAVATN-MAPPING** — opprett som separat plant (har dam-en, ikke
   selve kraftverket). Venter på dedikert SCADA-eksport.
4. **SPEC-CAPTURE-RATE** regresjonstest mot Excel-pivot for alle 11 anlegg
   (akseptansekriterium #5 fra spec). Kjernemodul er ferdig.
5. **SPEC-LINDLAND-MAPPING phase 2** — Generator-entity + multi-generator-
   KPI-aggregering (G1 vs G2 separat). Phase 1 gjort denne sesjonen.

### Avhengig av brukerens input

6. **Overflow-tag-liste** for 8 anlegg som ikke har dedikert seeder ennå:
   honnefoss, liavatn, grodemfoss, ogreyfoss, orsdalen, vikesa, stolskraft,
   logjen. Format: `<plantId>: <SCADA_TAG_FOR_OVERLOP_M3S>`.
7. **HRV/LRV/Volum + bilde av kaskade-struktur** per anlegg — drifts-leder
   skulle sende. Trengs for å pre-populere PlantAdmin-data og evt. lage
   visualisering av kaskade-flyt.
8. **Dalane Kraft logo PNG** — manuell nedlasting og plassering i
   `wwwroot/img/dalane-kraft-logo.png`. Tekst-fallback vises ellers.

### Mindre forbedringer (lav prioritet)

- Per-måned override for expectation (permanent regel — vi har manuell
  celle-override per nå)
- Cadence-utvidelse (ukentlig SCADA, daglig settlement) — DB-feltet finnes
- SLA-eskalering (OVERDUE > 14 dager → annet visuelt varsel)
- Real-time SignalR (banneren polled i dag)
- HotFolder retry-policy (HTTP-uploads → 2-3 retries før karantene)
- ENTSO-E backfill-jobb for capture-rate (eksplisitt deferred i spec)

### Større arbeider (krever planlegging)

- **SPEC-MULTI-USER-DEPLOYMENT** — Entra ID auth + multi-tenant
- **SPEC-HYDROGRID-API** — API-integrasjon med Hydrogrid
- **SPEC-HYDROGRID-PORTEFOLJE-SAMMENLIGNING** — produksjon vs plan på portefølje-nivå

---

## Filstruktur — kjernefiler endret 2026-05-04 + 2026-05-05

### Hot-folder + auto-import

```
src/KraftverkUptime.Infrastructure/HotFolder/
├── HotFolderDetector.cs              (R1-fallback for KAIA-strippede sheet-navn)
├── HotFolderDedupCache.cs            (NY: SHA-256 dedup + JSON-persist)
├── HotFolderWatcher.cs               (dedup + diag.json + duplicates/-mappe)
├── HotFolderOptions.cs               (DedupRetentionDays, DuplicatesFolderName)
└── HotFolderQueue.cs                 (uendret)

src/KraftverkUptime.Api/Endpoints/
├── HotFolderEndpoints.cs             (diagnose-endpoint + reimport-done)
├── DamsEndpoints.cs                  (POST + DELETE for kaskade-CRUD)
└── SignalMapsEndpoints.cs            (NY: tag-mapping + scada-tags-list)

src/KraftverkUptime.Web/Pages/DataImport.razor
├── "Re-importer alt"-knapp           (recovery etter blob-restart)
├── Diagnose-modal for karantene-filer
├── Duplikat-tabell
└── Klikkbare matrise-celler med detalj-modal
```

### Settlement + SCADA

```
src/KraftverkUptime.Modules.Settlement/Quality/
└── SettlementImportNotesBuilder.cs   (NY: persistere issues til notes)

src/KraftverkUptime.Modules.Scada/Import/
├── OperlogCsvParser.cs               (rå rad-stats + utvidet MapEvent)
└── ScadaMasterCsvParser.cs           (uendret)

src/KraftverkUptime.Infrastructure/Scada/
└── ScadaImportService.cs             (skip-telemetri + ukjent-prefiks-deteksjon)
```

### Dams + Lindland

```
src/KraftverkUptime.Infrastructure/Persistence/
├── DefaultDamSeeder.cs               (NY: hver plant får én default terminal-dam)
├── LindlandSignalMapSeeder.cs        (NY: 4-dam-kaskade + 117 tags)
├── Lindland117TagCatalog.cs          (NY: frosset tag-liste)
├── DbDamRepository.cs                (DeleteAsync)
├── DatabaseBootstrapper.cs           (registrere DefaultDamSeeder + LindlandSeeder)
└── HauklandSignalMapSeeder.cs        (uendret — eksisterende referanse-mønster)

src/KraftverkUptime.Web/Pages/PlantAdmin.razor
├── Dam CRUD (legg til, slett, edit)
└── Overflow-tag-mapping seksjon (auto-complete dropdown for SCADA-tags)
```

### Vakt-ROI

```
src/KraftverkUptime.Modules.Reporting/Nedetid/
└── VaktRoiCalculator.cs              (group-basert dedup for events i samme vakt-vindu)

tests/KraftverkUptime.Infrastructure.Tests/Nedetid/VaktRoiCalculatorTests.cs
└── 5 nye tester: dedup-grupper + arbeidstid-regel
```

### Branding

```
src/KraftverkUptime.Web/
├── Theming/DalaneTheme.cs            (Dalane Kraft farge-palett verifisert mot logo)
├── Layout/MainLayout.razor           (logo + tekst-fallback i AppBar)
└── wwwroot/
    ├── css/app.css                   (.dk-brand* CSS for fallback-heksagon)
    └── img/LOGO-INSTALL.md           (NY: instruks for manuell logo-nedlasting)
```

### Capture-rate (allerede ferdig før denne sesjonen — bare bekreftet)

```
src/KraftverkUptime.Modules.Reporting/CaptureRate/
├── CaptureRateCalculator.cs          (12 unit-tester grønne)
└── ICaptureRateQueryService.cs

src/KraftverkUptime.Infrastructure/Reporting/CaptureRateQueryService.cs
src/KraftverkUptime.Infrastructure/Events/MarketPriceUpsertHandler.cs
src/KraftverkUptime.Infrastructure/Persistence/Entities/MarketPriceEntry.cs
src/KraftverkUptime.Api/Endpoints/CaptureRateEndpoints.cs
src/KraftverkUptime.Web/Pages/CaptureRate.razor
```

---

## Datamodell — viktige endringer

### Nye tabeller

| Tabell | Innhold |
|---|---|
| `core.dams` | Per-anlegg dam-katalog. Hvert anlegg har én rad med `is_turbine_intake = true`. |
| `core.data_completeness_overrides` | Manuelle celle-overstyringer (drifts-leder verifisert). |
| `core.market_prices` | Spotpris per (price_area, time_utc). UPSERT fra settlement-imports. |

### Utvidede tabeller

| Tabell | Endring |
|---|---|
| `core.signal_map` | `dam_id` (nullable, FK til dams) — kaskade-modell |
| `core.data_imports` | `notes` brukes nå for å persistere settlement-issues + SCADA-skip-telemetri |
| `core.data_source_expectations` | `completion_threshold_pct` (default 0.95 for settlement, 0.80 for scada) |

### Idempotente seederer som kjøres ved hver oppstart

```
DatabaseBootstrapper rekkefølge:
1. EnsureXSchemaAsync (idempotent DDL)
2. DowntimeCategorySeeder
3. PlantPortfolioSeeder      (11 anlegg)
4. DefaultDamSeeder          (NY — én default-dam per anlegg uten dam)
5. DrivdalSignalMapSeeder    (22 tags)
6. HauklandSignalMapSeeder   (195 tags + 4-dam-kaskade)
7. LindlandSignalMapSeeder   (NY — 117 tags + 4-dam-kaskade)
8. DataImportsBackfillSeeder
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

**Etter blob-storage-fix denne sesjonen:** trykk "Re-importer alt"-knappen
på `/data-import` for å regenerere rapporter for eksisterende imports.

---

## Kjørbarhet — sjekkliste for ny sesjon

```powershell
# 1. Verifiser repo-state
cd "C:\Morten\00 Oppetid"
git log --oneline -5
# Skal vise bfa2f0d som siste commit

# 2. Bygg + test
dotnet build --nologo
dotnet test --nologo --no-build
# Skal vise 383/383 grønne

# 3. Start appen
.\Start Oppetid.bat

# 4. Verifiser at Lindland-seeder kjørte
docker exec 00oppetid-postgres-1 psql -U kraftverk -d kraftverk -c "SELECT plant_id, dam_id, name, is_turbine_intake FROM core.dams WHERE plant_id='lindland' ORDER BY cascade_position;"
# Skal returnere: heigravatn (1), eiavatn (2), barstadvatn (3), rosslandshølen (4, true)

# 5. Verifiser overflow-tag-mapping
docker exec 00oppetid-postgres-1 psql -U kraftverk -d kraftverk -c "SELECT plant_id, signal_id, dam_id FROM core.signal_map WHERE role='OverflowFlow' ORDER BY plant_id;"
# Skal vise minst 3 rader: drivdal, haukland (Stemmevatn), lindland (Rosslandshølen)
```

---

## Kontekst for ny chat-agent

Brukeren er **Morten Ulland**, drifts-leder for Dalane Kraft (11 vannkraftanlegg
i Norge). Han bruker norsk i tilbakemeldinger. Han verdsetter:

- **Enkelhet over besparelser:** "jeg vil heller at det skal være enkelt enn at jeg sparer litt tid manuelt"
- **Konkrete steg-for-steg-instruksjoner** når noe går galt
- **Visuell verifikasjon** via UI heller enn å trekke tilbake til CLI
- **Rask iterasjon** uten å være redd for å re-implementere
- **Idempotente endringer** — han kan restarte og forvente samme tilstand

11 anlegg:
- drivdal, lindland, haukland, honnefoss (kaskade m/Kydland + Spjodevatn-magasin)
- liavatn (separat fra Liavatn-kraftverket — SCADA-tag-prefiks `LIAVT` er Honnefoss-inntak)
- grodemfoss, ogreyfoss, logjen, orsdalen, vikesa, stolskraft

PlantType per anlegg er bekreftet 2026-05-02:
- Regulated: drivdal, haukland, honnefoss, liavatn, ogreyfoss, logjen, grodemfoss
- RunOfRiver: lindland (kaskade m/24t-lag), orsdalen
- Mixed: vikesa (lite magasin), stolskraft (vannforbruks-styrt)

---

## Aktive specs i repo

Levert i denne eller tidligere sesjoner:

- `docs/SPEC-MVP-HARDENING.md` — auth, regresjonstest, datakvalitet, PlantType (2026-05-02)
- `docs/SPEC-IMPORT-COMPLETENESS.md` — completeness-matrise (2026-05-03)
- `docs/SPEC-AUTO-IMPORT-FOLDER.md` — auto-folder (2026-05-03 lettvektsversjon)
- `docs/SPEC-CAPTURE-RATE.md` — kjernemodul ferdig 2026-05-05 (regresjon mot Excel-pivot ikke kjørt)
- `docs/SPEC-KASKADE-DAMMER.md` — Dam-entity + DamId på SignalMap (delvis levert: dam-CRUD + tag-mapping fra UI er på plass)
- `docs/SPEC-VAKT-ROI-OVERLOP.md` + `SPEC-VAKT-ROI-UBALANSE.md` — komponentene er implementert
- `docs/SPEC-LINDLAND-MAPPING.md` — phase 1 levert 2026-05-05; phase 2 (multi-generator) gjenstår

Klar for implementasjon (ikke startet):

- `docs/SPEC-HONNEFOSS-MAPPING.md` — oppdatert kaskade-modell, 4 dammer
- `docs/SPEC-LIAVATN-MAPPING.md` — nytt anlegg, venter på dedikert SCADA-eksport
- `docs/SPEC-MULTI-USER-DEPLOYMENT.md` — Entra ID + multi-tenant
- `docs/SPEC-HYDROGRID-API.md` + `SPEC-HYDROGRID-PORTEFOLJE-SAMMENLIGNING.md`

Ny i denne sesjonen (foreslås som specs i ny chat hvis ønsket):

- **SPEC-GLOBAL-PERIODE-VELGER** — brukerens ønske #1 over
- **SPEC-NEDETID-EDITERING** — brukerens ønske #2
- **SPEC-CAUSE-ALIASER** — brukerens ønske #3
- **SPEC-VAKT-ROI-DASHBOARD** — brukerens ønske #5

---

## Anbefaling for første handling i ny chat

1. Les denne overlevering grundig
2. Bekreft med bruker at de fortsatt prioriterer ønske #1-5 over de andre specs
3. Avklar rekkefølge: sannsynligvis #1 (anlegg+periode-velger) først siden den gir
   stor UX-gevinst og åpner for #5 (Vakt-ROI-dashboard som bruker samme periode-tilstand)
4. Lag ny spec-fil(er) hvis ønsket — `docs/SPEC-GLOBAL-PERIODE-VELGER.md` osv.
5. Implementer i sub-trinn: state-service → MainLayout-kontroll → integrasjons-arbeid
   per side. Hver phase bør være kjørbar og testbar isolert.
