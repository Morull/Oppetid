# Masterplan for Claude Code — status og kø (2026-05-22)

Samlet sjekkliste over alt utestående arbeid. Hak av etter hvert som punkter committes. Hvert punkt peker til den detaljerte spec-en der konkret datamodell, kontrakt og akseptkriterier ligger.

---

## Status — verifisert levert (per 2026-05-22 ettermiddag re-audit)

- [x] Effektivitet 15-min-data, tapstoppliste, Anleggssammenligning, KPI-seksjonstitler
- [x] ApexCharts `NullReferenceException` på Effektivitet — krasj og feilbanner borte
- [x] KAIA-kostnad per rapportperiode med Megler + Fast-komponenter
- [x] Normal årsproduksjon-felt + GWh-fordelt vakt-kost
- [x] Vakt-ROI Del B: Faktisk-varighet-override, splittet Reddet (Overløp/Ubalanse), Detaljer-kolonne
- [x] Nedetid hendelsesdetaljer-kolonne
- [x] Økonomi-underfane på Portefølje med Anleggs-velger, 3 KPI-grupper, 9 kort, trend-piler, per-anlegg-tabell
- [x] Spotomsetning ÅTD-aggregering (Økonomi-oppfølging Funn 1)
- [x] **NY:** CR-merverdi-opprydding — Timing-merverdi, Realisert vs spot, Realisert pris, volumvektet Capture-pris finnes på Capture rate (`NESTE-CHAT-CR-MERVERDI-OPPRYDDING`)
- [x] **NY:** Vakt-ROI U2-PlanDeviation-filter — «Vakt utrykt»-kolonne (Auto/Ja/Nei) + «Hendelser til vurdering»-seksjon på per-anlegg-visning (`NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER`)
- [x] **NY:** KPI-integritet Oppgjør — Oppgjør-kortet på Rapport-detalj viser nå **«Spot + ubalanse + andre poster (+225 685 NOK)»** i undertittelen. Avstemmer: 2 266 525 + (−13 922) + 225 685 = 2 478 288 ≈ 2 478 287 vist. ✓ (`FORBEDRINGSFORSLAG-KPI-KORT-INTEGRITET`)
- [x] **NY:** «Total produksjon» renamet til «Produksjon i perioden» (smårefiks fra KPI-integritet-doc)
- [x] **NY:** Sortering + header-tooltips levert på flere tabeller — Capture rate Måneds-trend (6/6 sortable + 6/6 tooltips), Nedetid Hendelser (7/9 sortable + 7/9 tooltips, handlings-kolonner ekskludert som forventet), Nedetid Per kategori (4/4 sortable + 4/4 tooltips), Portefølje Sammendrag (8/8 sortable + 7/8 tooltips). Delvis levert fra `FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING`

---

## 🔴 Kritisk — fakta- eller funksjonsfeil

### Vakt-ROI vakt-vindu-bug
Ref: `NESTE-CHAT-VAKTROI-OG-UI-FIKS.md` Del A

- [x] **Levert og verifisert live 2026-05-22.** Fiks i `VaktRoiCalculator.cs:225-236`, monoton-invariant-tester (linje 790 og 840) passerer. Live-test Haukland april 2026 / 15-23 / oppmøte 08: Din konfig 37 496 NOK, 2 reddbare events. Døgnvakt 40 158 NOK, 4 reddbare events. Differanse −2 663 NOK. Bannermeldingen rendrer ikke lenger.

### PDF-eksport ende-til-ende
Ref: `NESTE-CHAT-OKONOMI-OPPFOLGING.md` Funn 4 + 5

- [ ] **Frontend:** PDF-knappen prefikser med `ApiBaseAddress` fra `wwwroot/appsettings.json` (ikke relativ URL)
- [ ] **Backend:** Bytt NuGet-pakke fra `SkiaSharp.NativeAssets.Linux` til `SkiaSharp.NativeAssets.Linux.NoDependencies`
- [ ] **Fonter:** Hvis Inter-fonten brukes, registrer den i container-imaget eller i `QuestPDF.FontManager.RegisterFont`
- [ ] Verifiser ende-til-ende: klikk «Last ned PDF» → fil lastes ned → åpner i PDF-leser
- [ ] PDF inneholder forside + KPI-sammendrag + per-anlegg-tabell + trend per spec

### KPI-kort-integritet (Oppgjør-bruddet)
Ref: `FORBEDRINGSFORSLAG-KPI-KORT-INTEGRITET.md`

- [x] Implementer byggekrav i Del 1 som standard for nye KPI-kort — *anvendt på Oppgjør*
- [x] Rapport-detalj: «Oppgjør 2 478 287 NOK» viser nå «Spot + ubalanse + andre poster (+225 685 NOK)» som avstemmer ✓
- [x] Effektivitet: «Total produksjon» renamet til «Produksjon i perioden» ✓
- [ ] Rapport-detalj: legg «Drift / Marked / Økonomi»-seksjonstitler på den øverste KPI-kort-blokken (sekjonene finnes i KPI-katalog-tabellen lenger ned, men ikke på toppkortene)

---

## 🟡 Kort polering — høy daglig verdi, liten teknisk endring

### Økonomi-fanen — oppfølging
Ref: `NESTE-CHAT-OKONOMI-OPPFOLGING.md`

- [ ] **Funn 2 — SUM-i-kolonner (FORTSATT UTESTÅENDE):** Verifisert med pixel-mål 2026-05-22: tfoot-cellene tar 997 px total, mens thead/tbody-kolonnene strekker seg 5720 px. Tfoot er en fri liten rad uten kolonne-binding. Må migreres til `PropertyColumn.Footer`. Hver SUM-celle må arve sin kolonnes `width` og `right`-posisjon — først da legger «20 404 688» seg under «Spotoms (NOK)»-kolonnen.
- [x] **Funn 2 — Høyrejustering:** Verifisert levert — alle numeriske celler i thead/tbody/tfoot har `text-align: right`. Kun selve plassen mangler (jf. forrige punkt).
- [ ] **Funn 3 — 429-burst:** Status uklar — har ikke trigget 429 i denne testen, men 25+ kall i burst ble likevel observert ved periode-bytte. Verifiser at `@if (_activeTab == X)`-gating er på plass.
- [ ] **Funn 3 — Sekundært:** Klient-side request-coalescing i `EconomyApi`.

### Datakvalitets-banner
Ref: bekreftet live 2026-05-22

- [ ] Omformuler «9 av 11 anlegg mangler data (Nedetid)»-meldingen på Sammendrag. Vis i stedet «Importen rekker til 18.5. — siste 11 dager mangler for 9 anlegg» (eller tilsvarende). Bedre å si hvor mye + når, enn å si «mangler data».

### Vakt-ROI U2-PlanDeviation-filter
Ref: `NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md`

- [x] **Levert.** Verifisert live på `/vakt-roi/haukland`: hendelsestabellen har «Vakt utrykt»-kolonne (13 totale kolonner), «Hendelser til vurdering»-seksjon eksisterer som egen tabell under hovedtabellen. Detaljer-popup-innholdet ikke åpnet i denne testen — verifiser visuelt at radioknappene Auto/Ja/Nei og operlog-info finnes der.

### Øgreyfoss overløp-fix
Ref: `NESTE-CHAT-OGREYFOSS-OVERLOP-FIX.md`

- [ ] Verifiser status først: `SELECT signal_id, dam_id FROM core.signal_map WHERE plant_id='ogreyfoss' AND role='OverflowFlow';` — `dam_id` skal være `ogreyfoss_ogreyvatn`
- [ ] Hvis ikke: `DalanePortfolioSignalMapSeeder.cs:64` — `if (plantId == "ogreyfoss") damId = "ogreyfoss_ogreyvatn";`
- [ ] Test-filer + ny regresjons-test for alle MultiDam-anlegg
- [ ] Data-migrering: idempotent SQL-fixup i `DefaultDamSeeder`
- [ ] Verifiser: `GET /api/v1/nedetid/overflow?plantId=ogreyfoss&from=…&to=…` returnerer `DataAvailable=true`

### CR-merverdi-opprydding
Ref: `NESTE-CHAT-CR-MERVERDI-OPPRYDDING.md`

- [x] **Levert.** Verifisert live på `/capture-rate` (Vikeså april 2026): Capture-pris (volumvektet spot) 1100 NOK/MWh, CR (times) 1,07, Timing-merverdi +117 011, Realisert pris 1059 NOK/MWh, Realisert vs spot −71 475. Gammel «Merverdi vs spot» er borte fra KPI-kortene.
- [ ] **Mindre:** Måneds-trend-tabellen heter fortsatt kolonnen «Merverdi». Bør bli «Timing-merverdi» for konsistens med KPI-kortene over.
- [ ] **Verifiser:** Produksjon-fanens «Hydrogrid merverdi» rename til «Planens timing-verdi» (når fanen bygges på ekte i Tapsregnskap-arbeidet).
- [ ] **Verifiser:** unit-test for invariant CR > 1 ⟺ Timing-merverdi > 0 finnes i tester.

---

## 🟢 Større, klart avgrenset arbeid

### Info-popup og sortering på alle tabeller/grafer
Ref: `FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING.md`

**Delvis levert per re-audit 2026-05-22 ettermiddag.**

- [x] `KolonneHode`-mønsteret (sort + tooltip) rullet ut på Capture rate Måneds-trend (6/6), Nedetid Hendelser (7/9), Nedetid Per kategori (4/4), Portefølje Sammendrag (8/8 sort + 7/8 tooltips).
- [ ] **Resterende sortering:** Vakt-ROI portefølje top-events-tabell (10 kol, 0 sorterbare). Vakt-ROI portefølje per-anlegg-rollup (7 kol, 0 sorterbare). Vakt-ROI per anlegg «Hendelser til vurdering» (7 kol, 0 sorterbare). Rapport-detalj KPI-katalog × 3 (5 kol, 0 sorterbare) og «Klassifiserte timer» (8 kol, 0 sorterbare). Kategorier-tabellene.
- [ ] **Resterende tooltips:** samme tabeller som over + Effektivitet-tabellene (kunne ikke verifiseres i denne testen — siden tidsavbrøt).
- [ ] **Kort-popup på kort-nivå (ℹ-knapp i kort-header):** ingen `cardHasInfoIcon` funnet på de testede tabellene. Verifiser om dette er bevisst (header-tooltips dekker forklaringsbehovet) eller om kort-popup skal rulles ut i tillegg.
- [ ] Capture rate-grafer: sjekk om de to navnløse grafene har fått korttittel.
- [ ] Nedetid: aksetitler på «Tap per kategori».
- [ ] `Begreper.cs` — felles ordliste-kilde. Verifiser om den finnes (mest sannsynlig ja, gitt at tooltips er rullet ut konsistent).

### Tapsregnskap
Ref: `ANBEFALING-TAPSREGNSKAP.md`

- [ ] **Steg 1:** Produksjon-fanen — «Netto mot plan»: rename Hydrogrid-merverdi til «Planens timing-verdi», innfør Realisert timing-verdi, Timing-gap, Ubalanse-kost, og Netto mot plan
- [ ] **Steg 2:** Ubalanse-formelen blir tosidig (underlevering OG overlevering) hvis nedregulerings-pris finnes; ellers flagg som «kun underlevering medregnet»
- [ ] **Steg 3:** Måneds-trend-tabell på Produksjon-fanen med Netto mot plan-kolonne og in-celle søyle
- [ ] **Steg 4:** Drill-down: klikk en måned → timene som drev ubalanse-kosten, sortert etter kroner
- [ ] **Steg 5 (gating):** Rist-falltap — datakvalitets-port for falltap-signalet (≈0 ved Q=0, korrelasjon med Q²); auto-nullstilling; beregning bak port
- [ ] Map `LINDLAND_INNTAK_RIST_FALLTAP_PV` til `SignalRole.GridFallLoss` (i dag `Other`)
- [ ] **Steg 6:** Generaliser til fullt tapsregnskap (egen, mer detaljert spec etter at 1-5 står)

---

## ⚡ Ytelse — eget arbeidsområde

Drifts-leder rapporterer at appen «krever endel lasting for å få frem rapporter, spesielt når alle anleggene er valgt». Funnene mine bekrefter det:

### Observasjon

| Symptom | Hvor sett |
|---|---|
| Effektivitet-endepunkt tidsavbryter (~36 s før respons) | Verifisert live 2026-05-22 — `net_http_request_timedout` for Vikeså ÅTD |
| 22+ parallelle API-kall ved periode-bytte | Instrumentert `window.fetch`, 18-25 kall innenfor 8 sekunder |
| 429 Too Many Requests dukker opp sporadisk | Rapportert av drifts-leder, sannsynlig konsekvens av burst |
| Portefølje-fetch tar 10-15 sekunder å fullføre | Verifisert live tidligere i sesjonen |

### Rotårsaker

1. **Frontend fan-out:** 11 anlegg × 2-3 endepunkter = 25+ parallelle kall ved hver periode-endring (`/vakt-roi`, `/nedetid`, `/kaia-cost` osv. per anlegg)
2. **Manglende server-side aggregering på Sammendrag:** Økonomi-endepunktet aggregerer allerede, men Sammendrag kaller per-anlegg
3. **Tunge data per kall:** events-array per anlegg for hele perioden; for ÅTD = 5 måneder × 49 events × 11 anlegg
4. **Ingen response-caching:** hver kall treffer DB på nytt selv om settlement-data er statisk etter import
5. **Ingen HTTP-compression:** store JSON-responser sendes ukomprimert
6. **15-min-aggregering tung:** Effektivitet-endepunktet beregner episoder over ÅTD-perioden hver gang

### Tiltak — rangert etter forhold mellom innsats og effekt

#### Kjapp gevinst (timer–dager)

- [ ] **HTTP-compression** — én linje i `Program.cs` (`UseResponseCompression(...)` med Gzip+Brotli). 70-80 % båndbredde-reduksjon på JSON-responser. Null risiko.
- [ ] **Klient-side memoization i `HttpClient`-wrappere** — bufre (URL+params) → response med TTL ~30 s. Periode-bytte tilbake til samme periode trenger ikke nytt kall. Lite mønster, stor effekt ved navigasjon.
- [ ] **Tab-isolation (Funn 3, Økonomi-oppfølging)** — fjerner umiddelbart 22+ unødvendige kall ved fane-bytte.
- [ ] **Returner bare hva som vises** — `events`-arrayen på Nedetid-/Vakt-ROI-respons er stor. Sammendrag trenger bare aggregat-tall. Legg events bak en query-param (`?includeEvents=true`), default false. Sammendrag laster da kun aggregat; drill-down henter events ved behov.

#### Middels arbeid (1-3 dager)

- [ ] **Sammendrag-fanen reuser Økonomi-endepunktet** — `/api/v1/economy` returnerer allerede alt Sammendrag trenger (4 toppkort + per-anlegg-tabell-tall). Fjerner 22 per-anleggs-kall til fordel for 1 aggregert kall. Største enkelt-gevinst.
- [ ] **DB-indeksanalyse på de tunge spørringene** — `EXPLAIN ANALYZE` på `EffektivitetEpisodeService`, `SettlementQueryService`, `OverflowQueryService`. Sjekk at det finnes indeks på `(plant_id, period_start)` eller `(plant_id, ts)` der det er relevant. Manglende indekser er den vanligste årsaken til at en SELECT går fra millisekunder til sekunder.
- [ ] **Database connection pooling** — bekreft at Npgsql connection pool er på (default på i `Npgsql`, men sjekk `Pooling=true; Maximum Pool Size=…` i connection string). Hvis ikke, hver request åpner ny TCP/SSL-tunnel mot Postgres = 50-200 ms per kall.

#### Større tiltak (1-2 uker)

- [ ] **Pre-aggregering for vanlige perioder** — daglig job som beregner månedsaggregat per anlegg per KPI (Spotomsetning, Oppgjør, Ubalansekost, KAIA-kostnad, Nedetidstap, Reddet, η-snitt) og lagrer i en `monthly_kpi_aggregate`-tabell. Sammendrag/Økonomi-kall slipper å summere mange rader fra rå-tabellene — bare slå opp i aggregat-tabellen. Spesielt effektivt for år og «Hittil i år».
- [ ] **Effektivitet-endepunktet — flytt episode-deteksjon til pre-beregning** — i dag beregnes episoder on-the-fly per kall. For lange perioder er det tregt. Beregn episoder ved import, lagre i `effektivitet_episode`-tabell. Da blir spørringen en ren SELECT med filter.

### Foreslått ytelses-rekkefølge

1. HTTP-compression (1 linje, 1 dag, stor effekt)
2. Tab-isolation (allerede i Funn 3, 0,5 dag)
3. Klient-side memoization (1 dag)
4. Sammendrag reuser `/economy`-endepunktet (1-2 dager) — fjerner mesteparten av fan-out-problemet
5. `?includeEvents=false`-mønsteret + drill-down (1 dag)
6. DB-indeksanalyse + connection pool-sjekk (0,5-1 dag)
7. Pre-aggregert månedstabell (1 uke)
8. Effektivitet-episode-tabell (1 uke)

Etter punkt 1-5 bør appen oppleves som tydelig raskere uten større refaktor. Punkt 6-8 er forsikring for skalering når det importeres flere år med data.

---

## Skal skrives når Tapsregnskap er ferdig

- [ ] **Avviks-fane** — sentral fane med 12-mnd glidende snitt **OG** samme-måned-i-fjor som toggle. Viser «hva har kostet mest», «avvik som bør følges opp», «gjentagende mønster». Bygges oppå Tapsregnskap-output. Spec skrives når fundamentet står (~3-4 dager når den tid kommer).

---

## Anbefalt rekkefølge for Code

1. **PDF-fiks (Funn 4 + 5 fra Økonomi-oppfølging)** — én linje frontend + én NuGet-pakke. Krysser ut to bokser samtidig.
2. **Ytelse «kjapp gevinst»-punktene** — HTTP-compression, tab-isolation, memoization. Drifts-leder merker forskjellen umiddelbart.
3. **Vakt-vindu-bugen** (kritisk faktafeil)
4. **Sammendrag reuser `/economy`-endepunktet** — største enkelt-gevinsten for fan-out-problemet
5. **KPI-integritet (Oppgjør-bruddet)**
6. **U2-PlanDeviation-filter**
7. **CR-merverdi-opprydding + Øgreyfoss overløp**
8. **SUM-i-kolonner + datakvalitets-banner-tekst**
9. **Info-popup + sortering**
10. **Tapsregnskap** (legger fundamentet for Avviks-fanen)

Hvert punkt har sin egen detaljerte spec — denne masterplanen er kun en rute-tavle.
