# SPEC: Rollebasert UI-omstrukturering — Uptime
**Dato:** 2026-06-10
**Målgruppe:** Claude Code
**Grunnlag:** UI-GJENNOMGANG-ROLLER-2026-06-10.md
**Forutsetning:** Uavhengig av KODEGJENNOMGANG-2026-06-10.md (kritiske backend-fikser). Kan kjøres parallelt, men fase 1 her bør ikke kombineres med backend-endringer i samme økt.

## Mål

Appen skal svare tre roller uten graving:
- **Driftsleder** (alle anlegg): oppetid, nedetidshendelser, vaktutrykninger. Skal se «står noe / hva skjedde i natt» på under 5 sekunder.
- **Produksjonsleder**: maks verdi per liter vann — capture rate, overløp (i NOK), effektivitet, start/stopp, plan-etterlevelse.
- **Daglig leder**: totalstatus på 0 klikk.

Ingen eksisterende funksjonalitet skal fjernes. Vakt-ROI, Begreper/HjelpeIkon, KpiCard, Økonomi-fanen og Data-import beholdes som de er.

---

## Fase 1 — Ny landingsside «Oversikt» (STOR)

### 1.1 Nytt API-endepunkt: tverranleggs nedetidshendelser

**Ny fil:** `src/KraftverkUptime.Api/Endpoints/OversiktEndpoints.cs` (eller utvid NedetidEndpoints)

`GET /api/v1/oversikt/nedetid-hendelser?from={iso}&to={iso}&limit=20`

- Returnerer siste nedetidshendelser på tvers av **alle** anlegg, nyeste først.
- Gjenbruk `NedetidQueryService.ListEventsAsync` per anlegg, eller (bedre) ny metode `ListEventsAllPlantsAsync` som spør én gang med plant-join. Ikke N kall fra frontend.
- DTO per hendelse: `PlantId`, `PlantName`, `StartUtc`, `EndUtc`, `DurationHours`, `Category`, `CauseText`, `EstimatedLossNok` (null hvis ukjent), `Confidence`.
- Respekter eksisterende dedup-/overlay-logikk (annotations).

`GET /api/v1/oversikt/anleggsstatus`

- Per anlegg: `PlantId`, `PlantName`, `LastImportUtc` (per kilde: settlement/scada/operlog — gjenbruk DataCompleteness-data), `NedetidHoursLast7d`, `HasOpenIssues` (bool: karantene-filer eller manglende forventet import).
- Gjenbruk eksisterende query-services; ikke skriv ny SQL hvis dataene finnes.

### 1.2 Ny side: `Oversikt.razor`

**Ny fil:** `src/KraftverkUptime.Web/Pages/Oversikt.razor` med `@page "/"`.

Layout (ovenfra og ned):

1. **KPI-strip (DL-bildet):** gjenbruk Portefølje-sidens fire KpiCard (produksjon, spotomsetning, nedetidstap, reddet av vakt) for inneværende måned, med trend mot forrige måned der `KpiTrendCard`-data finnes. Hent fra samme endepunkt som Portefølje bruker — ikke dupliser aggregering i frontend.
2. **Statusbanner:** gjenbruk Data-import-bannerets «Siste 24 t»-logikk + varsel hvis karantene-filer > 0 (lenk til Data-import).
3. **«Siste nedetidshendelser» (driftsleder-blokken):** tabell fra nytt endepunkt, default siste 7 dager, kolonner: Anlegg, Start (lokal tid), Varighet, Kategori, Årsak, Est. tap. Radklikk → `/nedetid/{PlantId}`. Maks 10 rader + «Vis alle»-lenke til Nedetid.
4. **Anleggsstatus-rad:** kompakt tabell/kort-grid per anlegg: navn, nedetid siste 7 d, siste import, varselikon ved `HasOpenIssues`. Radklikk → anleggets Nedetid-side.

Krav:
- Bruk `NumberFormat.Norsk` på alle tall, lokal tid (Europe/Oslo) på alle tidspunkter.
- Loading/error/empty-tilstander etter samme mønster som Portefølje.
- HjelpeIkon på «Est. tap» og «Reddet av vakt» (tekstene finnes i `Begreper.cs`).
- Siden skal IKKE bruke global FilterState-periode — den viser alltid «nå»-bildet (inneværende måned for KPI, siste 7 d for hendelser). Skjul periodevelgeren her (samme mekanisme som ReportPageSelector-unntaket i MainLayout).

### 1.3 Routing

- `Oversikt.razor` tar `@page "/"`. `Reports.razor` beholder kun `@page "/reports"` — fjern `@page "/"` derfra.
- Auto-redirect-logikken i Reports.razor (FindLastMonthImport, linje ~152–160) **beholdes** for de som går til `/reports`.

---

## Fase 2 — Menygruppering og navngiving (STOR, men liten kode)

**Fil:** `src/KraftverkUptime.Web/Layout/NavMenu.razor`

Erstatt flat liste med `MudNavGroup` (alle `Expanded="true"` som default):

```
Oversikt        → / (ny side)
Portefølje      → /portefolje

— Drift —
Nedetid         → /nedetid
Vakt-ROI        → /vakt-roi

— Marked & vann —
Produksjon & plan → /produksjon
Capture rate      → /capture-rate
Effektivitet      → /effektivitet

— Data & admin —
Månedsrapporter → /reports
Anlegg          → /plants
Data-import     → /data-import
Kategorier      → /admin/kategorier
```

- Omdøp kun menyetiketter, **ikke** ruter (bokmerker skal ikke brekke).
- Badge på «Data-import» med antall importer i Behandles-status (gjenbruk tellingen fra `/behandles`; oppdater ved navigasjon, ikke polling). `/behandles` består som side.
- Mini-drawer-oppførselen (hover-ekspansjon) beholdes — verifiser at MudNavGroup fungerer i mini-modus; hvis ikke, bruk seksjonsoverskrifter (`MudText` subtitle) i stedet for kollapsbare grupper.

---

## Fase 3 — Små grep (rangert)

### 3.1 Hurtigvalg kort periode
**Fil:** `AppBarPlantPeriodSelector.razor` + `PeriodCalculator.cs` (+ tester `PeriodCalculatorTests`).
Legg til «Siste 7 dager» og «I går» i periodevelgeren. Sidene som konsumerer FilterState skal fungere uendret (de tar allerede vilkårlig fra/til via Egendefinert). Utvid PeriodCalculator med de to nye, med enhetstester for DST-overganger (Oslo-lokal dag → UTC).

### 3.2 Overløp i NOK + Portefølje-kolonne
- Beregning: overløps-MWh-ekvivalent × spotpris time-for-time (samme prisgrunnlag som nedetidstap; se ANBEFALING-TAPSREGNSKAP.md). Legg i eksisterende overløps-query/kalkulator med enhetstester — IKKE i frontend.
- Vis: nytt KPI-kort «Overløpstap (NOK)» på Produksjon-siden rad 3, og ny kolonne «Overløp» i Portefølje-tabellens utvidede sett. HjelpeIkon-tekst i Begreper.cs.
- Flagg datakvalitet: hvis overløpsestimatet har lav confidence, vis med «~»-prefiks og tooltip (samme mønster som andre estimater).

### 3.3 Persistér kolonnevalg Portefølje
**Fil:** `Portefolje.razor`. Lagre «Vis alle kolonner»-toggle i localStorage via samme mekanisme som FilterState. Vurder samtidig å flytte «Nedetid (t)» og «Reddet vakt» inn i default-settet (driftsleders kjernetall) — maks 9 default-kolonner.

### 3.4 StartStoppKort på Produksjon-siden
Gjenbruk `StartStoppKort`-komponenten fra ReportDetail på Produksjon-siden (per anlegg-modus), med periode fra FilterState. Hvis komponenten er bundet til rapport-DTO: lag overload som tar StartStopp-endepunktets respons direkte.

### 3.5 Verifiserte bugfikser
- `Nedetid.razor:81`: `Accent="KpiAccent.Positive"` → `KpiAccent.Negative` på «Tap i NOK».
- `VaktRoi.razor`: fjern duplikat «Total reddet» (linje 147 vs 179 — behold den som har trend/InfoText, fjern den andre).

### 3.6 Kanonisk merverdi-navnesett
Rydd i etikettbruk (kun visningstekst, ikke API/DTO-navn):
- «Timing-merverdi» — beholdes som kanonisk navn for CR-basert merverdi mot flat spot. Alle steder som viser samme tall skal bruke dette navnet.
- «Netto mot plan» — beholdes for plan-etterlevelse (Produksjon).
- «Hydrogrid plan-merverdi» → undertekst/tooltip, ikke hovedetikett.
- Legg én samleforklaring i Begreper.cs («Merverdi-begrepene henger sammen slik: …») og lenk HjelpeIkon på alle merverdi-kort til den.
- Grep etter alle forekomster av «merverdi» i .razor og normaliser.

---

## Akseptansekriterier

1. `/` viser Oversikt-siden med KPI-strip, hendelsesliste og anleggsstatus uten brukerinteraksjon. Laster < 3 s med 11 anlegg.
2. Driftsleder kan se alle nedetidshendelser siste 7 dager på tvers av anlegg uten å bytte anlegg.
3. Daglig leder ser produksjon, omsetning, nedetidstap og vakt-verdi for inneværende måned på 0 klikk.
4. Produksjonsleder ser overløp i NOK på Produksjon-siden og i Portefølje-tabellen.
5. Meny er gruppert; ingen eksisterende rute brukket; `/reports` fungerer som før.
6. «Siste 7 dager» og «I går» finnes i periodevelgeren og fungerer på Nedetid, Vakt-ROI, Capture rate, Produksjon, Effektivitet.
7. Alle nye tall bruker NumberFormat.Norsk; alle nye tidspunkter vises i lokal tid.
8. Eksisterende tester grønne; nye enhetstester for PeriodCalculator-tilleggene og overløp-NOK-beregningen.
9. `dotnet build` uten nye warnings.

## Rekkefølge og avgrensning

- Kjør fasene i rekkefølge 1 → 2 → 3; fase 3-punktene er uavhengige og kan tas enkeltvis.
- Ikke refaktorer NedetidApi/ReportsApi-arkitekturen i denne specen (eget arbeid, se KODEGJENNOMGANG-2026-06-10.md V1/K1/K2) — men nye API-kall som skrives her skal lese ProblemDetails ved feil, ikke `catch { return null; }`.
- Ikke endre backend-beregninger utover 3.2.
