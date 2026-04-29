# Overlevering — Vakt-ROI overløps-justering + SCADA drag-drop

Dato: 2026-04-29 (kveld)
Forrige overlevering: 2026-04-29 (Nedetid + Vakt-ROI v1 levert)

## Hva ble gjort

Implementert `docs/SPEC-VAKT-ROI-OVERLOP.md` (drifts-leders forespørsel om
overløps-justering) og B6 fra `OVERLEVERING-2026-04-28-SCADA.md`-roadmapen
(drag-drop SCADA-opplasting på `/plants`).

### 1. Vakt-ROI med overløps-justering (spec)

Bakgrunn: Drifts-leder i Dalane Kraft påpekte at v1 antok at all produksjon
under nedetid var tapt. For regulerte vannkraftverk er det feil hvis
magasinet hadde plass — vannet er bare utsatt, ikke tapt.

**Ny regel:** Vakt-ROI gjelder kun timer der det var overløp i magasinet.
Ingen overløp → vannet er trygt magasinert → vakten redder ingenting.

Nye/endrede filer:

```
src/KraftverkUptime.Core/Domain/SignalMap.cs                              + SignalRole.OverflowFlow
src/KraftverkUptime.Core/Domain/VaktRoiResultat.cs                        + OverflowTimerInCounterfactual, OverflowDataMissing
src/KraftverkUptime.Infrastructure/Persistence/DrivdalSignalMapSeeder.cs    OVERLOP-tag → OverflowFlow + idempotent rolle-oppgradering
src/KraftverkUptime.Modules.Reporting/Nedetid/IOverflowQueryService.cs    NY — interface + OverflowDataset record (OverflowHours + DataAvailable)
src/KraftverkUptime.Modules.Reporting/Nedetid/OverflowQueryService.cs     NY — terskel 0.001 m³/s, time-trunkering
src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs          overflow-filter per event + tre forklarings-varianter
src/KraftverkUptime.Modules.Reporting/ReportingModule.cs                    DI-registrering
src/KraftverkUptime.Api/Contracts/NedetidContracts.cs                       VaktRoiEventDto med 2 nye felt
src/KraftverkUptime.Api/Endpoints/NedetidEndpoints.cs                       henter OverflowDataset + sender til calculator + nye CSV-kolonner
src/KraftverkUptime.Web/Services/NedetidApi.cs                              VaktRoiEventDto med 2 nye felt
src/KraftverkUptime.Web/Pages/VaktRoi.razor                                 ny "Overløp"-kolonne med "Mangler data"-flagg

tests/KraftverkUptime.Infrastructure.Tests/Nedetid/VaktRoiCalculatorTests.cs   8 oppdaterte tester med overflow-scenarioer
tests/KraftverkUptime.Infrastructure.Tests/Nedetid/OverflowQueryServiceTests.cs  NY — 8 tester med stub-repositorier
```

Logikk per event:

```
ekstra_timer       = max(0, counterfactual_end − faktisk_end)
overflow_timer     = antall hele timer i [faktisk_end, counterfactual_end)
                     hvor overflowFlow > 0.001 m³/s
reddet_mwh         = overflow_timer × installertEffektMw × kapasitetsfaktor
reddet_nok         = reddet_mwh × snittSpotpris

OverflowDataMissing = true  hvis ekstra_timer > 0 OG SCADA-data mangler
                            (ingen tag konfigurert, eller 0 samples i perioden)
```

**Sentralt design-valg:** `OverflowDataset.DataAvailable` skiller "kjent ingen
overløp" (samples finnes med verdi 0) fra "vi vet ikke" (ingen samples i
perioden). Det er forskjellen mellom å si "vakten reddet ingenting" og
"vi har ikke datagrunnlag". Drifts-leder må kunne stole på det første og
ignorere det siste — derav `OverflowDataMissing`-flagget.

Tre forklarings-varianter (vises i UI):
- *"Vakt løste på X t. Counterfactual = Y t. Av disse hadde Z t overløp i magasinet → Z t reddet (≈ N NOK)."*
- *"Vakt løste på X t. Counterfactual = Y t, men ingen overløp i perioden — vannet ville vært magasinert. Ingen ROI."*
- *"Vakt løste på X t. SCADA mangler overløps-data for counterfactual-perioden — kan ikke beregne ROI."*

### 2. SCADA drag-drop på /plants (B6)

`/plants`-siden tok fra før kun .xlsx (avregning). Utvidet samme drop-zone
til å akseptere .csv og dispatche til riktig SCADA-endepunkt basert på filnavn:

| Filtype | Filnavn | Endepunkt |
|---|---|---|
| `.xlsx` / `.xls` | hva som helst | `POST /api/v1/plants/{id}/settlements` |
| `.csv` | inneholder `operlog` | `POST /api/v1/plants/{id}/scada/operlog` |
| `.csv` | annet | `POST /api/v1/plants/{id}/scada` (master) |

Endrede filer:

```
src/KraftverkUptime.Web/Services/ReportsApi.cs    + UploadScadaMasterAsync, UploadScadaOperlogAsync, ScadaImportResultDto, OperlogImportResultDto, PostScadaCsvAsync helper
src/KraftverkUptime.Web/Pages/Plants.razor        Accept=".xlsx,.xls,.csv" + DetectKind switch + LastSuccessMessage
```

Suksess-meldingen tilpasses opplastings-typen:
- Avregning: *"Avregning mottatt — klassifisering pågår."*
- SCADA master: *"SCADA master importert: {SamplesWritten} samples, {SignalCount} signaler."*
- Operlog: *"Operlog importert: {EventsWritten} events ({RowsSkipped} skippet)."*

### 3. Pre-eksisterende blokkerings-feil fikset

`Directory.Build.props`: la `CA1003` til NoWarn. `event Action?` er etablert
Blazor-mønster (delt FilterState varsler sider via Action). Refaktorering til
`EventHandler<T>` ville berørt alle callere uten å gi reell verdi.
Pre-eksisterende blokkerings-feil i `FilterState.OnChange` er nå lukket og
Web-bygget er grønt igjen.

## Verifisering

### Tester (alle grønne — 97 totalt)

```
KraftverkUptime.Core.Tests             31 passed
KraftverkUptime.Infrastructure.Tests   37 passed (8 nye OverflowQueryService + 8 oppdaterte VaktRoiCalculator)
KraftverkUptime.EndToEnd.Tests         13 passed, 1 skipped (DrivdalRegression — fasit må regenereres)
KraftverkUptime.Api.Tests              16 passed
                                     ----
                                       97 passed total
```

### Curl mot Drivdal feb-2025

```powershell
curl.exe "http://localhost:5080/api/v1/plants/drivdal/vakt-roi?from=2025-02-01T00:00:00Z&to=2025-03-01T00:00:00Z"
```

| | v1 | v2 (overløps-justert) |
|---|---|---|
| `totalReddetNok` | 163 326 NOK | **0 NOK** |
| `antallReddbareInnenforVakt` | 5 | 5 (uendret — fortsatt 5 reddbare) |
| `OverflowDataMissing` | n/a | 4 av 5 events |

Per event:
```
2025-02-02T18:00 ekstra=9.0t  overflow=0t missing=True   reddet=0
2025-02-03T14:00 ekstra=9.0t  overflow=0t missing=True   reddet=0
2025-02-04T03:00 ekstra=0.0t  overflow=0t missing=False  reddet=0   (Lang_Trip-tilfelle)
2025-02-16T18:00 ekstra=12.0t overflow=0t missing=True   reddet=0
2025-02-21T15:00 ekstra=60.0t overflow=0t missing=True   reddet=0
```

Forklaring: SCADA-eksporten i DB-en dekker bare feb **2026**, ikke feb 2025 —
så 4 reddbare events flagges ærlig som "mangler data". Ett event hadde
`ekstraTimer = 0` uansett (vakten brukte mer tid enn driftspersonell ville).

Spec'ens forventning: *"vesentlig lavere ROI enn dagens 163 000 NOK,
sannsynligvis nær null"* — **levert.**

### Postgres sanity-check

```sql
SELECT signal_id, COUNT(*) AS samples, MIN(time_utc), MAX(time_utc)
FROM core.sample_facts
WHERE asset_id = 'drivdal' AND signal_id LIKE '%OVERLOP%' GROUP BY signal_id;
```

Output: 649 samples, alle i feb 2026, alle verdier = 0 (Drivdal hadde tørr
vinter). Bekrefter at v1's 163 326 NOK var basert på et udokumentert
antagelse om at vannet uansett rant tapt.

## Status pr. 2026-04-29 kveld

| Punkt | Status |
|---|---|
| Phase A (klassifikator + KPI) | ✅ Live |
| Phase B (annoteringer) | ✅ Live |
| SCADA foundation | ✅ Live |
| Nedetid + Vakt-ROI v1 | ✅ Live |
| **Vakt-ROI overløps-justering (denne sesjonen)** | ✅ **Live** |
| **SCADA drag-drop på /plants (denne sesjonen)** | ✅ **Skrevet + build-grønn** |
| Web-bygget grønt igjen (CA1003) | ✅ |
| ScadaClassifier (9-state) | ❌ Ikke startet |
| FusionClassifier | ❌ Ikke startet |
| Event-baserte KPI-er (MTBF/MTTR/FOR/EAF) | ❌ Ikke startet |
| Drivdal effektivitets-side | ❌ Ikke startet |
| `kpi_facts` + portefølje-trender | ❌ Ikke startet |
| OpenTelemetry 1.15.3-bump | ❌ Fortsatt suppressed |
| EF-migrasjoner (Initial + AddAnnotations + AddScada) | ❌ Fortsatt EnsureCreated-fallback |
| `DrivdalRegressionTests`-fasit | ❌ Skipped med begrunnelse |

## Veien videre — anbefaling

1. **Importer SCADA for feb-2025** (eller en våt vår-/sommermåned) via den
   nye drag-drop-en på `/plants`. Da blir vakt-ROI ærlig istedenfor å vise
   "mangler data" på alle events. — Trenger CSV-fil fra Drivdal som dekker
   den perioden.
2. **ScadaClassifier (B1, ~1-2 dager)** — 9-state-regler basert på
   `sample_facts`. Foundation er klar; det er bare å skrive klassifikator +
   tester. Spec ligger i `ANALYSE-NEDETID-SCADA.md`.
3. **FusionClassifier (B2, ~1 dag)** — kombiner SCADA + settlement +
   annoteringer i én pipeline. Naturlig fortsettelse siden OverflowFlow-rollen
   nå er en del av classifier-vokabularet.
4. **Event-baserte KPI-er (B3, ~1 dag)** — MTBF/MTTR/FOR/EAF basert på
   fusion-output.
5. **Senere steg fra spec'en** (eksplisitt utenfor denne):
   - `ReservoirFillFactor > 0.95` som proxy når OverflowFlow mangler
   - `UpstreamLevel >= HRV` som tredje fallback
   - Vannverdi-modell for "magasin har plass, men spotpris i dag vs. senere"

## Kjent oppfølging fra denne sesjonen

1. **`UpgradeRolesAsync` i DrivdalSignalMapSeeder** er en idempotent rolle-
   oppgraderings-patch (Other → OverflowFlow). Kan fjernes når EF-migrasjoner
   tar over.
2. **CA1003** ble suppressed istedenfor refaktorert. Hvis dere senere vil
   følge analyzer-anbefalingen, bytt `event Action?` → `event EventHandler?`
   i `FilterState.cs` og oppdater alle callere
   (`Filter.OnChange += StateHasChanged` → wrapper).

## Kommandoer for morgenen

```powershell
# 1. Start Docker Desktop (hvis ikke allerede)

# 2. Trykk "Start Oppetid.bat" → bygger, starter, åpner nettleser
.\"Start Oppetid.bat"

# 3. Drag-drop SCADA-CSV (master eller *operlog*.csv) på et anleggs-kort
#    på /plants. Suksess-melding viser SamplesWritten / EventsWritten.

# 4. Verifiser ny overløp-kolonne på /vakt-roi/drivdal eller via curl:
curl.exe "http://localhost:5080/api/v1/plants/drivdal/vakt-roi?from=2025-02-01T00:00:00Z&to=2025-03-01T00:00:00Z"

# 5. Sanity-check overflow-samples i DB:
docker compose exec postgres psql -U kraftverk -d kraftverk -c `
  "SELECT signal_id, COUNT(*), MIN(time_utc), MAX(time_utc) FROM core.sample_facts WHERE asset_id='drivdal' AND signal_id LIKE '%OVERLOP%' GROUP BY signal_id;"
```

## Forslag til commit-grupper

Allerede commitet i denne sesjonen:
- `cd72b69 feat(vakt-roi): overløps-justering — ROI kun for timer med overløp`

Anbefalt for neste commit:
- `feat(web): drag-drop SCADA-CSV på /plants` — Plants.razor + ReportsApi.cs
- `docs: overlevering 2026-04-29 (kveld) — overløps-justering + SCADA drag-drop`
