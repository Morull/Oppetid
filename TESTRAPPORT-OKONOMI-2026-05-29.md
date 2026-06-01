# Testrapport — Økonomi-fane og periodesummering

**Dato:** 2026-05-29
**Omfang:** Kontrollregning av økonomi-KPI-ene og verifisering av at de summerer riktig over ulike perioder (måned vs. ÅTD/Custom), pluss kodegjennomgang av aggregeringslogikken.
**Metode:** Uavhengig kontrollregning i Python direkte mot kilde-`.xlsx` i `CSV Eksporter` + statisk kodegjennomgang av `EconomyReportQueryService`, `DbSettlementImportRecorder`, `KaiaCostQueryService` og `ExcelSettlementParser`.

> **Oppdatert 2026-05-30 med live-verifisering** mot kjørende app (`https://desktop-r4rfc53…ts.net`, API `/api/v1/economy`). Alle funn under er nå bekreftet mot faktiske app-tall. Se egen seksjon **«Live-verifisering»**.

---

## ⚠️ Hovedsvar på spørsmålet ditt: ja, mai mangler for 10 av 11 anlegg

Etter maskinbytte + re-import er **januar–april korrekt lastet inn for alle 11 anlegg** (verifisert mot kildedata, krone for krone — se «Live-verifisering»). Men:

- **Mai er IKKE lastet inn for 10 av 11 anlegg.** Kun Vikeså har mai, og bare til 20. mai. Appen sier selv: *«Importen rekker til 30.04.»* og `manglerImportHours = 746` (≈ én måned) for alle unntatt Vikeså.
- **Rotårsak:** Mai-multifila som dekker de 9 hovedanleggene (`…dataeksport_20260518114818.xlsx`, periode 01.–17.05.2026, alle 9 anleggsfaner) ligger i **`CSV Eksporter/duplicates/2026-05/`** — den ble avvist som duplikat under re-importen og kom aldri inn i databasen. Den står *ikke* i `.hotfolder-dedup.json` (som ble bygget på nytt 30.05), så det er import-pipelinens egen dedup som avviste den, ikke hotfolder-vakta.
- **Fiks:** Flytt mai-fila tilbake til `CSV Eksporter/`-roten (eller last den opp via appen) så watcheren plukker den. Da bør mai bli grønt for de 9 anleggene igjen. Vikeså mai 1–20 finnes allerede; den siste partielle uka (20.–31.) mangler kilde.

Dette er en *import*-mangel, ikke en regnefeil. Regnefeilen under (Funn 1) er en separat sak.

---

## Sammendrag

Det er **én reell, bekreftet feil** som forklarer at økonomitall ikke summerer riktig over perioder, og den er **systemisk** (rammer flere tjenester):

> Når et anlegg har flere importer som **overlapper i tid med ulik datospenn**, blir de **dobbelttelt** for enhver periode som overlapper. Dedupliseringen fjerner bare importer med *eksakt lik* `(PeriodStart, PeriodEnd)` — ikke importer der den ene perioden er inneholdt i den andre.

Konkret bevis finnes i dagens data: Vikeså har to mai-importer (1.–17. og 1.–20. mai). Den korteste er fullstendig inneholdt i den lengste, men begge summeres.

Den tidligere rapporterte hovedfeilen (ÅTD returnerte kun siste måned — `NESTE-CHAT-OKONOMI-OPPFOLGING.md` Funn 1) er **rettet**: koden summerer nå over alle importer i perioden. Men nettopp denne fiksen er det som introduserte dobbelttellings-sårbarheten.

Én mistenkt feil er **avkreftet**: månedsgrense-lekkasje skjer ikke (forklart under).

---

## Funn 1 — KRITISK: Overlappende importer dobbelttelles

### Bevis fra data

| Import | Periode | Spotomsetning | Kvarter (rader) |
|---|---|---|---|
| Vikeså A | 01.05.2026 – 17.05.2026 | 189 351 NOK | 1 632 (17 d × 96) |
| Vikeså B | 01.05.2026 – 20.05.2026 | 366 397 NOK | 1 920 (20 d × 96) |

Begge lå i `done/2026-05/` (begge importert). Periode A er **helt inneholdt** i periode B — A er altså en utdatert delmengde, men telles likevel med.

### Rotårsak (kode)

`DbSettlementImportRecorder.ListForPlantAsync` henter alle importer som *overlapper* perioden:

```csharp
query = query.Where(x => x.PeriodStartUtc <= to && x.PeriodEndUtc >= from);
```

`EconomyReportQueryService.GatherPlantAsync` dedupliserer deretter kun på eksakt periode-tuppel:

```csharp
var distinctPerPeriod = imports
    .GroupBy(i => (i.PeriodStartUtc, i.PeriodEndUtc))   // ulik slutt = ulik gruppe
    .Select(g => g.OrderByDescending(i => i.ImportedAtUtc).First())
    .ToList();
// ... deretter: spot += kpis["Spotomsetning_NOK"] for HVER gjenværende import
```

Siden A og B har ulik `PeriodEndUtc` havner de i hver sin gruppe, og **begge** legges til. Importen er innholdsbasert idempotent (IdempotencyKey = SHA256 av fil + plantId + periode), så to ulike filer gir to ulike rader som ikke overskriver hverandre.

### Effekt (kvantifisert)

For enhver periode som overlapper mai (ÅTD, Custom over mai, mai-måned):

| Tall | Verdi |
|---|---|
| Vikeså mai — korrekt (kun 1–20) | 366 397 |
| Vikeså mai — slik appen regner (A + B) | 555 748 |
| **Overrapportering, Vikeså mai spotoms** | **+189 351 (+52 %)** |
| Overrapportering på ÅTD-totalen (alle anlegg) | +189 351 (+0,23 %) |

Liten andel av totalen i dette tilfellet, men feilen er prinsipiell: ved hver re-import av en korrigert måned (med litt annen sluttidsstempel) vokser feilen, og den kan bli vilkårlig stor.

### Systemisk — samme mønster i KAIA-kostnad

`KaiaCostQueryService` bruker identisk dedup:

```csharp
.GroupBy(x => (x.PlantId!, x.PeriodStartUtc, x.PeriodEndUtc))
```

KAIA-kostnad for Vikeså mai dobbelttelles derfor på samme måte. Samme sårbarhet bør antas i alle tjenester som aggregerer settlement-importer med tuppel-dedup (også produksjon/MWh, oppgjør, ubalanse, og time-vekting av AF/FOR i samme løkke).

### Anbefalt fiks

Dedup må kollapse **overlappende** perioder, ikke bare identiske. To alternativer:

1. **Behold nyeste dekkende import per overlappsklynge.** Sorter importer på `PeriodStartUtc`, slå sammen overlappende intervaller, og velg for hver klynge den importen med størst dekning (eller nyeste `ImportedAtUtc`). Enkelt og robust når én import alltid dekker hele perioden den representerer.
2. **Tidslinje-deduplisering.** Hvis to importer dekker delvis overlappende men ikke-identiske spenn, klipp på timenivå så hver time telles én gang. Mer arbeid, men korrekt også ved ekte delvis-overlapp.

For dagens datamønster (hele måneder + én voksende inneværende måned) er **alternativ 1** tilstrekkelig. Legg det i en delt hjelpemetode som både `EconomyReportQueryService` og `KaiaCostQueryService` bruker.

### Foreslått enhetstest

Seed to importer for samme anlegg: `(mai 1–17)` og `(mai 1–20)`. Kall `GetAsync(YearToDate)`. Forvent at spotomsetning = kun 1–20-verdien, ikke summen.

---

## Funn 2 — AVKREFTET: Månedsgrense-lekkasje

Hypotese: at overlapp-filteret (`PeriodEndUtc >= from`) skulle dra forrige måned inn i en månedsspørring.

Avkreftet. `ExcelSettlementParser.BuildParsedSettlement` setter:

```csharp
var periodEnd = hourly.Max(r => r.TimeUtc);   // siste datapunkt i måneden
```

`PeriodEndUtc` er altså siste 15-min-stempel *inni* måneden (f.eks. 31.01 23:45), ikke neste måneds start. En februar-spørring (`from = 01.02`) får dermed `PeriodEnd(januar) < from` → januar-importen matcher ikke. Stemmer med at januar-summen min kontrollregning ga (19 644 120) er identisk med appens egen januar-verdi.

---

## Fasit — kontrollregning Spotomsetning 2026 (NOK)

Summert direkte fra detaljradene i kildefilene. Januar er verifisert mot appen (eksakt treff), og alle april-tall per anlegg matcher kjente app-verdier (Haukland 2 266 525, Øgreyfoss 7 202 942, Lindland 4 888 005) — metoden er dermed validert.

| Anlegg | Jan | Feb | Mar | Apr | ÅTD\* |
|---|---:|---:|---:|---:|---:|
| Drivdal | 532 213 | 301 307 | 1 532 703 | 860 687 | 3 226 910 |
| Grødemfoss | 2 318 169 | 1 947 862 | 2 188 924 | 1 888 838 | 8 343 793 |
| Haukland | 4 310 451 | 1 729 888 | 2 984 670 | 2 266 525 | 11 291 534 |
| Honnefoss | 1 359 519 | 1 139 772 | 1 279 778 | 1 078 640 | 4 857 709 |
| Liavatn | 303 498 | 254 479 | 210 733 | 121 932 | 890 642 |
| Lindland | 5 616 514 | 1 334 048 | 6 513 414 | 4 888 005 | 18 351 982 |
| Løgjen | 124 597 | 79 142 | 326 851 | 180 459 | 711 049 |
| Stølskraft | 4 151 | 4 521 | 16 232 | 142 624 | 167 528 |
| Vikeså | 1 187 005 | 482 373 | 2 821 869 | 1 857 498 | 6 715 141 |
| Øgreyfoss | 3 799 471 | 1 460 952 | 10 296 792 | 7 202 942 | 22 760 157 |
| Ørsdalen | 88 532 | 126 168 | 1 758 539 | 1 407 640 | 3 380 879 |
| **SUM** | **19 644 120** | **8 860 511** | **29 930 505** | **21 895 789** | **80 697 323** |

\* ÅTD = Jan–Apr + Vikeså mai 1–20 (366 397). Øvrige anlegg har ingen mai-data i kildefilene.

- **ÅTD korrekt:** 80 697 323
- **ÅTD slik appen regner (med dobbelttelling):** 80 886 674 (+189 351)

---

## Datakvalitet — flagg å sjekke (ikke regnefeil i appen)

1. **Februar ser lav ut.** Feb-summen (8,86 mill) er markant lavere enn Jan (19,6), Mar (29,9) og Apr (21,9). Filmappingen er riktig (01.02–28.02), men verdt å kontrollere at den gjenopprettede februar-fila (`done/_recovered-2026-05-29/dataeksport_20260429131224.xlsx`) faktisk inneholder hele måneden for alle anlegg.
2. **Mai er kun delvis importert** (kun Vikeså, til 20. mai). En ÅTD-spørring per i dag (29. mai) vil derfor mangle mai for 10 anlegg — KPI-en blir reelt sett ufullstendig, ikke feilberegnet.
3. **Utdaterte delmengde-importer bør ryddes.** Vikeså mai 1–17 er overflødig så lenge 1–20 finnes. Inntil dedup-fiksen er på plass bør den slettes fra DB for å unngå dobbelttelling. (Sletting må du gjøre selv — jeg gjør ikke permanente slettinger.)

---

## Live-verifisering (2026-05-30)

Hentet rå-JSON fra `/api/v1/economy?plants=all&kind=YearToDate` (Jan 1 – Jun 1, 2026) på kjørende app.

### 1. Import-fullstendighet — ÅTD per-anlegg matcher fasit eksakt

App-ens ÅTD-spotomsetning per anlegg er identisk med min Jan–Apr-fasit (krone for krone), som beviser at jan–apr er fullstendig importert for alle 11:

| Anlegg | App ÅTD | Fasit Jan–Apr | Match |
|---|---:|---:|:--:|
| Øgreyfoss | 22 760 157 | 22 760 157 | ✓ |
| Lindland | 18 351 982 | 18 351 982 | ✓ |
| Haukland | 11 291 534 | 11 291 534 | ✓ |
| Grødemfoss | 8 343 793 | 8 343 793 | ✓ |
| Honnefoss | 4 857 709 | 4 857 709 | ✓ |
| Ørsdalen | 3 380 879 | 3 380 879 | ✓ |
| Drivdal | 3 226 910 | 3 226 910 | ✓ |
| Liavatn | 890 642 | 890 642 | ✓ |
| Løgjen | 711 049 | 711 049 | ✓ |
| Stølskraft | 167 528 | 167 528 | ✓ |
| Vikeså | **6 904 492** | 6 348 744 (+mai) | ✗ se under |

`manglerImportHours`: 746 for alle unntatt Vikeså (266). Bekrefter at **mai mangler** for 10 anlegg.

### 2. Dobbelttellingen er bekreftet live

- App-ens portefølje-total spotomsetning ÅTD = **80 886 673** NOK.
- Min «buggy»-prediksjon (Jan–Apr + Vikeså mai dobbelttelt) = 80 886 674. **Treffer på krona.**
- Korrekt verdi = 80 697 323. **Overrapportering = 189 351 NOK.**
- Vikeså ÅTD = 6 904 492. Jan–Apr = 6 348 744. Differanse 555 748 = 189 351 (mai 1–17) + 366 397 (mai 1–20) → begge mai-importene summert. Bekreftet.

Funn 1 (overlapp-dobbelttelling) er altså ikke teoretisk — den vises i produksjonstallet akkurat nå.

> Merk: UI-en defaultet til **april (Måned)** ved åpning, så per-anleggs-tabellen viste april-tall. Det er ikke en feil i seg selv, men gjør at «Hittil i år» må velges aktivt for å se ÅTD.

### Gjenstår

- Full gjennomgang av øvrige faner (Nedetid, Effektivitet, Start/stopp) mot kildedata — kun økonomi er kontrollregnet i denne runden.
- Re-import av mai for de 9 hovedanleggene (se hovedsvar øverst).
