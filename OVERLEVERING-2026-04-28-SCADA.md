# Overlevering — SCADA-foundation deployt + verifisert

Dato: 2026-04-28 (kveld)
Forrige overlevering: 2026-04-28 (Phase B-deploy)

## Hva ble gjort

### UI-forbedringer på rapport-siden
- KPI-katalog gruppert i 3 ekspansjons-paneler (Drift / Marked / Økonomi). Drift utvidet by default. Søk åpner alle paneler automatisk.
- Driftstidslinje-legend gruppert i 4 hovedklasser (Drift / Nedetid / Planlagt / Annet). Reduserer visuell støy uten å miste celle-fargene.
- Annoterings-dialog viser nå appens nåværende klassifisering for valgte timer ("Drift (3 t)" eller blandet "2t Drift, 1t Nedetid").
- /plants drag-drop fikset — input-overlay stopper 64 px fra bunnen så kort-knappene fortsatt er klikkbare.

### SCADA foundation komplett
Tre nye Postgres-tabeller automatisk opprettet ved oppstart via `EnsureScadaSchemaAsync` i `DatabaseBootstrapper`:

| Tabell | Innhold |
|---|---|
| `core.signal_map` | Whitelist per anlegg — Drivdal seedet med 22 tags mappet til SignalRole |
| `core.sample_facts` | Time-aggregert SCADA-data (asset_id, signal_id, time_utc, value, quality) |
| `core.classified_events` | Sub-time event-rader fra operlog + (fremtidig) klassifikator |

TimescaleDB-extension forsøkes aktivert ved oppstart; hvis postgres-imaget ikke har den, faller systemet tilbake til vanlige tabeller uten skala-forskjell.

### Nytt Modules.Scada-prosjekt
- Domain types: `SignalMap`, `SignalRole` (17 roller), `ScadaSample`, `ClassifiedEvent`
- Repository-kontrakter: `ISignalMapRepository`, `IScadaSampleRepository`, `IClassifiedEventRepository`
- Parser-er: `ScadaMasterCsvParser` (Drivdal-format), `OperlogCsvParser` (event-mapping)
- `IScadaImportService` + EF-impl i Infrastructure

### To nye API-endepunkter
- `POST /api/v1/plants/{plantId}/scada` — multipart .csv master-data
- `POST /api/v1/plants/{plantId}/scada/operlog` — multipart .csv operlog

Begge anonyme i v1 (samme som settlements). Buffer-er hele CSV til minne før parsing for å unngå ASP.NET 10's "synchronous IO disallowed".

### Drivdal feb-2026 importert og verifisert

| Datapunkt | Verdi |
|---|---|
| samples | 14 278 (22 signaler × 649 timer) |
| classified_events | 64 (start/stopp/alarm fra operlog, 159 settpunkt-endringer skippet) |
| signal_map | 22 (alle aktive) |

Reelle verdier hentet fra dataen:
- Drivdal kjørte 90 av 649 timer (13.9%) i februar
- η-gjennomsnitt 88.7%, sweet-spot 92.4% @ 1800-1999 kW
- Spesifikt vannforbruk 4.29 m³/kWh

### Arkitektur-dokumenter for fortsettelsen
- `ARKITEKTUR-SCADA.md` — TimescaleDB, hypertables, kpi_facts, retensjon, multi-bruker-skala
- `ANALYSE-NEDETID-SCADA.md` — 9-state klassifiserings-regler, fusion-logikk, MTBF/MTTR/FOR/EAF
- `ANALYSE-VIRKNINGSGRAD.md` — 8 nye effektivitets-KPI-er, dashboards, SCADA-η formler

## Status pr. 2026-04-28 kveld

| Punkt | Status |
|---|---|
| Phase A — klassifikator + KPI | ✅ Live |
| Phase B — annoteringer | ✅ Live |
| UI-forbedringer (KPI-grupper, legend-grupper, dialog-info) | ✅ Live |
| /plants drag-drop til opplasting | ✅ Fungerer |
| SCADA foundation (tabeller, modul, importer) | ✅ Deployt + verifisert med ekte data |
| TimescaleDB-bytte | ❌ Ikke gjort (vanlig postgres fungerer fint i dette volumet) |
| ScadaClassifier (9-state) | ❌ Ikke startet |
| FusionClassifier (kombinerer SCADA + settlement + annoteringer) | ❌ Ikke startet |
| Event-baserte KPI-er (MTBF, MTTR, FOR, EAF) | ❌ Ikke startet |
| kpi_facts-tabell + portefølje-dashboard | ❌ Ikke startet |
| Drivdal effektivitets-side | ❌ Ikke startet |

## Bugs fikset i denne sesjonen

1. `Color`-ambiguitet mellom `ApexCharts.Color` og `MudBlazor.Color` i ReportDetail.razor
2. Razor-feil: mixed content i `Class`-attributt på MudFileUpload
3. `DisableGutters` → `Gutters="false"` (MudBlazor 8.x rename)
4. `UTF8Encoding`-konstruktør: `detectEncodingFromByteOrderMarks` flyttet til `StreamReader`
5. CA1068: `CancellationToken` flyttet til siste parameter i `ProcessUploadAsync`
6. Synchronous IO disallowed: bufrer CSV til MemoryStream før parsing
7. /plants drop-zone blokkerte knapper: input-overlay forkortet med 64 px bunn-margin

## Neste sesjon — anbefalt rekkefølge

**Prioritet 1 — leverbart raskt (drifts-leder-presentasjon):**

| # | Steg | Estimat |
|---|---|---|
| 1 | **Nedetids-analyse v1** for drifts-leder: hendelses-tabell, total nedetid per kategori, tap i NOK, trend-graf, CSV-eksport. Bruker eksisterende settlement-klassifisering + operlog-events vi allerede har importert. Side `/nedetid/{plantId}`. | **1 dag** |
| 2 | **Vakt-ROI-analyse**: kost-ved-å-vente-til-neste-arbeidsdag-per-event. Counterfactual end-time = neste arbeidsdag 08:00 hvis trip skjer etter 15:00. Tap = effekt × spotpris × ekstra timer. Aggregert årssum = vakt-tjenestens verdi. Side `/vakt-roi/{plantId}`. | **1.5 dag** etter steg 1 |
| 3 | Drivdal effektivitets-side (η-kurve, sweet-spot, spesifikt vannforbruk) | 1 dag |

Disse tre gir ledelsen tre konkrete leveranser fra samme datakilde i løpet av 3-4 dager.

**Prioritet 2 — presisjons-løft:**

| # | Steg | Estimat |
|---|---|---|
| 4 | `ScadaClassifier` med 9-state-regler basert på `sample_facts` | 1-2 dager |
| 5 | `FusionClassifier` (SCADA + settlement + annoteringer) | 1 dag |
| 6 | Nye event-baserte KPI-er (MTBF/MTTR/FOR/EAF) | 1 dag |

**Prioritet 3 — portefølje-skala:**

| # | Steg | Estimat |
|---|---|---|
| 7 | `kpi_facts`-tabell + portefølje-trender på tvers av anlegg | 1-2 dager |
| 8 | UI for SCADA-opplastning (drag-drop på /plants for .csv) | 2-3 t |

Foundation er solid — alle disse stegene bygger på eksisterende `sample_facts` / `classified_events` / `signal_map`-data uten å touche backend-foundationen.

## Vakt-ROI: åpne spørsmål før implementasjon

Disse må avklares før Vakt-ROI-side bygges (steg 2):

1. **Vakttider** — er det 15:00-08:00 hverdager + helg/helligdag? Eller annet?
2. **Responstid med vakt** — typisk 1-2 t? Konstant eller anleggs-spesifikk?
3. **Counterfactual responstid uten vakt** — neste arbeidsdag 08:00? Eller mer realistisk modell (driftspersonell oppdager feilen X timer etter kl 08)?
4. **Fjernreset/gjenstart fra hjemmekontor** — er det "vakt" eller "uten vakt"?
5. **Skal feil som krever fysisk oppmøte** (turbin-skade) skilles fra feil som kan resettes eksternt (relé-fall)?

Disse svarene avgjør hvilken kost-modell vi velger. Default-foreslag: 15-08 hverdager + helg, 1.5 t responstid med vakt, 16 t (neste 08) uten vakt, ingen skille mellom fjernreset/oppmøte i v1.

## Eksempel-beregning Vakt-ROI

Drivdal har installert 2.2 MW. Hvis det er én trip kl. 16:00 onsdag som ble fikset kl. 17:30 med vakt:

- Med vakt: 1.5 t nedetid
- Uten vakt: vent til torsdag 08:00 = 16 t nedetid
- Ekstra nedetid: 14.5 t
- Snittpris × effekt: 2.0 MW × 850 NOK/MWh × 14.5 t ≈ 24 700 NOK i tapt produksjon

5-10 slike events i året: 125 000 - 250 000 NOK reddet per år.
Sammenlign mot årlig vakt-bemanningskostnad → ROI på vakt-ordningen.

## Kommandoer for morgenen

```powershell
# Status
git status

# Sjekk at SCADA-data fortsatt er der
docker compose exec postgres psql -U kraftverk -d kraftverk -c "SELECT count(*) FROM core.sample_facts;"

# Hvis ny CSV kommer for andre anlegg, samme mønster:
curl.exe -X POST "http://localhost:5080/api/v1/plants/{plantId}/scada" `
         -F "file=@path-to-csv;type=text/csv"

# Bygg etter endringer
docker compose up -d --build api worker
```

## Forslag til commit-grupper

```
1. feat(web): KPI-katalog gruppering + driftslinje legend-grupper
2. feat(web): Plants drag-drop fiks + dialog viser nåværende klassifisering
3. feat(scada): Modules.Scada foundation + EF tabeller + bootstrap
4. feat(scada): CSV-importer (master + operlog) + Drivdal signal-map
5. docs: ARKITEKTUR-SCADA.md, ANALYSE-NEDETID-SCADA.md, ANALYSE-VIRKNINGSGRAD.md
6. docs: overlevering 2026-04-28-SCADA
```

## Åpne spørsmål

1. **TimescaleDB-bytte** — vurderes når sample-volumet vokser. Foundation er klar; det er bare å bytte image og restarte.
2. **SCADA-η-formel** — er `TURB_VIRKNGRD_PV` ren turbin (mekanisk) eller total (turbin × generator)? Påvirker `EgenBeregnetVirkningsgrad`-validering.
3. **Tag-navn-konvensjon andre anlegg** — Drivdal bruker `DRIVDAL_*`-prefiks. Trenger ny seeder per anlegg når de kobles inn.
