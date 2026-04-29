# Autonomt veikart — Claude Code-instruksjon

**Dato:** 2026-04-29
**Sluttmål:** Alle 11 anlegg skal ha **samme funksjoner og data tilgjengelig**: nedetids-analyse, vakt-ROI med overløp + ubalanse, effektivitets-side, 9-state-klassifisering, event-baserte KPI-er, og portefølje-dashboard. Når veikartet er ferdig: ingen funksjonell forskjell mellom Drivdal og de øvrige.

## Hvordan bruke dette dokumentet

1. Les denne fila og `OVERLEVERING-2026-04-29.md` først.
2. Jobb gjennom Stegene 0-7 i rekkefølge.
3. Etter hvert hovedsteg: kjør `dotnet build`, `dotnet test`, commit, oppdater status-tabellen nederst.
4. **Stopp og spør brukeren** kun ved "BRUKER-INPUT KREVES"-flagg. Ellers: fortsett.
5. Hvis du støter på en blokker, legg notat i status-tabellen og hopp til neste ikke-blokkerte steg.

Hovedprinsipp: hver funksjon skal være **anlegg-uavhengig**. Ingen hardkoding av "Drivdal" — alt må fungere likt for alle 11.

## Anlegg-portefølje

| Plant-ID | Navn | Operlog | InstalledCapacityMw |
|---|---|---|---|
| `drivdal` | Drivdal | ✓ | 2.2 (satt) |
| `logjen` | Løgjen | – | – |
| `grodemfoss` | Grødemfoss | – | – |
| `haukland` | Haukland | – | – |
| `honnefoss` | Honnefoss | – | – |
| `lindland` | Lindland | – | – |
| `ogreyfoss` | Øgreyfoss | – | – |
| `orsdalen` | Ørsdalen | – | – |
| `liavatn` | Liavatn | – | – |
| `vikesa` | Vikeså | – | – |
| `stolskraft` | Stølskraft | – | – |

Status oppdateres etter hvert som data og effekt-tall fylles inn.

---

## Steg 0 — Multi-anleggs-import

**Spec:** `docs/SPEC-MULTIPLANT-IMPORT.md`
**Estimat:** 4-6 t

Akseptansekriterier:
1. `dotnet build` + `dotnet test` grønt
2. Curl-verifikasjon i specens "Verifisering"-seksjon kjører grønt
3. `core.plants` har 11 rader
4. `core.settlement_imports` får 9 nye rader for feb-2026 etter test-import
5. Alle anlegg auto-opprettes med korrekte kanoniske navn (`Løgjen`, `Grødemfoss` osv.) selv om fane-navn mangler norske tegn

---

## Steg 1 — Plant-admin-side (sett InstalledCapacityMw)

**Estimat:** 2-3 t

Bygg `/plants/{plantId}/admin` med skjema for `Name`, `Type`, `InstalledCapacityMw`, `TimeZone`. Endepunkt: `PUT /api/v1/plants/{plantId}`.

På `/plants`-listen: marker rader med `InstalledCapacityMw = 0` med rødt flagg + "Krever oppsett"-tooltip.

**BRUKER-INPUT KREVES** etter at koden er ferdig: brukeren må fylle inn faktiske MW-tall for de 10 nye anleggene. Pause her og merk i status-tabellen som "Klar til bruker-input". Brukeren kan også fylle inn via SQL — i så fall er steget komplett.

---

## Steg 2 — Effektivitets-side

**Spec-grunnlag:** `ANALYSE-VIRKNINGSGRAD.md`
**Estimat:** 1 dag

Bygg `/effektivitet/{plantId}`:
- η-kurve scatterplot (effekt vs virkningsgrad)
- Sweet-spot-deteksjon (algoritmisk, ikke hardkodet)
- Spesifikt vannforbruk (m³/kWh)
- KPI-kort: snitt η, sweet-spot-effekt, snitt vannforbruk

Backend: `Modules.Reporting.Effektivitet` med `IEffectivityQueryService`. Endepunkt: `GET /api/v1/plants/{plantId}/effektivitet?from=&to=`.

Verifisering mot Drivdal feb-2026: `snittEta ≈ 0.887`, `sweetSpotEffekt ≈ 1900 kW`, `snittSpesifiktVannforbruk ≈ 4.29`.

**Anlegg-uavhengig:** algoritmen finner sweet-spot fra dataen, ikke hardkodede verdier. Skal fungere for alle 11 anlegg når SCADA-data er tilgjengelig.

---

## Steg 3 — ScadaClassifier (9-state)

**Spec-grunnlag:** `ANALYSE-NEDETID-SCADA.md`
**Estimat:** 1-2 dager

9 tilstander: InService, ForcedOutage, ForcedDerating, MaintenanceOutage, PlannedOutage, PlannedDerating, ResourceUnavailable, ReserveShutdown, InformationUnavailable.

Implementer `Modules.Scada.Classification.ScadaClassifier` som tar `IScadaSampleRepository`-data og returnerer `ClassifiedHourlyRow`-liste. Tester for hver state.

---

## Steg 4 — Event-baserte KPI-er

**Estimat:** 1 dag

Implementer MTBF, MTTR, FOR, EAF i KPI-katalogen.

Formler:
- MTBF = total_drift_timer / antall_FO_events
- MTTR = total_FO_timer / antall_FO_events
- FOR = FOH / (FOH + SH)
- EAF = (AH − POH − MOH − EFDH) / period_hours

Tester med kjent input-set + edge cases.

---

## Steg 5 — FusionClassifier

**Estimat:** 1 dag

Kombinerer settlement + SCADA + operlog + annoteringer. Konfliktløsning:
1. Annotering > alt
2. Settlement og SCADA enige → bruk
3. Uenige → SCADA for drifts-tilstand, settlement for økonomisk
4. Operlog beriker uten å overstyre time-state

Oppdater `NedetidQueryService.ListEventsAsync` til å bruke fusion. UI får automatisk mer presise events.

---

## Steg 6 — kpi_facts + portefølje-dashboard

**Estimat:** 1-2 dager

Ny tabell `core.kpi_facts` (plant_id, period_start, period_end, kpi_name, kpi_value, period_kind).

Jobb som beregner KPI-er per måned per anlegg og lagrer her.

Ny side `/portefolje`:
- Tabell: anlegg som rader, KPI-er som kolonner
- Sortering, periode-velger (kvartal/år)
- Multi-line graf for valgt KPI på tvers
- "Topp 5 tap-events siste måned"-widget

Endepunkt: `GET /api/v1/portfolio/kpis?period=2026-02&kind=monthly`.

**Anlegg-uavhengig:** tabellen viser alle anlegg som har data, ikke en hardkodet liste.

---

## Steg 7 — Operlog-import for andre anlegg

**BRUKER-INPUT KREVES:** operlog-CSV per anlegg.

Generaliser `OperlogCsvParser.MapEvent` til å være anlegg-uavhengig — kun se på suffix (`_AL`, `STARTER_AL`, `STOPPER_AL`, `FEIL_AL`), ikke prefiks.

UI: drag-drop på `/plants/{plantId}` skal støtte både master-CSV og operlog-CSV (detekter format basert på header).

Pause her og vent på brukerens operlog-filer per anlegg.

---

## Sluttverifikasjon — alle 11 anlegg har lik funksjonalitet

Etter at Steg 0-6 er ferdig (og Steg 1 + 7 har fått bruker-input), kjør denne sjekken:

```powershell
$plants = @('drivdal','logjen','grodemfoss','haukland','honnefoss','lindland','ogreyfoss','orsdalen','liavatn','vikesa','stolskraft')
foreach ($p in $plants) {
    $r = curl.exe "http://localhost:5080/api/v1/plants/$p/nedetid?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" 2>$null | ConvertFrom-Json
    $v = curl.exe "http://localhost:5080/api/v1/plants/$p/vakt-roi?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" 2>$null | ConvertFrom-Json
    Write-Host "$p`: $($r.antallEvents) events, $($v.totalReddetNok) NOK reddet"
}
```

Forventet: alle 11 returnerer gyldige tall (ikke nødvendigvis identiske — anleggene har forskjellig drift). Anlegg uten operlog skal fortsatt fungere, bare uten sub-time-presisjon.

Test også portefølje-dashboardet:

```
http://localhost:5180/portefolje
```

Forventet: alle 11 anlegg listet med KPI-er for valgt periode.

---

## Status-tabell (oppdater etter hvert steg)

| Steg | Status | Commit | Notater |
|---|---|---|---|
| 0 — Multi-anleggs-import | ⏳ | – | Spec: `docs/SPEC-MULTIPLANT-IMPORT.md` |
| 1 — Plant-admin-UI | ⏳ | – | Avhenger av 0; bruker-input til effekt-tall |
| 2 — Effektivitets-side | ⏳ | – | Anlegg-uavhengig algoritme |
| 3 — ScadaClassifier (9-state) | ⏳ | – | Krever SCADA-data per anlegg |
| 4 — Event-KPI-er | ⏳ | – | Avhenger av 3 |
| 5 — FusionClassifier | ⏳ | – | Avhenger av 3 |
| 6 — Portefølje-dashboard | ⏳ | – | Avhenger av 4 |
| 7 — Operlog andre anlegg | ⏳ Bruker-input | – | Trenger CSV-er fra Morten |

Statuskoder: ⏳ pending, 🔨 in progress, ✅ done, ⚠️ blokkert (se notater)

---

## Spørre-policy

- **Tekniske valg** (bibliotek, navngiving, struktur): bestem selv basert på eksisterende mønstre i kodebasen
- **Forretningslogikk** (terskler, kategori-mapping, KPI-formel): bruk default fra ANALYSE-*.md-filer. Hvis fortsatt uklart, bruk konservativ default + notér
- **Brukerdata** (effekt-tall, operlog-CSV): pause og marker bruker-input-kreves
- **Modellvalg som påvirker rapporterte tall til drifts-leder** (ny KPI-formel, endret beregning): pause og spør brukeren via Cowork

Du har full tillit til alt annet. Bygg, test, commit, oppdater veikart, gå videre.

## Når alt er ferdig

Skriv ny overlevering `OVERLEVERING-2026-MM-DD.md` og foreslå neste prioriteter:
- Vannverdi-modell (ekte alternativkost)
- TimescaleDB-bytte (når sample-volumet vokser)
- Multi-tenant (flere selskaper per database)
- Live SCADA-integrasjon (Modbus/OPC UA istedenfor CSV-import)
- Mobile UI for drifts-leder
