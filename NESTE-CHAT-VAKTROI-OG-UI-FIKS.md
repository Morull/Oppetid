# NESTE CHAT — Vakt-ROI vindu-bug + UI-fiks i Portefølje

**Dato:** 2026-05-22
**Skrevet for:** Claude Code, basert på live-feilsøking i kjørende app.

Fire saker i prioritert rekkefølge. **Del A er en faktafeil** — Vakt-ROI viser at et kortere vaktvindu redder mer enn et lengre, som er matematisk umulig. De andre tre er polering.

---

## Del A — Vakt-ROI: «Din konfig» bryter monoton-invariant

### Symptom (verifisert live på Haukland april 2026)

I `VaktRoi.razor` viser «Sammenligning vs. døgnvakt»:

| Konfig | Reddet brutto | Reddbare events |
|---|---|---|
| **Din konfig 15:00–23:00** (oppmøte 08:00) | **54 521 NOK** | **4 av 13** |
| Døgnvakt 15:00–07:00 (oppmøte 08:00) | 47 619 NOK | 10 av 13 |

Et vakt-vindu på 8 timer (15–23) redder mer enn et vindu på 16 timer (15–07), med færre events. Det er umulig: 15–23 dekker en strikt delmengde av timene i 15–07, så hvert event som fanges i 15–23 må også fanges i 15–07 — aldri omvendt. Banneret på siden flagger selv at det er «uvanlig» og ber brukeren sjekke parametriseringen — men feilen er i beregningen, ikke i input.

### Diagnose

API-et er ikke kilden:

- `GET /api/v1/plants/haukland/vakt-roi?from=…&to=…` aksepterer **kun** `from` og `to`. Ingen vindu-parametere.
- Responsen returnerer alltid `vaktStartLokal:"15:00"`, `vaktSluttLokal:"07:00"`, `oppmoteLokal:"08:00"` med `totalReddetNok: 47 619` for april — det er døgnvakt-tallet.
- Etter klikk på «Bruk vakt-vindu» blir det fortsatt bare ett kall, samme URL, ingen window-param i body eller header (verifisert via instrumentert `window.fetch`).

Konklusjon: **«Din konfig» beregnes 100 % client-side i `VaktRoi.razor` fra samme event-respons**, ved å re-filtrere/re-aggregere med det brukerinntastede vinduet. Den re-beregningen har en feil som bryter monoton-invarianten.

### Invariant som må holde (kandidat for unit-test)

For samme periode og samme oppmøte, og to vindu hvor `Vindu_A ⊆ Vindu_B`:

```
TotalReddetNok(Vindu_A)  ≤  TotalReddetNok(Vindu_B)
AntallReddbareEvents(Vindu_A) ≤ AntallReddbareEvents(Vindu_B)
```

Konkret: 15–23 er en delmengde av 15–07 (sistnevnte wrapper midnatt og dekker også 23–07). Så Sammenligning kan **aldri** vise et høyere tall for det smalere vinduet.

### Sannsynlige rotårsaker å sjekke i `VaktRoi.razor`

1. **Feil tidssjekk for events som starter utenfor brukerens vindu.** Et event som starter kl. 05:00 (innenfor 15–07, utenfor 15–23) skal i brukerens konfig vente til oppmøte 08:00 — `ekstraTimerSpart` blir mindre, ikke mer. Sjekk at re-beregningen behandler «event utenfor vindu» korrekt: vakta gjør ingenting; eventet løses ikke før oppmøte.
2. **`(oppmøte − vaktSlutt)` brukt som multiplikator.** Hvis koden et sted bruker timer mellom slutt-vakt og oppmøte som «savings-faktor», blir den 9 t for brukerens 23→08, men bare 1 t for døgnvakt 07→08. Det ville feilaktig bumpe brukerens reddet-NOK.
3. **Counterfactual recompute.** API-en gir `counterfactualEndUtc` per event (basert på default-konfig). Hvis client recomputerer counterfactual fra brukerens vindu, kan logikkfeil gi lengre counterfactual = større reddet.
4. **Per-event `reddetNok` skalering uten å nullstille events utenfor vinduet.** Det ville forklare 4 events × høyere snitt vs 10 events × lavere snitt.

### Akseptkriterier

- [ ] For Haukland april 2026: Din konfig 15–23 ≤ Døgnvakt 15–07, både i NOK og antall reddbare events.
- [ ] Ny unit-test i `VaktRoiCalculator` (eller hvor re-beregningen ligger) som asserter monoton-invarianten for et lite event-sett og to vindu hvor A ⊆ B.
- [ ] Banneret «Din konfig redder mer enn døgnvakt — uvanlig» bør i praksis aldri trigges; behold som sikkerhetsnett, men forventning er at det forblir skjult etter fiks.
- [ ] Verifiser også et anlegg med flere natt-events (Øgreyfoss eller Vikeså) — der skal forskjellen være tydelig: døgnvakt fanger natt-events, ditt vindu fanger ikke.

---

## Del B — Vakt-kost: ingen endring nødvendig (verifisert)

Du sa at vakt-kost kun skal kunne settes på portefølje-nivå og fordeles etter GWh-andel — ikke per anlegg. **Det er allerede slik.** Verifisert live:

- `PlantAdmin` (`/plants/haukland/admin`) har feltene Plant-ID, Navn, Type, Installert effekt (MW), Normal årsproduksjon (GWh), Turbin-type, Fallhøyde, Energiekvivalent, Operasjonsstart, Tidssone, Plan-avviks-terskel, SCADA-tag. **Ingen vakt-kost-felt.** ✓
- Vakt-ROI per anlegg viser «Anleggets andel av vaktkost X NOK/år · Y % av total — Fordelt etter normal årsproduksjon (GWh-andel)». ✓
- Vakt-ROI portefølje har én input «Samlet portefølje-vaktkost (NOK/år)» som styrer hele fordelingen. ✓

**Eneste åpne underpunkt:** Bekreft at porteføljeverdien faktisk persisteres mot DB / settings — ikke bare i sesjons-state. Hvis den ikke gjør det i dag, lagre den som en porteføljeinnstilling (egen tabell eller key/value-rad), slik at den overlever reload og er den samme for alle brukere.

---

## Del C — Portefølje-tabellen: høyrejuster tall og fest SUM-raden

### Symptom (verifisert live, `/portefolje`)

- Alle `<th>` og `<td>` har `text-align: start` (= venstre i LTR). Numeriske kolonner (Effekt MW, AF, FOR, Produksjon, Normalår %, Spotoms, Datakvalitet) blir venstrejustert. Det er ikke konvensjonen for tall-tabeller, og det får SUM-raden i `<tfoot>` til å «skli» visuelt bort fra tallene over.
- Anlegg-kolonnen får residual-bredde i layouten og blir veldig vid på ultrawide-skjerm (49"). Det skaper «for mye luft» mellom anleggsnavnet og neste kolonne.
- Datakvalitet-cellene viser f.eks. «93.8 %» (US-format: punktum desimal, ingen tusenskille) mens nabokolonner viser «20 080,4» (norsk: mellomrom + komma). Samme mønster som funn #3 i `TESTRAPPORT-2026-05-21.md`.

### Endringer

1. **Høyrejuster numeriske kolonner** — header (`<th>`), data (`<td>`) og fot (`<tfoot>`):

   ```css
   .portefolje-table th.numeric,
   .portefolje-table td.numeric { text-align: right; }
   ```

   (eller MudDataGrid: `<PropertyColumn ... HeaderClass="text-end" CellClass="text-end" FooterClass="text-end"/>`)

   Kolonner som skal høyrejusteres: Effekt (MW), AF, FOR, Produksjon (MWh), Normalår %, Spotoms (NOK), Datakvalitet. Anlegg forblir venstrejustert.

2. **Få SUM-cellene til å bruke samme kolonnebredde som dataradene.** I MudDataGrid betyr det å sette `Footer` på hver `PropertyColumn` (én verdi per kolonne), ikke en separat `<tfoot>`-rad med fri layout. Da arver fot-cellene `width` fra kolonne-definisjonen og legger seg perfekt under tallene.

3. **Anlegg-kolonnens bredde.** Sett `min-width`/`max-width` på Anlegg-kolonnen (f.eks. `min-width: 140px; max-width: 200px`), så overflødig viewport-plass fordeles på de andre kolonnene i stedet for å samle seg i Anlegg. Eventuelt sett `max-width` på hele tabell-containeren (f.eks. `1280px`, sentrert), så den ikke prøver å fylle ultrawide-skjerm.

4. **Datakvalitet-formatering.** Bruk den samme `NumberFormatInfo` som resten av tabellen (mellomrom som tusenskille, komma som desimal). Knytt dette til den felles `NorskTall`-hjelperen som UI-gjennomgangen 20.05 anbefalte å konsolidere.

### Akseptkriterier

- [ ] Tall i `<thead>`, `<tbody>` og SUM-raden står i samme vertikale linje per kolonne.
- [ ] SUM-radens verdier er høyrejustert under sine respektive datakolonner.
- [ ] Datakvalitet-kolonnen viser tall i samme format som nabokolonnene (f.eks. «99,2 %», ikke «93.8 %»).
- [ ] Anlegg-kolonnen tar ikke mer enn ~200 px på 1440p+; overflødig bredde fordeles til de andre kolonnene eller tabellen senterers.

---

## Del D — Prioritet og rekkefølge

1. **Del A (Vakt-ROI vindu-bug)** — faktafeil i et beslutningsgrunnlag. Drifts-leder vil ellers feiltolke at en kortere vaktordning gir bedre ROI. Topp prioritet.
2. **Del C (Portefølje-tabell)** — kosmetisk-funksjonell. Lav teknisk risiko, høy daglig verdi.
3. **Del B (vakt-kost-persistens)** — kun verifiser/dokumentér; trolig allerede greit.

Punkt 1 og 2 kan committes som hver sin patch. Punkt 1 bør ha en monoton-invariant-test som blir liggende permanent.
