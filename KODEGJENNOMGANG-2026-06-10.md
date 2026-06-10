# Kodegjennomgang — KraftverkUptime
**Dato:** 2026-06-10
**Omfang:** Hele kodebasen (~34 500 linjer C# i src, ~9 900 linjer tester). Fokus: kodekvalitet/arkitektur og feil/robusthet. Gjennomført med tre parallelle review-agenter; alle KRITISK-funn er deretter verifisert manuelt mot koden.

---

## Helhetsvurdering

Kodebasen er **over gjennomsnittet god** for et prosjekt i denne fasen. Modulariteten er reell (rene kalkulator-funksjoner adskilt fra query-services), dokumentasjonen med spec-referanser er uvanlig sporbar, og testene som finnes tester ekte logikk — ikke happy-path-teater. Hovedproblemene er konsentrert rundt **DST-håndtering**, **samtidighet i hot-folder**, og **arkitektur-selvmotsigelser rundt Worker/jobbkø**.

---

## KRITISK (sannsynlig feil i produksjon)

### 1. DST-tvetydighet i oktober gir korrupte timer — hver oktober-import
- `ExcelSettlementParser.cs` (~507–524): Ved tvetydig lokaltid (02:00 ved DST-slutt) velges **største offset for begge forekomster** → begge rader mapper til samme UTC-time, og 01:00Z mangler. Verifisert: koden velger `o > pick` for alle ambiguous-treff.
- Nedstrøms: `UptimeKpiCalculator` dobbelteller timen, `NedetidQueryService.ListEventsAsync` kaster vilkårlig én rad, kvalitetsrapporten flagger feil time som manglende.
- `ScadaMasterCsvParser.cs` (~305–323): Ambiguous-håndteringen er **død kode** — `GetUtcOffset` kaster aldri for tvetydig tid, så standard-offset (+01, *andre* forekomst) brukes alltid. Settlement og SCADA gir dermed **ulik UTC for samme time** i DST-uka → fusion/overflow-oppslag bommer.
- **Fix:** Sentraliser DST-disambiguering i én funksjon i `Core.Time`, håndter duplisert time eksplisitt (første/andre forekomst-teller).

### 2. Kolonne-misalignment i `ParseSummary` (ExcelSettlementParser ~248–282)
`headerRow.Cells()` returnerer kun brukte celler, men mapping nøkles på listeindeks. Én tom header-celle (dokumentert at kolonne 17 er tom spacer) forskyver hele mappingen → verdier i feil felt. `ParsePlantSheet` gjør det riktig med `cell.Address.ColumnNumber` — bruk samme tilnærming.

### 3. Race condition i HotFolderWatcher (linje 36, 164–172)
`TriggerScanAsync` (fra scan-now/retry-endepunktene) kjører parallelt med polling-loopen. `_seenFiles` er vanlig `Dictionary` uten lås (verifisert) → mulig korrupsjon, og check-then-act gjør at samme fil kan dobbelimporteres; taperen av `File.Move`-kappløpet havner i quarantine med misvisende feil.
**Fix:** `SemaphoreSlim(1,1)` rundt `ScanOnceAsync`.

### 4. Settlement via hot-folder: fil markeres OK før import har skjedd
Watcher POSTer til API som svarer 202 (jobb på in-memory-kø), registrerer dedup-hash og flytter til `done/` umiddelbart. Feiler jobben, eller restarter prosessen, er importen **stille tapt** — fila ligger i `done/` og dedup blokkerer re-import i 14 dager. `JobLoopHostedService` har dessuten **ingen retry** (jobben droppes permanent ved exception, tross navnet `RetryBackoffMs`).
**Fix:** Synkron import (som SCADA-rutene) eller persistent kø + utsett done-flytting til jobben er fullført.

### 5. Worker-arkitekturen er en selvmotsigelse
- `ChannelsJobQueue` er in-memory per prosess. Worker kjører som egen container men **kan aldri motta jobber** API-et legger i sin kø. API-et kjører selv JobLoop — Worker gjør ingenting nyttig.
- `HotFolderWatcher` registreres i delt `AddKraftverkInfrastructure` og `Enabled` defaulter `true` → **begge containere kjører watcher** mot samme mappe: kappløp om filflytting, og Worker-watcheren poster mot `localhost:5080` som ikke finnes der.
- **Fix:** Sett `HotFolder:Enabled=false` i Worker nå; dropp Worker-deploy eller innfør DB-basert kø (`FOR UPDATE SKIP LOCKED`).

### 6. `ReplaceIds` i annoteringer sletter vilkårlige rader (AnnotationsEndpoints ~548–563)
Verifisert: alle id-er i `replaceSet` soft-deletes uten å sjekke at de tilhører `plantId` eller var blant de overlappende. Sletting skjer før create, **uten transaksjon** — feiler create, er gamle rader borte.
**Fix:** Slett kun id-er som finnes i `overlapping`-listen; pakk delete+create i én transaksjon.

### 7. Skjemastyring: ingen vei til fungerende prod-skjema
`EnsureCreatedAsync` + 5 håndskrevne SQL-broer, ingen EF-migrasjoner, skjema definert to ganger (bootstrapper + DbContext). `Program.cs` kjører bootstrapper kun i Development → **i prod kjører ingenting**. Bootstrapper svelger dessuten alle skjema-feil som Warning.
**Fix:** Generer Initial-migrasjon nå (koden sier selv at broene da kan slettes), avklar prod-bootstrap.

### 8. API er reelt uautentisert
Alle policies er `RequireAssertion(_ => true)`, JWT er placeholder. Dokumentert V1-valg, men `DELETE /plants/{id}/data` og admin-endepunkter er åpne — og caddy-proxy i repoet tyder på nettverkseksponering (Tailscale).
**Fix:** Minimum API-nøkkel til Entra ID (SPEC-AUTENTISERING-ENTRA-ID.md) er implementert. Med kun Tailscale-eksponering er risikoen begrenset, men verifiser at API-porten ikke er nådd utenfra.

---

## VIKTIG (bør fikses)

### Beregningsfeil som gir feil tall mot driftsleder
- **VaktRoiCalculator (~258–263):** Av-en-feil i outage-kvantisering — event `[10:30, 11:30)` markerer bare time 10; time 11 krediteres full plan-MWh som "reddet". Systematisk ROI-overestimat for events som ikke slutter på hel time.
- **VaktRoiCalculator (~296):** Overrides slås kun opp på leder-eventets `StartUtc` — override satt på medlems-event ignoreres stille.
- **ProduksjonAnalyseCalculator (~98):** Måned grupperes på UTC, ikke Europe/Oslo — første time i norsk kalendermåned havner i forrige måned. Inkonsistent med `CaptureRateCalculator` som gjør det riktig.
- **UptimeKpiCalculator (~169–196):** Timer med både RK-salg og RK-kjøp bruker hele `AbsUbalansevolumMwh` i begge retninger — dobbelttelling.
- **NedetidQueryService (~230–252):** `GetAvgImbalancePremiumAsync` deduper ikke per `TimeUtc` på tvers av overlappende imports — re-import skjevvekter snittet.
- **AnnotationOverlayService (~79–86):** Kun annoteringer som dekker hele timen har effekt — annotering 10:30–11:30 er stille no-op for brukeren.

### Parsing-robusthet
- `TryGetDouble` (Settlement) og `ParseValue` (SCADA): `Replace(',', '.')` ødelegger tall med tusenskilletegn; uparsebare verdier blir stille `null` uten `ValidationIssue`.
- `OperlogCsvParser (~88):` naiv `Split(';')` uten quoting — semikolon i alarmtekst forskyver kolonnene bak.
- `ScadaMasterCsvParser`: ISO-tid uten sone ("2026-05-01T10:00:00") forkastes stille (faller mellom to parse-stier).
- `HourlyAggregator.DetectGranularity`: ser kun på to første differanser — ett datahull tidlig i 15-min-fil gir kvarter-rader behandlet som timer (×4 KPI-feil).
- `ExcelSettlementParser.ParseAsync` returnerer `all[0]` — multi-plant-fil i single-plant-sti importerer feil anleggs data uten advarsel.

### Infrastructure/Api
- `EfScadaSampleRepository.BulkInsertAsync`: delete+insert via change tracker, titusener tracked entiteter, **ingen transaksjon rundt batchene** — avbrutt import står halvferdig. Bruk `INSERT ... ON CONFLICT DO UPDATE` eller COPY.
- `EconomyReportQueryService`: N+1 mot blob-store (hundrevis av sekvensielle kall for «all»+YTD), og catch-all setter verdier til **0 uten flagg** — økonomirapport kan vise 0 kr i tap pga. en feilet spørring. Eksponer "partial data"-felt.
- `SettlementsEndpoints (~123–141)`: N+1 — deserialiserer hele rapporten per import-rad bare for en bool. Trenger `ExistsAsync`.
- `ResetPlantDataAsync`: sletter **ikke** `sample_facts_fine`, `data_imports`, `vakt_event_overrides`, `data_completeness_overrides`; ingen transaksjon; ingen audit-logg på den mest destruktive operasjonen i API-et.
- `IsHourAligned` (Annotations ~529): validerer uten `ToUniversalTime()` — feil for ikke-UTC-offsets.
- Hardkodet: `Europe/Oslo` i ScadaImportService (tross `plants.time_zone` i DB), `"dev-org"` i watcher, `C:\Morten\...`-sti som default i `HotFolderOptions`.
- Duplisering: `IsMultipart`/`TryGetFileSection`/`SanitizeFileName`/`ResolveOwnerOrgId` kopiert 4–6 steder med divergens. Én helper fjerner ~200 linjer.
- Lagdelingsbrudd: flere endpoints injiserer `KraftverkDbContext` direkte; `MultiPlantSettlementsEndpoints` kaller en *seeder* i request-path.

### Web
- 8 API-metoder i `NedetidApi` med blankt `catch { return null/false; }` — UI kan ikke skille «ikke aktiv» fra «server nede».
- ProblemDetails-body kastes bort overalt (`EnsureSuccessStatusCode`) — brukeren ser "400 Bad Request" i stedet for faktisk valideringsfeil, verst ved filopplasting. `AnnotationsApi.DeleteCategoryAsync` har riktig mønster — generaliser til felles `GetJsonOrThrowAsync<T>`.
- Kultur-rester (~30 forekomster): `F1`/`N0`/`MMM yyyy` uten kultur gir punktum og engelske månedsnavn («May 2026») pga. `InvariantGlobalization=true`. Mekanisk fiks med grep.
- Ingen kansellering av utdaterte kall — raskt anleggsbytte kan vise feil anleggs data.
- Forretningslogikk i store .razor-filer (DataImport 1 936 linjer, VaktRoi 1 355) uten tester — flytt til `Services/` som statiske klasser.
- `NedetidApi` (992 linjer) er gud-klasse for 6 domener — splitt ut `DataStatusApi`.

### Tester
- **Godt dekket og god kvalitet:** VaktRoiCalculator (1 094 linjer, håndregnet fasit), Settlement-parser (ekte xlsx-fiksturer, regresjonsvern), økonomi-beregninger, klassifisering, helligdager/vaktmodell.
- **Hull:** `ScadaImportService` (612 linjer) utestet; API-laget (~6 500 linjer) har én testfil — ingen `WebApplicationFactory`-tester, destruktive endepunkter utestet; Web har null tester; `EndToEnd.Tests` er feilnavngitt (inneholder enhetstester); DB-tester bruker EF InMemory — Testcontainers-Postgres ville verifisert SQL-oversettelse og bootstrapper.

---

## MINDRE (utvalg)

- `FloorToHour` duplisert 6+ steder; `EffektivitetEpisodeService.ToHourBucket` er avvikende variant.
- `EffektivitetEpisodeService`: aggregeringsterskel hardkodet −2.0 mens episoder bruker konfigurert terskel — innbyrdes inkonsistens hvis terskel endres.
- Død konfig: `SustainedStopHours`, `MarginalCostNokMwh`, `VaktResponstid` definert men aldri brukt.
- `DataCompletenessQueryService`: `DaysOverdue` ignorerer `ExpectedLagDays`; `Pending` alltid 0 (død kode).
- `DateTimeOffset.MinValue` som sentinel lekker ut flere steder.
- `DataImport.razor:1747`: `IdempotencyKey[..16]` kaster ved kort nøkkel.
- Audit-logging etter mutasjon uten transaksjon — feilet audit gir 500 selv om endringen er lagret.

---

## Det som er bra

- Modulær arkitektur med rene, testbare kalkulator-funksjoner adskilt fra query-services.
- Spec-referanser med dato i doc-kommentarer gjennomgående — sporbarhet til driftsbeslutninger er uvanlig god.
- `VaktRoiCalculator` og `SettlementClassifier` er forbilledlig dokumentert (monoton-invariant-resonnement, prioriterte regler).
- ReDoS-bevissthet: regex med timeout og bundne kvantorer (lærdom fra reell prod-hendelse).
- Dedup-design i hot-folder (peek-før-import, registrer-etter-suksess) og `DetectionDiagnostics` med `.diag.json` i karantene.
- Konsekvent `ConfigureAwait(false)`, `AsNoTracking`, CancellationToken-propagering, gjennomtenkte indekser.
- `NumberFormat.Norsk` sentralisert og godt begrunnet; konsekvent ProblemDetails med norske feilmeldinger.
- Overlapp-protokollen for annoteringer (409 + replaceIds) er godt API-design — kun valideringshullet skjemmer.

---

## Prioritert handlingsplan

| # | Tiltak | Innsats | Risiko ved å vente |
|---|--------|---------|---------------------|
| 1 | DST-fix: sentralisert disambiguering (Excel + SCADA-parser) | Middels | Korrupte data hver oktober; SCADA/settlement-mismatch i DST-uka |
| 2 | `SemaphoreSlim` rundt hot-folder-scan + valider `ReplaceIds` | Liten | Dobbeltimport / vilkårlig sletting |
| 3 | Settlement hot-folder: synkron import eller utsatt done-flytting + jobb-retry | Middels | Stille tapte importer |
| 4 | Worker: `HotFolder:Enabled=false` + avklar kø-arkitektur | Liten | Kappløp om filer ved Worker-deploy |
| 5 | Generer Initial EF-migrasjon, avklar prod-bootstrap | Middels | Ingen vei til prod-skjema |
| 6 | `ParseSummary` kolonne-indeksering | Liten | Verdier i feil felt |
| 7 | VaktRoi outage-kvantisering + UTC-måned i Produksjon | Liten | Feil tall mot driftsleder |
| 8 | Web: felles HTTP-hjelper med ProblemDetails-lesing | Liten | Ubrukelige feilmeldinger |
| 9 | Tester: ScadaImportService + API-integrasjon (Testcontainers) | Stor | Regresjoner i udekket import-/API-lag |
| 10 | Kultur-rester i Web (~30 forekomster, mekanisk) | Liten | Kosmetisk |

Punkt 1–7 egner seg godt som Claude Code-oppgaver med denne rapporten som spec.
