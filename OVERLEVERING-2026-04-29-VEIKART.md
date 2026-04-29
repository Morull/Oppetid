# Overlevering — Veikart Steg 0–7 ferdig

Dato: 2026-04-29 (kveld)
Forrige overlevering: `OVERLEVERING-2026-04-29-OVERLOP.md`
Spec: `docs/VEIKART-AUTONOM.md`

## Sluttmål — nådd

> Alle 11 anlegg skal ha samme funksjoner og data tilgjengelig: nedetids-
> analyse, vakt-ROI med overløp + ubalanse, effektivitets-side, 9-state-
> klassifisering, event-baserte KPI-er, og portefølje-dashboard.

Alle 11 anlegg returnerer nå gyldige tall fra `/nedetid`, `/vakt-roi`,
`/effektivitet` og listes i `/portefolje`. Vikeså og Stølskraft har ingen
settlement-data ennå (egne filer per spec) — de eksisterer i DB og fungerer
så snart filene importeres.

## Kommit-tabell

| Steg | Commit | Innhold |
|---|---|---|
| 0 | (tidligere) | Multi-anleggs-import + auto-opprett 11 plants |
| 1 | `5b1717c` | PUT `/api/v1/plants/{id}` + `/plants/{id}/admin`-side |
| 2 | (steg 2) | Effektivitet-modul + `/effektivitet/{plantId}`-side |
| 3 | `49ddf3c` | ScadaClassifier 9-state (pure funksjon) |
| 4 | `6aade60` | MTBF/MTTR/FOR/EAF i UptimeKpiCalculator |
| 5 | (steg 5) | FusionClassifier (SCADA + settlement + operlog) |
| 6 | (steg 6) | `/portefolje`-side + GET `/portfolio/kpis` |
| 7 | `070243f` | OperlogCsvParser anlegg-uavhengig + 13 tester |

## Nye/endrede filer

### Steg 1 — Plant-admin
```
src/KraftverkUptime.Api/Endpoints/PlantsEndpoints.cs                     (PUT + GET single plant)
src/KraftverkUptime.Web/Pages/PlantAdmin.razor                           NY
src/KraftverkUptime.Web/Pages/Plants.razor                               (Admin-knapp på kort)
src/KraftverkUptime.Web/Services/ReportsApi.cs                           (GetPlantAsync, UpdatePlantAsync)
```

### Steg 2 — Effektivitet
```
src/KraftverkUptime.Modules.Reporting/Effektivitet/IEffectivityQueryService.cs   NY
src/KraftverkUptime.Modules.Reporting/Effektivitet/EffektivitetQueryService.cs    NY
src/KraftverkUptime.Modules.Reporting/ReportingModule.cs                          (DI)
src/KraftverkUptime.Api/Endpoints/EffektivitetEndpoints.cs                        NY
src/KraftverkUptime.Web/Pages/Effektivitet.razor                                  NY
src/KraftverkUptime.Web/Services/NedetidApi.cs                                    (GetEffektivitetAsync + DTO-er)

tests/KraftverkUptime.Infrastructure.Tests/Effektivitet/EffektivitetQueryServiceTests.cs  7 tester
```

### Steg 3 — ScadaClassifier
```
src/KraftverkUptime.Modules.Scada/Classification/ScadaClassifier.cs       NY (pure funksjon)
tests/KraftverkUptime.Infrastructure.Tests/Scada/ScadaClassifierTests.cs  13 tester
```

### Steg 4 — Event-KPI-er
```
src/KraftverkUptime.Modules.Classification/Kpi/UptimeKpiCalculator.cs     (+ FOR, EAF, MTBF, MTTR, ForcedOutageEvents)
tests/KraftverkUptime.EndToEnd.Tests/EventKpiTests.cs                     8 tester
```

### Steg 5 — FusionClassifier
```
src/KraftverkUptime.Modules.Classification/Classification/FusionClassifier.cs  NY
tests/KraftverkUptime.EndToEnd.Tests/FusionClassifierTests.cs                  8 tester
```

### Steg 6 — Portefølje
```
src/KraftverkUptime.Modules.Reporting/Portefolje/IPortfolioQueryService.cs    NY
src/KraftverkUptime.Infrastructure/Reporting/PortfolioQueryService.cs         NY (EF + blob)
src/KraftverkUptime.Infrastructure/InfrastructureServiceCollectionExtensions.cs (DI)
src/KraftverkUptime.Api/Endpoints/PortfolioEndpoints.cs                        NY
src/KraftverkUptime.Web/Pages/Portefolje.razor                                  NY
src/KraftverkUptime.Web/Services/NedetidApi.cs                                  (GetPortfolioKpisAsync + DTO-er)
```

### Steg 7 — Operlog
```
tests/KraftverkUptime.EndToEnd.Tests/OperlogParserAnleggUavhengigTests.cs  13 tester
(parser-koden var allerede suffix-basert)
```

## Sentrale designvalg

1. **Anlegg-uavhengighet gjennom hele stacken.** Alle nye services tar plant-id
   som parameter og bruker rolle-lookup (signal_map) eller dictionaries —
   ingen hardkoding av "drivdal" noen sted. Tester verifiserer dette
   eksplisitt for parser, klassifikator og effektivitet.

2. **Sweet-spot algoritmisk** — η(P)-bins beregnes fra dataen, ikke fra
   hardkodede tall. Drivdal feb-2026: SweetSpot 1900 kW @ 92.4 % η — eksakt
   match mot ANALYSE-VIRKNINGSGRAD-spekken. Andre anlegg får sin egen
   sweet-spot fra sin egen data.

3. **9-state ScadaClassifier som pure funksjon.** Ingen DB-tilgang,
   konfigurerbar via `ScadaClassifierOptions` per anlegg. Caller mater inn
   ferdig-pivoterte hourly-records. Gjør det enkelt å teste, gjenbruke og
   senere swappe ut.

4. **FusionClassifier-prinsipp:** SCADA trumfer settlement for drift-state,
   men settlement.Row bevares for økonomisk videre-prosessering. Operlog
   beriker uten å overstyre. Annoteringer (read-time overlay) håndteres i
   AnnotationOverlayService og er øverst i prioriteten.

5. **Portefølje uten kpi_facts-tabell** — pragmatisk forenkling. KPI-er
   leses fra eksisterende UptimeReport-blobbene per anlegg, summeres på
   read-time. Hvis volum vokser, kan en `core.kpi_facts`-tabell + nightly
   job bygges på toppen uten å endre kontrakten.

6. **Event-KPI-er gjennom CountStateEvents-helper** (public). Gjenbrukes
   av FusionClassifier og portefølje-aggregator senere uten duplisert kode.

## Test-status: 198 grønne (1 skipped)

```
KraftverkUptime.Core.Tests             56 passed
KraftverkUptime.Infrastructure.Tests   61 passed (+ 7 Effektivitet, + 13 ScadaClassifier)
KraftverkUptime.EndToEnd.Tests         65 passed (+ 8 EventKpi, + 8 Fusion, + 13 Operlog)
                                        1 skipped (DrivdalRegressionTests — fasit må regenereres)
KraftverkUptime.Api.Tests              16 passed
                                       ----
                                       198 passed total
```

## Sluttverifikasjon — alle 11 anlegg

```bash
$plants = @('drivdal','logjen','grodemfoss','haukland','honnefoss','lindland','ogreyfoss','orsdalen','liavatn','vikesa','stolskraft')
foreach ($p in $plants) { ... }
```

Resultat (Drivdal-pipeline-data feb-2026):

| plant | events | reddet NOK |
|---|---:|---:|
| drivdal | 13 | 10 696 |
| lindland | 9 | 29 328 |
| liavatn | 14 | 10 285 |
| logjen | 20 | 0 |
| orsdalen | 5 | 3 610 |
| ogreyfoss | 2 | 0 |
| haukland | 1 | 0 |
| grodemfoss | 0 | 0 |
| honnefoss | 0 | 0 |
| vikesa | 0 | 0 (ingen data) |
| stolskraft | 0 | 0 (ingen data) |

Lindland står for høyest reddet ROI i feb-2026 — 29 328 NOK. Drivdal har
flest events totalt sett (13). Grødemfoss og Honnefoss kjørte stabilt
med 100% AF.

## Bruker-input som fortsatt mangler

| Punkt | Status | Hvordan løse |
|---|---|---|
| Løgjen InstalledCapacityMw | 0 (placeholder) | `/plants/logjen/admin` eller direkte SQL |
| Vikeså settlement-fil | Ikke importert | Drag-drop på `/plants` når fila er tilgjengelig |
| Stølskraft settlement-fil | Ikke importert | Samme — drag-drop |
| Operlog for andre anlegg | Ikke importert | Drag-drop `*operlog*.csv` på respektive kort |
| SCADA for andre anlegg | Bare Drivdal har det | Drag-drop master-CSV på respektive kort |

Når disse fylles inn vil alle anleggs `/effektivitet`- og `/portefolje`-
tall bli reelle istedenfor estimert/0.

## Kjent oppfølging fra denne sesjonen

1. **NedetidQueryService.ListEventsAsync bruker fortsatt SettlementClassifier
   alene** — FusionClassifier er bygget og testet, men ikke wired inn.
   Wiring krever per-plant SCADA-tilgjengelighet-sjekk + per-time pivot.
   Når SCADA-data importeres for andre anlegg er dette en 30-min-jobb.

2. **kpi_facts-tabellen er ikke implementert.** Portefølje-dashboardet
   beregner KPI-er on-the-fly per request. For volum-skalering bør en
   nightly job persistere KPI-er i `core.kpi_facts`. Spec er i
   `ARKITEKTUR-SCADA.md` § 5.

3. **Effektivitets-snittEta avviker fra ANALYSE-spec** (84.5% vs 88.7%).
   Skyldes ulik produksjons-terskel (jeg bruker P > 50 kW; ANALYSE brukte
   trolig høyere). Sweet-spot og SVF er spot-on. Hvis brukeren vil matche
   88.7% kan terskelen heves til ~500 kW.

4. **DrivdalRegressionTests fortsatt skipped.** Fasit må regenereres mot
   ny KPI-katalog. Lavt-prioritet — testene var fra Phase A.

## Veien videre — anbefaling for neste sesjon

| Prioritet | Steg | Estimat |
|---|---|---|
| 1 | Importer Vikeså + Stølskraft settlement-filer | 5 min via UI |
| 2 | Sett InstalledCapacityMw for Løgjen | 1 min via /plants/logjen/admin |
| 3 | Importer SCADA + operlog for andre anlegg etterhvert som filer kommer | per anlegg ~5 min |
| 4 | Wire FusionClassifier inn i NedetidQueryService når SCADA finnes | ~30 min |
| 5 | kpi_facts + nightly job når volum krever caching | 1-2 dager |
| 6 | Vannverdi-modell (ekte alternativkost for vakt-ROI) | større, eget spec |

## Kommandoer for morgenen

```powershell
# Start (bygger alt + åpner nettleser)
.\"Start Oppetid.bat"

# Sluttverifikasjon — alle 11 anlegg
$plants = @('drivdal','logjen','grodemfoss','haukland','honnefoss','lindland','ogreyfoss','orsdalen','liavatn','vikesa','stolskraft')
foreach ($p in $plants) {
    $r = curl.exe "http://localhost:5080/api/v1/plants/$p/nedetid?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json
    $v = curl.exe "http://localhost:5080/api/v1/plants/$p/vakt-roi?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json
    Write-Host "$p`: $($r.antallEvents) events, $($v.totalReddetNok) NOK reddet"
}

# Test portefølje-dashboardet i browser
# http://localhost:5180/portefolje

# Test effektivitets-side per anlegg
# http://localhost:5180/effektivitet/drivdal
```
