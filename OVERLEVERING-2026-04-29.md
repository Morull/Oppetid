# Overlevering — Nedetids-analyse + Vakt-ROI v1 levert

Dato: 2026-04-29
Forrige overlevering: 2026-04-28-SCADA (kveld)
Verktøy brukt denne sesjonen: Cowork

## Hva ble gjort

### Prioritet 1 fra forrige overlevering — levert

| Steg | Status |
|---|---|
| Nedetids-analyse v1 (`/nedetid/{plantId}`) | ✅ Live |
| Vakt-ROI v1 (`/vakt-roi/{plantId}`) | ✅ Live |
| API-endepunkter med JSON + CSV-eksport | ✅ Live |
| Norske helligdager + vakt-tids-modell | ✅ Live |
| 45 nye tester (45/45 passerer) | ✅ |

### Vakt-tids-modell (avklart med drifts-leder)

- **Vakt-vindu:** 15:00-07:00 hverdager + hele helg/helligdag
- **Responstid med vakt:** 1.0 t
- **Counterfactual uten vakt:** neste arbeidsdag 08:00 lokal tid
- **Reddbare kategorier:** Trip/feil + ekstern forstyrrelse + ukjent nedetid
- **Default kapasitetsfaktor:** 0.5 (konfigurerbar i UI)
- **Norske helligdager:** Anonymous Gregorian Easter + faste/bevegelige (skjær, lang, påske, Kr.himmelf., pinse, jul, 1. mai, 17. mai)

### Database-tilstand pr. 2026-04-29 morgen

| Tabell | Innhold |
|---|---|
| `core.settlement_imports` | 1 import: Drivdal feb-2025 (gammel test-data) |
| `core.classified_events` | 64 operlog-events fra feb-2026 |
| `core.sample_facts` | 14 278 samples fra feb-2026 |

**Inkonsistens:** Settlement er feb-2025, SCADA + operlog er feb-2026. Dette er fordi settlement-eksport for feb-2026 ikke er tilgjengelig ennå (eksport-problem hos drifts-leder). For testing brukes feb-2025-perioden.

### Verifiserte tall (Drivdal feb-2025)

API-svar fra `/api/v1/plants/drivdal/nedetid?from=2025-02-01&to=2025-03-01`:
- 8 events, 32 t total nedetid, 13.9 MWh tap, 22 932 NOK tap

API-svar fra `/api/v1/plants/drivdal/vakt-roi?...`:
- 8 events, 5 reddbare innenfor vakt
- 99 MWh reddet (med faktor 0.5), 163 326 NOK reddet (snitt-spot 1 650 NOK/MWh)
- **NB:** Dette tallet er overestimert — overløps-justering ikke implementert ennå

### UI-forbedringer

- `FilterState`-singleton som husker valgt anlegg + periode på tvers av `/nedetid` og `/vakt-roi`
- Cache av siste API-respons → instant navigasjon mellom sider når filter er uendret
- Endring av filter invaliderer cache → må trykke Hent på nytt
- Norsk tallformat hardkodet (Blazor WASM støtter ikke `nb-NO`-culture)

## Bugs fikset

1. `MUD0002`: `AlignItems` finnes ikke på `MudGrid` i MudBlazor 8.x → fjernet
2. `CA1512`: Bytte til `ArgumentOutOfRangeException.ThrowIfNegativeOrZero` osv.
3. `CS1570`: XML-kommentar i test-fil hadde `<` som måtte escapes til `&lt;`
4. WASM-runtime-feil: `CultureInfo.GetCultureInfo("nb-NO")` kaster i InvariantGlobalization-modus

## Status pr. 2026-04-29

| Punkt | Status |
|---|---|
| Phase A — klassifikator + KPI | ✅ Live |
| Phase B — annoteringer | ✅ Live |
| SCADA foundation (tabeller, modul, importer) | ✅ Live |
| **Nedetids-analyse v1** | ✅ Live |
| **Vakt-ROI v1** | ✅ Live (uten overløps-justering) |
| Vakt-ROI overløps-justering | ❌ Spec klar, ikke implementert |
| Drivdal effektivitets-side | ❌ Ikke startet |
| ScadaClassifier (9-state) | ❌ Ikke startet |
| FusionClassifier | ❌ Ikke startet |
| Event-baserte KPI-er (MTBF/MTTR/FOR/EAF) | ❌ Ikke startet |

## Neste sesjon — anbefalinger

### Steg 1 — Vakt-ROI overløps-justering

**Bruk Claude Code.** Dette er ren implementasjons-arbeid med tydelig spec, build-test-iterasjon, og definerte verifikasjons-tall. Cowork ville hatt mye frem-og-tilbake mellom kommandolinje og chat.

**Spec:** `docs/SPEC-VAKT-ROI-OVERLOP.md` (klar til bruk)

**Logikk i én setning:** Vakt-ROI gjelder bare timer med overløp i magasinet — ellers er vannet trygt magasinert og kan kjøres senere.

**Drivdal har eksplisitt overløps-tag:** `DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV` (overløps-vannføring m³/s). Når > 0 → overløp.

**Estimat:** 1-2 t

**Slik starter du:**

```powershell
cd "C:\Morten\00 Oppetid"
git status              # se ucommittet arbeid fra denne sesjonen
git add .
git commit -m "feat(reporting): nedetid + vakt-roi v1"
claude
```

Når Claude Code starter, gi denne meldingen:

> Implementer `docs/SPEC-VAKT-ROI-OVERLOP.md`. Logikken: vakt-ROI gjelder kun for timer med overløp i counterfactual-perioden — ellers er vannet trygt magasinert. Kjør `dotnet build` og `dotnet test` underveis. Commit hvert hovedsteg. Verifiser til slutt med curl-kommandoen i specens "Verifisering"-seksjon. Forventet resultat: vesentlig lavere ROI enn dagens 163 326 NOK for Drivdal feb-2025 (sannsynligvis nær null pga. tørr vintermåned).

### Steg 2 — Drivdal effektivitets-side (η-kurve, sweet-spot)

**Bruk Cowork eller Claude Code — begge funker.** Lett implementasjons-jobb (~1 dag) men kan trenge designvalg underveis (hvilke KPI-er, fargevalg, hvordan vise sweet-spot). Cowork hvis du vil iterere på UI med skjermbilder. Claude Code hvis du har tydelig design i hodet.

### Steg 3 — ScadaClassifier (9-state) + FusionClassifier

**Bruk Cowork først, deretter Claude Code.**

Cowork-runden: avklar 9-state-reglene, hvordan SCADA, settlement og operlog skal kombineres, terskler for derating osv. (1-2 t diskusjon, gir en spec-fil i `docs/`).

Claude Code-runden: implementer specen (1-2 dager).

`ANALYSE-NEDETID-SCADA.md` har grunnlag for 9-state-reglene allerede — bruk den som utgangspunkt.

## Hvor jobbe når

| Oppgave-type | Bruk |
|---|---|
| Klar implementasjons-spec, mye build/test/iterasjon | **Claude Code** |
| Forretningslogikk, modellvalg, vannkraft-økonomi | **Cowork** |
| UI-feilsøking med skjermbilder | **Cowork** |
| Database-spørringer/diagnose | **Cowork** (har terminal-output i samtalen) |
| Spec-skriving før implementasjon | **Cowork** |
| Pure refaktorering, ny modul, ny API-endepunkt | **Claude Code** |

**Tommelfingerregel:** Hvis du allerede vet *hva* som skal gjøres, bruk Claude Code. Hvis du fortsatt diskuterer *hva* og *hvorfor*, bruk Cowork.

## Åpne spørsmål

1. **Settlement-eksport for feb-2026** — drifts-leder har eksport-problem i eSett-portalen. Når dette løses, last opp via `/upload` eller via curl mot `/api/v1/plants/drivdal/settlements`. Da blir alle datakilder synkronisert i tid og /nedetid kan vises for feb-2026.

2. **Den feilede "Tilstandsfordeling"-importen** — kan slettes:
   ```powershell
   docker compose exec postgres psql -U kraftverk -d kraftverk -c "DELETE FROM core.settlement_imports WHERE plant_name = 'Tilstandsfordeling';"
   ```

3. **Vannverdi-modell** — neste nivå utover overløps-justeringen. Tar hensyn til "lav spotpris nå vs høy spot senere"-scenario. Krevende, kommer i v2 eller senere.

4. **Annoteringskategorien `EksternForstyrrelse`** — brukes ikke i Drivdal-data ennå. Når operatøren begynner å markere "nett-fall"-events via annoterings-dialogen, vil disse plukkes opp som reddbare i Vakt-ROI.

## Filer endret denne sesjonen

Nye:
- `src/KraftverkUptime.Core/Time/NorgesHelligdager.cs`
- `src/KraftverkUptime.Core/Time/VaktTidsmodell.cs`
- `src/KraftverkUptime.Core/Domain/DowntimeEvent.cs`
- `src/KraftverkUptime.Core/Domain/VaktRoiResultat.cs`
- `src/KraftverkUptime.Modules.Reporting/Nedetid/INedetidQueryService.cs`
- `src/KraftverkUptime.Modules.Reporting/Nedetid/NedetidQueryService.cs`
- `src/KraftverkUptime.Modules.Reporting/Nedetid/DowntimeEventAggregator.cs`
- `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs`
- `src/KraftverkUptime.Api/Contracts/NedetidContracts.cs`
- `src/KraftverkUptime.Api/Endpoints/NedetidEndpoints.cs`
- `src/KraftverkUptime.Web/Services/NedetidApi.cs`
- `src/KraftverkUptime.Web/Services/FilterState.cs`
- `src/KraftverkUptime.Web/Pages/Nedetid.razor`
- `src/KraftverkUptime.Web/Pages/VaktRoi.razor`
- `tests/KraftverkUptime.Core.Tests/Time/NorgesHelligdagerTests.cs`
- `tests/KraftverkUptime.Core.Tests/Time/VaktTidsmodellTests.cs`
- `tests/KraftverkUptime.Infrastructure.Tests/Nedetid/DowntimeEventAggregatorTests.cs`
- `tests/KraftverkUptime.Infrastructure.Tests/Nedetid/VaktRoiCalculatorTests.cs`
- `docs/SPEC-VAKT-ROI-OVERLOP.md` *(spec til neste sesjon)*

Endret:
- `src/KraftverkUptime.Modules.Reporting/KraftverkUptime.Modules.Reporting.csproj` (refs til Settlement + Scada)
- `src/KraftverkUptime.Modules.Reporting/ReportingModule.cs` (DI for Nedetid-tjenester)
- `src/KraftverkUptime.Api/Program.cs` (`MapNedetidV1`)
- `src/KraftverkUptime.Web/Program.cs` (NedetidApi + FilterState)
- `src/KraftverkUptime.Web/Layout/NavMenu.razor` (Nedetid + Vakt-ROI links)

## Forslag til commit-grupper

Før du starter ny sesjon, commit denne sesjonens arbeid i logiske grupper:

```
1. feat(core): NorgesHelligdager + VaktTidsmodell + DowntimeEvent-domeneklasser
2. feat(reporting): INedetidQueryService + DowntimeEventAggregator + VaktRoiCalculator
3. feat(api): /nedetid og /vakt-roi-endepunkter med JSON + CSV
4. feat(web): /nedetid og /vakt-roi Razor-sider med MudBlazor + ApexCharts
5. feat(web): FilterState-singleton for delt periode/anlegg-state + cache
6. test: 32 nye tester for helligdager, vakt-tid, event-aggregering og ROI-modellen
7. docs: spec for vakt-roi overløps-justering + overlevering 2026-04-29
```

Eller alt i én:

```powershell
git add .
git commit -m "feat: nedetid + vakt-roi v1 (Prioritet 1) + spec for overløps-justering"
```

## Kommandoer for morgenen

```powershell
# Status
cd "C:\Morten\00 Oppetid"
git status
docker compose ps

# Sjekk at databasen er der
docker compose exec postgres psql -U kraftverk -d kraftverk -c "SELECT count(*) FROM core.sample_facts;"

# Hvis tjenestene er nede:
docker compose up -d

# Test API direkte
curl.exe "http://localhost:5080/api/v1/plants/drivdal/nedetid?from=2025-02-01T00:00:00Z&to=2025-03-01T00:00:00Z"

# Web-UI: http://localhost:5180/nedetid eller /vakt-roi
```

## Hvis Claude Code ikke er installert

```powershell
npm install -g @anthropic-ai/claude-code
claude
```

Gå gjennom autentisering første gang. Etter det kan du bare skrive `claude` i prosjektmappen for å starte en sesjon der.
