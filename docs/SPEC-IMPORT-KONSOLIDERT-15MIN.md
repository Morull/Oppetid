# SPEC: Konsolidert import — én mappe, tre filtyper, alt SCADA-trend på 15 min

**Dato:** 2026-07-02
**Status:** Klar for implementering (Claude Code)
**Avløser/utvider:** NESTE-CHAT-EFFEKTIVITET-15MIN.md (fine-import), SPEC-AUTO-IMPORT-FOLDER.md (hot folder)
**Berører IKKE:** KPI-beregninger, VaktRoiCalculator, EffektivitetQueryService (leser allerede fine)

---

## 1. Bakgrunn

Tre funn fra gjennomgang 2026-07-02:

1. **Fine/hourly-deteksjon er filnavn-basert.** `HotFolderDetector.DetectCsvType`
   (linje 443–482) skiller 15-min fra hourly kun på markørene `15min`, `avg-15min`,
   `_fine` i filnavnet. En 15-min-eksport uten markør defaulter til `ScadaTrends`
   → logges som `source_type="scada"` → completeness-matrisen viser «SCADA trender»
   100 % selv om ingen hourly-import finnes, og 15-min-samples havner i
   `sample_facts` (hourly). Dekning clampes til 1.0 fordi 4 kvarter/time gir
   `uniqueUnits/spanHours ≈ 4` (`ScadaImportService.cs:100–104`).

2. **481 tags importeres, ~37 brukes.** KPI-konsumentene bruker kun rollene i
   `signalliste_scada_minimum.csv`. Resten er tilstandsdata som lagres uten å
   konsumeres (drivdal 37→5, grodemfoss 19→7, honnefoss 113→7, lindland 117→11,
   haukland 195→7).

3. **To trend-oppløsninger gir dobbel eksport og dobbel completeness-bokføring.**
   `"scada-fine"` auto-oppretter expectation via `DbDataImportLogger.EnsureExpectationAsync`
   (linje 80–125), men kan ikke administreres i PlantAdmin (kun settlement/scada/operlog,
   `PlantAdmin.razor:612`, `DataSourceExpectationsEndpoints.cs:174`) og vises rått
   som «scada-fine» i matrisen (`DataImport.razor:1685–1691`).

**Målbilde:** Én importmappe (hot folder, som i dag). Tre filtyper som appen
identifiserer selv på innhold:

| Kilde | Format | Innholds-signatur |
|---|---|---|
| KAIA settlement | .xlsx | Workbook med plant-faner (som i dag) |
| SCADA trend | .csv, **alltid 15-min** | Header `DateTime`/`Cluster1.`, tidsavstand ≤ 15 min |
| SCADA operlog | .csv | Header `Tidsstempel`/`Hendelse`/`Event` |

Hourly-data avledes internt fra 15-min — SCADA trenger bare én trend-eksportjobb.

---

## 2. Endring A — innholdsbasert oppløsnings-deteksjon

**Fil:** `src/KraftverkUptime.Infrastructure/HotFolder/HotFolderDetector.cs` (`DetectCsvType`, linje 431–483)

1. Behold dagens header-sjekker for operlog vs trend (innhold har forrang, som i dag).
2. For trend-CSV: les de første ~200 dataradene, beregn **median tidsavstand**
   mellom påfølgende tidsstempler per signal:
   - ≤ 20 min → `ScadaTrendsFine` (15-min)
   - ellers → `ScadaTrends` (legacy hourly — beholdes for re-import av gamle filer)
3. Filnavn-markørene (`15min`, `_fine`) beholdes kun som fallback når filen har
   for få rader (< 3 tidsstempler) til å måle avstand.
4. Logg valgt oppløsning + målt avstand i `DetectionDiagnostics.Attempts`.

Multi-plant-rutingen (linje 127–147) er uendret — den bygger på samme SourceType.

---

## 3. Endring B — avled hourly fra 15-min ved import

**Fil:** `src/KraftverkUptime.Infrastructure/Scada/ScadaImportService.cs`
(`ImportMasterCsvFineAsync` linje 180–263, `ImportMasterCsvMultiPlantCoreAsync` linje 380–492)

I fine-banene, etter bulk-insert til `sample_facts_fine`:

1. Aggreger samples per **(signal, UTC-time)**: `avg(value)` over kvarterene i timen.
   Timestamp for time-bucketen = timens start (`:00`), konsistent med dagens
   hourly-format.
2. Bulk-insert aggregatene til `sample_facts` via `_sampleRepo` (samme
   batch-størrelse 5000 og samme dedup-/upsert-semantikk som dagens hourly-import —
   **verifiser** hva `BulkInsertAsync` gjør ved eksisterende (plant, signal, time)
   og gjenbruk det).
3. Aggregering skjer på UTC-buckets — DST-overganger (mars/oktober) krever ingen
   særbehandling utover det parseren allerede gjør.

Dermed fungerer alle hourly-konsumenter uendret: `OverflowQueryService`
(OverflowFlow/UpstreamLevel/GeneratorActivePower), `InflowOverflowQueryService`
(ReservoirVolume/TotalDamFlow/ReservoirFillFactor/TurbineWaterFlow), klassifisering
og Vakt-ROI.

---

## 4. Endring C — én SCADA-kilde i completeness

**Filer:** `ScadaImportService.cs`, `HotFolderDetector.cs` (`MapToImportSourceType`
linje 610–613), `DataCompletenessQueryService.cs`, migrasjon.

1. Fine-import logger `data_imports` med `source_type="scada"` (ikke lenger
   `"scada-fine"`). Notes beholder «15-min»-merkingen. Dekning beregnes på kvarter
   som i dag (`uniqueStamps/totalQuarters`) — den er korrekt per definisjon.
2. `MapToImportSourceType`: `ScadaTrendsFine`/`ScadaTrendsFineMultiPlant` → `"scada"`.
3. Defensiv mapping i `DbDataImportLogger.LogAsync`: normaliser innkommende
   `"scada-fine"` → `"scada"` så ingen kodevei kan gjenskape den gamle kilden.
4. **Migrasjon** (EF eller SQL i `DatabaseBootstrapper`):
   - `UPDATE core.data_imports SET source_type='scada' WHERE source_type='scada-fine';`
   - Slett `data_source_expectations`-rader med `source_type='scada-fine'`
     (scada-expectation finnes allerede eller auto-opprettes ved neste import).
   - Samme for eventuelle `data_completeness_overrides` med `scada-fine`.
5. Matrisen viser etter dette tre kolonner: Settlement (KAIA), SCADA trender,
   SCADA alarmer — som matcher de tre filtypene i importmappen.

---

## 5. Endring D — tag-minimering

**Filer:** seederne i `src/KraftverkUptime.Infrastructure/Persistence/`
(`DrivdalSignalMapSeeder`, `GrodemfossSignalMapSeeder`, `HonnefossSignalMapSeeder`,
`LindlandSignalMapSeeder`/`Lindland117TagCatalog`, `HauklandSignalMapSeeder`/
`HauklandTagCatalog`, `DalanePortfolioSignalMapSeeder`), samt import-filtrering.

1. **Aktiv tag-liste = `signalliste_eksport_15min.csv`** (repo-rot, 2026-07-02).
   Dette er fasiten: 80 aktive tags over 10 anlegg — KPI-rollene
   (GeneratorActivePower, TurbineWaterFlow, TurbineEfficiency) per aggregat,
   **OverflowFlow kun på inntakene/terminal-dammene** (beslutning driftsleder
   2026-07-02: Lindland=Rosslandshølen, Hønnefoss=inntak, Haukland=Stemmevatn;
   øvre kaskade-dammer eksporteres ikke), ReservoirVolum/TotalDamFlow/FillFactor
   for tilsigsmodellen, UpstreamLevel (kote) per inntak, og CommunicationAlarm
   per anlegg. **LowestRegulatedLevel-tags (NED_KAP/NEDBKAP) er utelatt**
   (beslutning driftsleder 2026-07-02): rollen konsumeres ikke av noen
   KPI-tjeneste, og LRV er statisk dam-konfig (`DamEntry.LrvMoh` i PlantAdmin) —
   margin mot LRV regnes fra UpstreamLevel-sensoren når klassifikatorens
   LRV-sjekk eventuelt tas i bruk. Merk: uten tags for øvre dammer får
   kaskade-dam-visningen (SPEC-KASKADE-DAMMER) kun inntaksdata — deaktiver
   øvre dam-rader i seed så UI ikke viser tomme dammer.
2. Rader med `status=MANGLER I SCADA-EKSPORT` (kun Stølskraft kom-alarm) krever
   nye tags på SCADA-siden. Ørsdalen trenger INGEN nye tags: overløp løses med
   ProductionStateProxy (pkt. 3), og effektivitetsrollene er bevisst utelatt —
   rent elvekraftverk uten magasin har ingen driftspunkt-optimalisering
   (beslutning driftsleder 2026-07-02). **Liavatn er avklart 2026-07-03**
   (verifisert mot eksporten `export-96-tags-avg-15min-20260703-105826`):
   7 aktive tags i CSV-en, inkl. overløp `LIAVATN_VDAL_INNTAK_NIVA_OVERLOP_VF_PV`.
   Liavatn mangler tag-katalog i koden — legg de 7 tagene inn i seed (utvid
   `DalanePortfolio72TagCatalog` eller egen seeder) med roller fra CSV-en,
   og verifiser at `liavatn_main`-dammen finnes (DefaultDamSeeder).
   Haukland: eksporten bruker `HAUKLAND_STEMMEVT_NIVA_SENSOR_PRI_KOTE_PV`
   (ikke `NIVA_MOH_PV`) — ny signal-map med rolle UpstreamLevel på
   haukland_stemmevt.
3. **Ørsdalen overløp: bytt modus, ikke ny tag.** Anlegget har ~0 inntaksmagasin
   og overløp straks maskinen ikke produserer (minstevannføring 100 l/s berører
   ikke proxyen). Endre `OverflowModeSeeder.cs:60` fra `LevelProxy` til
   `ProductionStateProxy`. Krever kun `ORSDAL_G1_GEN_P_PV`. KAIA-planen
   fungerer allerede som «skulle kjørt»-indikator via `planByHour` i
   `VaktRoiCalculator` — timer med plan=0 gir 0 kr uansett.
4. **Stølskraft: ny modus `OverflowMode.PlanOnly` — overløp telles ikke.**
   Anlegget er vannforsyning til Stavanger og startes kun ved vannbehov i
   forsyningen; overløp styrer ikke driften (beslutning driftsleder 2026-07-02).
   Vakt-ROI skal gi produksjonskreditt for alle counterfactual-timer der KAIA-
   planen har verdi — ikke gate på overløp:
   - Nytt enum-medlem `PlanOnly` i `OverflowMode.cs` med XML-doc.
   - `OverflowQueryService`: ny gren som returnerer ALLE timer i [from, to)
     med `DataAvailable = true` (planByHour i kalkulatoren nullstiller timer
     uten plan, så resultatet blir plan-styrt kreditt).
   - `OverflowModeSeeder.cs:47`: Stølskraft `ProductionStateProxy` → `PlanOnly`.
   - `HasOverflowTagAsync`: `PlanOnly` regnes som «har overflow-detektering»
     (ingen advarsel i UI).
   - Overløpstag/virkningsgrad for Stølskraft utgår permanent — driften styres
     av vannbehov, ikke driftspunkt-optimalisering.
3. Alle andre tags: sett `IsActive=false`, `StoreSamples=false` i seed.
   **Ikke slett** `signal_maps`-rader eller historiske samples — historikk
   og KPI-er bakover i tid skal være uendret.
4. **Import-filtrering:** trend-importen skal droppe samples for signaler som
   ikke har `StoreSamples=true` i `signal_maps` (både kjente-men-deaktiverte og
   helt ukjente tags). Telles og rapporteres i notes
   («N samples droppet, M deaktiverte/ukjente tags»). Dette gjør at en overgangs-
   periode med gamle brede eksporter ikke fyller databasen.
5. Lindland: minimum-CSV-en har to OverflowFlow-mappinger for terminal-dammen
   (rad 27–28, «vurder fjerning av en»). Behold `LINDLAND_INNTAK_NIVA_OVERLOP_VF_PV`,
   deaktiver alt-mappingen — med mindre datakvalitetssjekk viser at den andre er bedre.

**Ny felles SCADA-eksportjobb** (dokumenteres i `SCADA_eksport_referanse.md`):
én 15-min multi-plant master-CSV med de ~45 aktive tagene for alle anlegg
+ eksisterende operlog-eksport. Hourly-eksportene avvikles.

---

## 6. Endring E — opprydding UI/labels

1. `DataImport.razor` `FormatSource`/`SourceShort` (linje 1685–1696): fjern behov
   for scada-fine-case; «SCADA trender» dekker nå 15-min-kilden. Oppdater
   hjelpetekster som sier «Forventet ca. N timer (dager × 24)» til kvarter
   (dager × 96) for scada (linje 1091–1102).
2. `PlantAdmin.razor:805/813`: default lag/threshold for scada uendret
   (5 dager / 0.80), men vurder å heve threshold til 0.95 når eksporten
   blir en fast jobb med komplette filer.
3. Manual (`wwwroot/manual/index.html`): oppdater import-avsnittet til
   «tre filtyper, én mappe».

---

## 7. Ikke i scope

- Endringer i KPI-/Vakt-ROI-/effektivitetsberegninger.
- SPEC-VAKT-ROI-UBALANSE-FULLPERIODE-OG-VISNING.md (egen leveranse).
- Sletting av historiske hourly-data eller re-eksport av gamle måneder på 15-min.

---

## 8. Akseptansekriterier

- [ ] En 15-min master-CSV **uten** filnavn-markør detekteres som fine
      (spacing-måling), skrives til `sample_facts_fine` + aggregert til
      `sample_facts`, og logges som `source_type="scada"`.
- [ ] En gammel hourly master-CSV detekteres fortsatt som hourly og importeres som før.
- [ ] Completeness-matrisen har nøyaktig tre kilder; ingen `scada-fine`-rader
      igjen i `data_imports`/`data_source_expectations` etter migrasjon.
- [ ] «SCADA trender»-cellen kan ikke bli COMPLETE uten at det finnes trend-data
      (fine eller legacy hourly) for måneden.
- [ ] Import av en bred (481-tag) eksportfil lagrer kun aktive tags; notes viser
      antall droppede.
- [ ] Effektivitet-, Nedetid-, VaktRoi- og Produksjon-sidene gir uendrede tall
      for juni 2026 (regresjonssjekk mot dagens database).
- [ ] `drivdal-feb2025-fasit.json`-regresjonstestene er grønne.

## 9. Testplan

1. **Detektor:** enhetstester for spacing-måling — 15-min-fil, hourly-fil,
   fil med 2 rader (fallback til filnavn), multi-plant-varianter.
2. **Aggregering:** 4 kvarter → 1 time avg; time med 1–3 kvarter (delvis) → avg
   av tilgjengelige; DST-døgn mars/oktober (23/25 timer UTC-bucketing).
3. **Idempotens:** re-import av samme fine-fil dobler ikke hourly-aggregatene.
4. **Migrasjon:** seed en `scada-fine`-import + expectation, kjør migrasjon,
   verifiser matrise.
5. **Tag-filter:** fil med deaktiverte + ukjente tags → kun aktive lagres.

## 10. Åpne spørsmål (avklar med driftsleder før/under implementering)

1. Skal aggregerings-funksjonen være `avg` for alle roller? (OverflowFlow og
   nivåer: avg er riktig for m³/s og kote; ingen kjente sum-roller i minimumslisten.)
2. Historiske måneder som kun har hourly: OK som de er, eller re-eksporteres
   noen på 15-min for Effektivitet-analyse bakover i tid?
3. Bekreft Lindland overløps-tag-valget (pkt. 5.5) mot faktisk sensor-kvalitet.
