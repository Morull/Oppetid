# Spec: Vakt-ROI med ubalanse-komponent (v3)

**Status:** Klar til implementasjon (2026-04-29)
**Estimat:** 1-2 timer
**Forrige spec:** `SPEC-VAKT-ROI-OVERLOP.md` (overløps-justering, levert)

## Bakgrunn

v2 (overløps-justeringen) løste én feil i v1: at vi antok all uprodusert
energi var tapt. Men løsningen var for konservativ — den ignorerer at
producent har en **Spotbud-forpliktelse** uavhengig av magasinstanden:

> Selv om vannet er trygt magasinert, blir du ubalansert hvis du ikke leverte
> det du forpliktet deg til. Du betaler RK-pris (oppregulering) for det
> manglende volumet — vakten redder akkurat det gebyret.

Drifts-leder bekreftet 2026-04-29 (samtale): *"Ja, ubalanse-tap kan også
regnes med."*

## To uavhengige tap-komponenter

| Komponent | Når oppstår det | Avhenger av overløp? |
|---|---|---|
| **Produksjonstap** | Bare når magasinet er fullt og vannet renner forbi | Ja (v2's regel) |
| **Ubalanse-tap** | Når Spotbud > 0 i timen og levering = 0 | Nei |

### Eksempel

Drivdal byr inn 1 MWh i time 18 (Spotbud > 0). Trip kl 17:55, vakten fikser
kl 23. Magasinet har plass.

- **v2 sier:** ingen overløp → 0 NOK reddet
- **v3 sier:** ingen produksjons-ROI (vannet er ikke tapt), men **vakten
  reddet ubalanse-gebyret** for de 5 timene plant'en ville stått, ≈
  5 t × 1 MWh/t × ~250 NOK/MWh = 1 250 NOK

## Beslutning

**Vakt-ROI per event splittes i to komponenter, summeres til total ROI:**

```
reddet_produksjon_nok = overflow_timer × effekt × kapasitetsfaktor × snittSpot
reddet_ubalanse_nok   = ekstra_timer  × effekt × kapasitetsfaktor × snittUbalansetillegg
reddet_nok            = reddet_produksjon_nok + reddet_ubalanse_nok
```

Der:
- `overflow_timer` ≤ `ekstra_timer` (ubalanse gjelder for ALLE counterfactual-timer)
- `snittUbalansetillegg = max(0, snitt(RkPris − Spotpris))` over perioden

## Snitt-ubalansetillegg-beregning

For settlement-rader i [from, to):

```
positive_diffs = rader hvor RkPris > Spotpris
                 OG begge har verdi
                 OG (under-leveranse pekt på i UbalanseMwh)

snittUbalansetillegg = avg(RkPris − Spotpris) over positive_diffs
                       hvis count > 0, ellers 0
```

Forsiktig: vi tar bare med timer der RK var dyrere enn spot (typisk
oppregulering når systemet var kort). Tilfeller der RK < spot betyr
nedregulering — der ville producent IKKE tape ved ikke å levere
(over-leveranse-scenarioet er irrelevant for trip).

Hvis settlement-data mangler RK-priser eller alle RK-priser er ≤ Spot:
`snittUbalansetillegg = 0` → `reddet_ubalanse_nok = 0`. Konservativt.

## Endringer i kodebasen

### 1. Utvid `VaktRoiResultat`

**Fil:** `src/KraftverkUptime.Core/Domain/VaktRoiResultat.cs`

```csharp
public required double ReddetProduksjon_NOK { get; init; }   // overflow-betinget
public required double ReddetUbalanse_NOK { get; init; }     // alle counterfactual-timer
// ReddetNok-summen blir nå avledet:
//   ReddetNok = ReddetProduksjon_NOK + ReddetUbalanse_NOK
```

Eksisterende `ReddetMwh` beholdes som "produsert MWh som vakten reddet"
(altså overflow-betinget delen). Ubalanse-MWh blir et nytt felt eller
implisitt fra `ReddetUbalanse_NOK / snittUbalansetillegg`.

### 2. Utvid `VaktRoiCalculator.Calculate(...)`

Legg til parameter `snittUbalansetillegg_NokMwh` (default 0). Beregn
`reddet_ubalanse_nok` per event som ovenfor. Oppdater `Forklaring` til å
nevne begge komponenter når aktuelt:

> *"Vakt løste på 1.5 t. Counterfactual = 14.5 t. Av disse hadde 6 t overløp
> → 6 t produksjon reddet (2 200 NOK). Ubalanse-gebyr unngått for hele
> 14.5 t (3 600 NOK). Total: 5 800 NOK."*

### 3. Endepunkt-logikk

**Fil:** `src/KraftverkUptime.Api/Endpoints/NedetidEndpoints.cs`

I `GetVaktRoiAsync`:

```csharp
var snittUbalansetillegg = ComputeAvgImbalancePremium(events);
var roi = calculator.Calculate(
    events, plant.InstalledCapacityMw, snittSpot, faktor,
    overflowHours, overflowDataAvailable: dataset.DataAvailable,
    snittUbalansetillegg_NokMwh: snittUbalansetillegg);
```

`ComputeAvgImbalancePremium` kjøres på `events`-listen — eller helst på
periodens hourly-rader om vi henter dem direkte. Enkelste vei: snitt over
event-rader som har Spotpris og RkPris (sammenlign per hour i events,
men det krever utvidelse av DowntimeEvent).

**Beste tilnærming:** Hent rader fra `INedetidQueryService` eller direkte
fra ClassifiedHourlyRow-feltene som inkluderer pris-data, og beregn
gjennomsnittet på read-time.

### 4. DTO og UI

**Fil:** `src/KraftverkUptime.Api/Contracts/NedetidContracts.cs`

```csharp
public sealed record VaktRoiEventDto(
    ...,
    double ReddetProduksjon_NOK,
    double ReddetUbalanse_NOK,
    double ReddetNok,           // = sum av de to
    ...);
```

**Fil:** `src/KraftverkUptime.Web/Pages/VaktRoi.razor`

Legg til to nye kolonner ("Reddet produksjon", "Reddet ubalanse") eller hold
fortsatt sum-kolonnen og vis splitt i tooltip.

### 5. Tester

`VaktRoiCalculatorTests`:
- `Trip_Med_Overlop_Og_Ubalansetillegg_Beregner_Begge_Komponenter`
- `Trip_Uten_Overlop_Med_Ubalansetillegg_Gir_Bare_Ubalanse_ROI`
- `Trip_Uten_Ubalansetillegg_Faller_Tilbake_Til_Bare_Produksjons_ROI`

## Verifisering

For Drivdal feb-2025 forventer vi:
- `totalReddetProduksjon_NOK ≈ 0` (samme som v2 — overløp-data mangler)
- `totalReddetUbalanse_NOK ≈ 5-50 000 NOK` (avhenger av faktisk RK-spot-spread)
- `totalReddetNok` blir altså ikke lenger 0, men reflekterer ubalanse-besparelsen

## Out-of-scope

- Per-time imbalance estimate basert på faktisk Spotbud-volum (vi bruker
  installertEffekt × kapasitetsfaktor som proxy)
- Skille mellom oppregulering og nedregulering — vi tar konservativt bare
  med RK > spot
- Vannverdi-modell (utsatt produksjon kan være verdt mindre senere)
