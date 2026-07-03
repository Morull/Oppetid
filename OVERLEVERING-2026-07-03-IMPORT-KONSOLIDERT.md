# OVERLEVERING: SPEC-IMPORT-KONSOLIDERT-15MIN (2026-07-03)

Alle endringene A–E er implementert. Dette dokumentet noterer valg gjort der
spec-en ga rom, avvik fra spec-teksten, og rest-punkter for driftsleder.

## Valg per spec-ens åpne spørsmål (§10)

1. **Aggregeringsfunksjon:** `avg` for alle roller (spec-ens egen anbefaling —
   ingen sum-roller i fasiten). Timer der alle kvarter mangler verdi får
   `null` + quality=2; delvise timer snittes over tilgjengelige kvarter.
2. **Historiske hourly-måneder:** beholdt som de er (spec-ens anbefaling).
   Legacy hourly-filer detekteres fortsatt og importeres som før.
3. **Lindland overløps-tag (pkt. 5.5):** Datakvalitetssjekken spec-en ba om
   SLO TIL: fasit-taggen `LINDLAND_INNTAK_NIVA_OVERLOP_VF_PV` er **konstant
   0,00** over hele perioden (des 2025–jun 2026, 6 410 samples — død eller
   feilkalibrert sensor), mens alt-taggen `LINDLAND_INNTAK_KONTROLL_MAG_OVLOP_PV`
   har **243 timer med reelt spill** (maks 17,2 m³/s). → **Begge beholdt
   aktive**: alt-taggen bærer historikken (deaktivering ville slettet
   Lindlands overløp fra Vakt-ROI), fasit-taggen er den eksporten sender.
   **Driftsleder bør: (a) sjekke/kalibrere NIVA_OVERLOP_VF-sensoren, eller
   (b) bytte eksport-taggen til KONTROLL_MAG_OVLOP.**

## Avvik fra spec-teksten (begrunnet)

1. **KPI-bevarende unntak i aktiv-listen.** Rolle-oppslagene
   (`GetSignalIdForRoleAsync`/`GetByPlantDamAndRoleAsync`) filtrerer på
   `IsActive` og velger alfabetisk først. Å deaktivere dagens «vinner» på en
   terminal-dam ville byttet signal for HISTORISKE spørringer og brutt
   akseptansekriteriet om uendrede KPI-tall. Tre tags utover fasiten er derfor
   holdt aktive: Lindland alt-overløp (over), `GRODEM_SMIEVT_NIVA_SENSOR_PRI_HRV_PV`
   og `HAUKLAND_INNTAK_NIVA_OPPSTROM_KOTE_PV` (UpstreamLevel-duplikater på
   terminal-dammene). Øvre-dam-tags er trygt deaktivert (KPI-spørringene er
   dam-skopet mot terminal).
2. **Tag-minimeringen anvendes ÉN gang** (markør `aktiv-tagliste-2026-07-02`
   i ny tabell `core.seed_markers`), ikke ved hver oppstart — ellers ville
   driftsleders senere manuelle re-aktivering av en tag blitt overskrevet ved
   neste boot. Plant-seederne er IKKE omskrevet; de seeder ferske databaser
   som før, og aktiv-listen anvendes etterpå (samme boot). Ny fasit-versjon →
   nytt markør-navn.
3. **Liavatn (fasit-status AVKLARES):** utfylt fra faktiske tags i
   `sample_facts` (51 tags totalt). 9 KPI-tags seedet inn i `signal_map`
   (anlegget manglet tag-katalog): GEN_P, TURB_VF, VDAL-overløp, magasin
   volum/fyllgrad/LRV, oppstrøms nivå, kom-alarm, utløps-nivå.
   **`TurbineEfficiency` og `TotalDamFlow` FINNES IKKE i Liavatn-eksporten**
   — må evt. opprettes i SCADA (som Stølskraft-manglene).
4. **Dam-deaktivering:** implementert som ny `is_active`-kolonne på
   `core.dams` + engangs-deaktivering av ALLE ikke-terminal-dammer (Lindland,
   Haukland, Hønnefoss, Øgreyfoss-kaskadene). PlantAdmin viser «Deaktivert»-
   chip og demper raden; ingen re-aktiverings-toggle i UI (gjøres via API/DB).
5. **Coverage/notes beregnes nå på LAGREDE samples** (etter tag-filteret),
   ikke rå parse-resultat — det er de lagrede dataene matrisen skal beskrive.
6. **PlantAdmin scada-threshold beholdt 0,80** (spec E.2: vurder 0,95 når
   eksporten blir fast jobb — ikke gjort nå).
7. **Detektor-følgefiks:** content-sniff-fallbacken for plant-id dekker nå
   også `ScadaTrendsFine` — nødvendig når fine-filer ikke lenger har markør
   i filnavnet (før: fine-fil uten plant i navnet → karantene).
8. **HotFolderOptions OGREY1/OGREY2-prefiks** lå ukommittert i arbeidstreet
   fra før (G1/G2-generator-sidene i ny eksport) — inkludert i leveransen.
9. **Migrasjonstest (§9.4) ikke automatisert** — scada-fine→scada-migrasjonen
   er idempotent bootstrapper-SQL etter samme mønster som hydrogrid_plan-
   oppryddingen; verifisert live ved deploy i stedet (matrise + data_imports).

## Verifisering (utført, live 2026-07-03)

- Full testsuite grønn: **588 tester** (inkl. drivdal-feb2025-fasit-
  regresjonen) + Web Release-bygg 0 warnings.
- Nye tester: `HotFolderDetectorSpacingTests` (7 stk — 15-min uten markør,
  hourly, innhold-vinner-over-markør, 2-raders fallback, multi-plant fine,
  ISO-tidsstempler, kildenøkkel-mapping) og `ScadaImportServiceTests` (9 stk —
  aggregering 4/delvise kvarter, null-timer, DST-UTC-bucketing, determinisme/
  idempotens, fine-import med filter+hourly-avledning+notes, hourly-filter,
  useedet-anlegg-unntak).
- **Migrasjon verifisert live:** 0 scada-fine-rader igjen i data_imports;
  expectations viser nøyaktig tre kilder (operlog, scada, settlement).
- **Aktiv-tagliste verifisert live:** markør satt; 99 av 592 signal_map-rader
  har StoreSamples=true; Liavatns 9 tags seedet; 18 av 29 dammer (øvre
  kaskade) deaktivert; Lindland alt-overløp bevart aktiv.
- **KPI-regresjon juni 2026 (før/etter-diff av live API):**
  - Effektivitet: **identisk** for alle anlegg.
  - Economy (MWh, IEEE-AF, nedetid-timer, tap, events): **identisk** for
    10 av 11 anlegg; eneste diff er `orsdalen.reddetAvVaktNok`
    −1 216 → **+41 047** — den TILSIKTEDE ProductionStateProxy-effekten
    (se «Kjente konsekvenser»). Portefølje-vakt-roi −42 614 → −350.
  - Vakt-ROI per anlegg: alle øvrige anlegg uendret (kun sorterings-
    rekkefølge flyttet seg fordi Ørsdalen gikk til topps).

## Stille feil funnet og fikset under deploy (viktig kontekst)

1. Settlement-backfillen i `EnsureDataCompletenessSchemaAsync` hadde feilet
   STILLE lenge: live-tabellen har `completion_threshold_pct NOT NULL` uten
   default (eldre skjema), så INSERT-en kastet 23502 hver oppstart — fanget
   av catch-og-logg. Fikset (eksplisitt kolonne + idempotent SET DEFAULT),
   og scada-fine-migrasjonen ligger nå i egen batch så den ikke avhenger av
   backfillen.
2. EF raw-SQL-parametere støtter ikke `DBNull` — Liavatn-inserten brukte det
   og veltet hele AktivTagListeSeeder ved første deploy. Fikset med
   NULLIF-sentinel. (Markør-designet gjorde at andre forsøk anvendte alt
   korrekt.)
3. Testfilen `VaktRoiCalculatorTests.cs` hadde mojibake-encoding fra en
   tidligere PowerShell-tekstpipeline — 3 tester feilet stille (maskert av
   `dotnet test | tail`-exitkode). Reparert (147 linjer) i egen commit;
   lærdom notert i prosjektminnet.

## Kjente konsekvenser (tilsiktet)

- **Ørsdalen Vakt-ROI kan endre seg**: OverflowMode LevelProxy →
  ProductionStateProxy (spec D pkt. 3). LevelProxy var aldri funksjonell
  (HRV ble aldri fylt inn → DataAvailable=false); nå kan overløp utledes fra
  produksjonsstatus, så hendelser kan få produksjonskreditt de før ikke fikk.
- Neste SCADA-import med bred (gammel) eksport vil droppe deaktiverte/ukjente
  tags — synlig i importloggens notes («N samples droppet …»).

## Rest-punkter for driftsleder

1. SCADA: opprett tags for Stølskraft (overløp, virkningsgrad, kom-alarm) —
   fasit-status `MANGLER I SCADA-EKSPORT`.
2. SCADA: vurder Liavatn virkningsgrad + total-VF (mangler i eksporten).
3. Lindland: sjekk/kalibrer NIVA_OVERLOP_VF-sensoren (konstant 0) eller bytt
   eksport-tag (se over).
4. Sett opp den nye faste eksportjobben (én 15-min multi-plant master +
   operlog) — dokumentert i `SCADA_eksport_referanse.md`; avvikle
   hourly-eksportene.
5. Valgfritt: hev scada-threshold 0,80 → 0,95 i PlantAdmin når eksporten er
   fast jobb (spec E.2).
6. Merk: magasin-signalene mangler i mai/juni-importen (stopper 30.04) — ny
   eksport MED magasin-tags trengs for tilsigsmodellen etter april (kjent fra
   16.05-hendelsen, egen sak).
