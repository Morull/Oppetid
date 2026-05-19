# Endringer til KraftverkUptime — mai 2026

Fem uavhengige endringer. Implementer som separate commits i samme rekkefølge
som listet under. Etter hver endring: kjør `dotnet build` og relevante tester.

---

## 1. Erstatt `kapasitetsfaktor` med `ProduksjonplanMwh` i Vakt-ROI

### Bakgrunn
I dag estimeres reddet produksjon som
`overflowTimer × installertEffektMw × kapasitetsfaktor (0,5)`.
Det er en flat antagelse som ignorerer årstid, vannføring og markedssituasjon.

Settlement-fila inneholder allerede `ProduksjonplanMwh` per time
(Hydrogrid-plan). Den er allerede grunndata i modellen — leveres alltid med
KAIA-eksporten — og brukes i Produksjon-analysen via
`ProduksjonAnalyseQueryService`.

**Beslutning:** Bruk plan direkte i stedet for `installert × kapasitetsfaktor`.
Ingen fallback til kapasitetsfaktor — plan-kolonnen er alltid med.

### Ny formel
```
reddet MWh   = sum(ProduksjonplanMwh for hele klokketimer i counterfactual-vinduet
                   som har overløp OG ikke er dekket av outage)
ubalanse MWh = sum(ProduksjonplanMwh for hele klokketimer i counterfactual-vinduet
                   som ikke er dekket av outage)
```

For events der counterfactual-vinduet strekker seg forbi importert
settlement-data: bruk siste tilgjengelige plan-timer som proxy (nærmeste
samme ukedag/time bakover i tid, maks 4 ukers vindu). Flagg eventet med en ny
property `PlanDataPartial = true` slik at UI kan markere det.

### Filer som må endres

| Fil | Endring |
|---|---|
| `src/KraftverkUptime.Core/Domain/VaktRoiResultat.cs` | Fjern doc-referanser til kapasitetsfaktor. Legg til `PlanDataPartial`-property. |
| `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs` | Fjern `kapasitetsfaktor`-parameter. Legg til ny parameter `IReadOnlyDictionary<DateTimeOffset, double> planByHour`. Endre formlene i Pass 3 til å summere plan per time. Oppdater doc-string + `BuildForklaring`. |
| `src/KraftverkUptime.Modules.Reporting/Nedetid/NedetidQueryService.cs` | Legg til ny metode `GetProduksjonplanByHourAsync(plantId, fromUtc, toUtc, ct)` som returnerer dictionary fra `ProduksjonplanMwh` per `TimeUtc`. Trekker fra samme report-blobber som `ListEventsAsync`. |
| `src/KraftverkUptime.Modules.Reporting/Nedetid/INedetidQueryService.cs` | Deklarer den nye metoden. |
| `src/KraftverkUptime.Api/Endpoints/NedetidEndpoints.cs` | Fjern `kapasitetsfaktor`-query-param fra `GetVaktRoiAsync`. Kall den nye `GetProduksjonplanByHourAsync` og send inn i calculator. Fjern `Kapasitetsfaktor` fra `VaktRoiResponse`. |
| `src/KraftverkUptime.Api/Endpoints/PortfolioEndpoints.cs` | Fjern `kapasitetsfaktor`-query-param. |
| `src/KraftverkUptime.Modules.Reporting/Portefolje/IPortfolioVaktRoiQueryService.cs` | Fjern `kapasitetsfaktor`-parameter fra metode-signatur og fra DTO. |
| `src/KraftverkUptime.Infrastructure/Reporting/PortfolioVaktRoiQueryService.cs` | Fjern faktor-clamp og parameter-pass-through. Hent plan per anlegg. |
| `src/KraftverkUptime.Api/Contracts/NedetidContracts.cs` | Fjern `Kapasitetsfaktor` fra `VaktRoiResponse`. Legg til `PlanDataPartial` på `VaktRoiEventDto`. |
| `src/KraftverkUptime.Web/Services/NedetidApi.cs` | Fjern `kapasitetsfaktor`-parameter fra `GetVaktRoiAsync`, `GetPortfolioVaktRoiAsync`, og `BuildUrl`. Fjern feltet fra response-records. |
| `src/KraftverkUptime.Web/Pages/VaktRoi.razor` | Fjern `MudNumericField` for kapasitetsfaktor (rad 47-51). Fjern `_kapasitetsfaktor`-variabel og bruken i API-kall. Fjern visning av "Kapasitetsfaktor" i KPI-kortene. |
| `src/KraftverkUptime.Web/wwwroot/manual/index.html` | Oppdater Vakt-ROI-seksjon: forklar at produksjonsplan brukes som basis. |

### Tester som må oppdateres
- `tests/KraftverkUptime.Core.Tests` — VaktRoiCalculator-tester må refaktoreres til å gi plan-dictionary i stedet for kapasitetsfaktor.
- `tests/KraftverkUptime.Api.Tests` — endepunkt-tester må fjerne `kapasitetsfaktor`-query-param.
- Legg til en ny test som verifiserer at proxy-fallback for ukomplett plan-data fungerer (PlanDataPartial-flagget).

### CSV-eksport
Behold `reddet_mwh`-kolonnen og fjern eventuell kapasitetsfaktor-kolonne fra CSV.

---

## 2. Fjern Oversikt-siden

`src/KraftverkUptime.Web/Pages/Index.razor` brukes ikke i daglig flyt.

### Endringer
- Slett `Pages/Index.razor`.
- I `Pages/Reports.razor`: legg til `@page "/"` slik at roten lander på rapport-siden.
- I `Layout/NavMenu.razor`: fjern første `MudNavLink` (Oversikt-lenken med `Href=""`).
- Søk gjennom hele kodebasen etter `NavigateTo("")`, `NavigateTo("/")` og `Href=""` for å sikre at ingen forventer en Oversikt-side å returnere til. Det skal være null treff utenfor NavMenu/Index etter sletting.

### Verifisering
Bygg + start appen. Naviger til `http://localhost:5000/` — skal lande på Reports. Klikk rundt i sidemenyen og sjekk at ingen lenke gir 404.

---

## 3. Flytt "Anlegg" under "Produksjon" i sidemenyen

I `src/KraftverkUptime.Web/Layout/NavMenu.razor`: flytt linjen
`<MudNavLink Href="plants" ...>Anlegg</MudNavLink>` slik at den kommer etter
`Produksjon`-linja. Endelig rekkefølge:

```
Rapporter
Portefølje
Nedetid
Vakt-ROI
Capture rate
Produksjon
Anlegg          ← flyttet hit
Data-import
Kategorier
```

(Oversikt er borte etter endring 2.)

---

## 4. Konfigurerbar vakttid i Vakt-ROI

### Mål
Drifts-leder skal kunne stille på vakt-vinduet (start-tid og slutt-tid) for å
evaluere lønnsomheten av f.eks. å droppe nattvakt 22-07.

### Endringer

#### Backend
`VaktTidsmodellOptions` har allerede feltene `EttermiddagStart`, `MorgenCutoff`
og `OppmoteTidspunkt`. Bygg på dette:

- `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs`:
  Ta i mot `VaktTidsmodellOptions?` (nullable) som ny valgfri parameter
  i `Calculate(...)`. Hvis ikke null, bygg en lokal `VaktTidsmodell(options)`
  for denne ene spørringen, ellers bruk konstruktør-injected `_vaktModell`.

- `src/KraftverkUptime.Api/Endpoints/NedetidEndpoints.cs`:
  Legg til fire valgfrie query-params på `GET /api/v1/plants/{plantId}/vakt-roi`:
  ```
  ?vaktStartLokal=15:00&vaktSluttLokal=07:00&oppmoteLokal=08:00
  ```
  Parse til `TimeSpan` og bygg `VaktTidsmodellOptions`. Hvis ingen av de tre er
  satt, bruk default.

- Tilsvarende i `PortfolioEndpoints.cs`.

- `src/KraftverkUptime.Api/Contracts/NedetidContracts.cs`:
  Legg til feltene i `VaktRoiResponse` slik at UI kan vise hvilke verdier som ble brukt.

#### Frontend
`src/KraftverkUptime.Web/Pages/VaktRoi.razor`:

Bytt ut paper-en med kapasitetsfaktor (som forsvinner i endring 1) med en
ny seksjon "Vakt-vindu":

```
[Start vakt 15:00 ▼]  [Slutt vakt 07:00 ▼]  [Oppmøte neste arbeidsdag 08:00 ▼]

ℹ Default = hjem-vakt 15-07 hverdager + hele helg/helligdag. Endre for å
  simulere alternative vakt-ordninger (eks. droppe nattvakt 22-07).
```

Bruk `MudTimePicker` for de tre feltene. Helg-håndteringen
(hele lørdag/søndag/helligdag) endres ikke — det er fortsatt aktivt.

`src/KraftverkUptime.Web/Services/NedetidApi.cs`: legg til parametere i
`GetVaktRoiAsync` og `GetPortfolioVaktRoiAsync` og bygg URL deretter.

### Akseptkriterier
- Default-spørring (uten params) gir samme tall som i dag.
- Setter du `vaktStartLokal=22:00`, skal events i intervallet 15-22 mandag-fredag
  klassifiseres som `UtenforVakt` (driftspersonell), ikke `Reddbar`.
- UI skal huske valget i `localStorage`/`FilterState` for varigheten av sesjonen.

---

## 5. Rapporter åpner siste måneds rapport som default

### Bakgrunn
`src/KraftverkUptime.Web/Pages/Reports.razor` viser i dag en liste over
imports. Brukeren må klikke en rad for å gå til `ReportDetail`. Ønsket flyt:
samme som Nedetid og Vakt-ROI — siden bruker `FilterState.PlantId` + global
periode-velger og åpner detaljvisningen direkte.

### Atferd
- Når `Reports.razor` lastes inn:
  1. Hvis `FilterState.PlantId` er satt: kall `GET /api/v1/plants/{plantId}/settlements`
     og finn nyeste import som dekker forrige kalendermåned.
  2. Hvis funnet: redirect til `/reports/{plantId}/{importId}` (eller den
     URL-en `ReportDetail` bruker i dag).
  3. Hvis ingen import dekker forrige måned: behold dagens liste-visning som
     fallback med en banner "Ingen rapport for forrige måned — velg fra liste".
- Hvis `FilterState.PlantId` er tom: vis dagens info-melding
  "Velg et anlegg i topp-baren".

### Filer
- `src/KraftverkUptime.Web/Pages/Reports.razor`: legg til `OnInitializedAsync`-
  logikk som beskrevet over.
- `src/KraftverkUptime.Web/Pages/ReportDetail.razor`: ingen endring (er
  destinasjonen for redirect).
- Sjekk at `FilterState.PeriodChanged`-event også trigger ny lookup.

### Akseptkriterier
- Åpner du `/reports` med et anlegg valgt: lander rett på forrige måneds rapport.
- Periode-bytte i topp-baren oppdaterer visningen.
- Hvis ingen rapport: liste-visning vises som fallback.

---

## Test- og leveranse-rekkefølge

1. Endring 1 (plan i stedet for faktor) — mest invasiv, kjør tester først.
2. Endring 2 (fjern Oversikt) — enkel, lav risiko.
3. Endring 3 (sidebar-rekkefølge) — kosmetisk.
4. Endring 4 (konfigurerbar vakttid) — bygger på endring 1's UI-rydding.
5. Endring 5 (default-rapport) — uavhengig.

Hver commit skal være self-contained slik at vi kan revertere én enkelt.
Bruk samme commit-melding-stil som resten av repo-en (norsk, imperativ,
referer til denne fila i body).
