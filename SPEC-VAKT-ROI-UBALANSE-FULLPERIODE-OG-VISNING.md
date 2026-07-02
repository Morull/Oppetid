# SPEC: Vakt-ROI — ubalanse over hele nedetidsperioden + vis faktiske tall

**Dato:** 2026-06-17
**Status:** Klar for implementering (Claude Code)
**Avløser/utvider:** SPEC-UBALANSE-ENPRIS-FIX (énprismodellen beholdes uendret)
**Berører IKKE:** selve premie-beregningen `GetAvgImbalancePremiumAsync` — den er korrekt.

---

## 1. Bakgrunn

To uavhengige funn i dagens Vakt-ROI:

1. **Ubalanse-komponenten kappes ved neste day-ahead gate closure.**
   `VaktRoiCalculator.Calculate` summerer ubalanse-MWh kun fram til
   `UbalanseEksponeringSlutt(leaderStart)` (slutten av siste budte døgn), mens
   produksjons-komponenten dekker hele counterfactual-vinduet. Antakelsen var at
   «uten vakt nullstiller operatøren neste døgns bud».

2. **Negative/null ubalanse-beløp skjules som «–» i UI.**
   Tabellene i `VaktRoi.razor` viser `ReddetUbalanse_NOK`, `ReddetProduksjon_NOK`
   og `ReddetNok` kun når verdien er `> 0`. I NO2 er énpris-premien `avg(rk − spot)`
   ofte negativ (verifisert Øgreyfoss jan 2026: −27,2 NOK/MWh, feb 2026:
   −23,6 NOK/MWh), så hele ubalanse-komponenten forsvinner visuelt og ser ut som
   datamangel.

**Domenekorreksjon (driftsleder):** Hydrogrid melder inn produksjon **automatisk**
for påfølgende døgn. Antakelsen om at bud nullstilles uten vakt holder derfor
ikke — anlegget står fortsatt forpliktet til å levere innmeldt produksjon i
**hele** den ekstra nedetiden vakten avverget. Ubalanse-kostnaden skal dermed
påløpe over hele counterfactual-vinduet, ikke bare til neste gate closure.

**Mål:**
- A. Ubalanse-komponenten skal dekke **hele** counterfactual-vinduet (samme timer
  som produksjons-/spart-time-beregningen).
- B. UI skal vise det **faktiske** ubalanse-/total-tallet — også når det er
  negativt eller null — slik at driftsleder ser om vakten faktisk tjener eller
  taper penger på ubalanse i nedetidstimene.

---

## 1b. Funn: ubalansepremien er reelt negativ — datavalidering 2026-06-17

Validert mot importfilene (`CSV Eksporter/done/2026-06/_multi__settlement_*.xlsx`,
fane «7 Øgreyfoss»):

- **Kolonnen er ekte:** `RK-pris × |Ubalanse|` avstemmer eksakt mot `RK-salg`/`RK-kjøp`
  per rad → «RK-pris» er den faktiske énpris-regulerkraftprisen, ikke en avledet/feil
  kolonne. Ingen parserfeil.
- **Vedvarende negativ, ikke vinterfenomen:** snitt(rk − spot) for Øgreyfoss er
  negativ i 13 av 15 måneder (apr 2025–jun 2026), 15-mnd snitt ≈ **−36 NOK/MWh**.
  Kun mar 2026 (+40) og mai 2026 (+30) positive.
- **Forklaring (ikke bug):** énpris-systemet + NO2 som overskuddssone gir
  regulerkraftpris systematisk under spot (hyppig ned-regulering). Et periodesnitt
  av (rk − spot) er derfor strukturelt negativt her.
- **Stor varians:** stdev ≈ 96, spenn −800…+860 NOK/MWh. Et periodesnitt er en
  skjør størrelse og maskerer at enkelt-hendelser i knapphetstimer (rk ≫ spot) kan
  ha sterkt positiv ubalanse-eksponering.

**Konsekvens for modellen:** se **Endring A, Variant 2** (per-time-verdsetting) som
anbefalt tilnærming. Periodesnittet (dagens) bør erstattes, ikke bare utvides.

---

## 2. Endring A — ubalanse over hele counterfactual-vinduet

**Fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs`

> **Velg variant.** Variant 1 fjerner kun gate-closure-cappen og beholder
> periodesnittet (minst arbeid). Variant 2 er anbefalt etter funn 1b: verdsett
> ubalanse per time på faktisk pris. Begge fjerner cappen.

### Variant 1 (minimal) — fjern cap, behold periodesnitt

#### A1. Fjern gate-closure-cappen i hovedløkka

I `Calculate`, i løkka som går `for (var h = leaderStartHour; h < counterfactualHour; ...)`:

**Før (ca. linje 327–332):**
```csharp
// Ubalanse-komponenten gjelder counterfactual-timer FRAM TIL neste
// gate closure (Spotbud-forpliktelsen står kun for alt budte døgn).
if (h < ubalanseCapHour)
{
    ubalanseMwh += planForHour;
}
```

**Etter:**
```csharp
// Ubalanse-komponenten gjelder HELE counterfactual-vinduet. Hydrogrid melder
// inn produksjon automatisk for påfølgende døgn, så forpliktelsen til å levere
// innmeldt produksjon består i hele den ekstra nedetiden vakten avverget —
// det finnes ingen manuell «nullstilling av bud» ved gate closure.
// (Endring 2026-06-17, avløser gate-closure-cappen.)
ubalanseMwh += planForHour;
```

Timene er allerede begrenset til ikke-outage-timer via `if (outageHourSet.Contains(h)) continue;` lenger opp i samme løkke — det skal **beholdes**. Ubalanse krediteres altså for de samme «spart»-timene som ellers, bare nå over hele vinduet.

### A2. Fjern død kode

- Slett linjen som beregner cappen (ca. linje 299):
  ```csharp
  var ubalanseCapHour = FloorToHour(UbalanseEksponeringSlutt(leaderStart));
  ```
- Slett hele privatmetoden `UbalanseEksponeringSlutt(...)` (ca. linje 612–630) inkl.
  XML-doc. Den har ingen andre kallsteder.
- Hvis `TimeZones.Norway` / `TimeZoneInfo`-bruk kun stammet herfra: rydd ubrukte
  `using`/referanser (kompilator vil flagge).

### A3. Oppdater dokumentasjon i samme fil

- Klasse-XML-doc og parameter-doc for `snittUbalansetillegg_NokMwh` (ca. linje 99–107
  og 292–298): fjern formuleringer om at ubalanse «summeres kun fram til neste
  day-ahead gate closure». Erstatt med at ubalanse gjelder hele counterfactual-
  vinduet pga. Hydrogrids automatiske innmelding.

#### A4. Oppdater forklaringsteksten

I `BuildForklaring` (ca. linje 636–721): fjern «fram til neste gate closure» fra
ubalanse-setningene (ca. linje 718–719). Se også Endring B3 for fortegnsnøytral tekst.

### Variant 2 (ANBEFALT) — verdsett ubalanse per time på faktisk pris

**Motivasjon (funn 1b):** ett periodesnitt er strukturelt negativt i NO2 og maskerer
at en trip i en knapphetstime (rk ≫ spot) gir stor positiv ubalanse-redning. Verdsett
i stedet hver counterfactual-time på *den timens* faktiske (rk − spot).

**Endringer:**

1. **Ny inn-parameter** til `Calculate`, analogt med `planByHour`:
   ```csharp
   IReadOnlyDictionary<DateTimeOffset, double>? ubalansePremieByHour = null
   ```
   = `RkPrisNokMwh − SpotprisNokMwh` per UTC-time (signert). `null` ⇒ fall tilbake
   til skalaren `snittUbalansetillegg_NokMwh` (bakoverkompatibelt).

2. **I hovedløkka** (ikke-outage-timer, hele vinduet): akkumuler NOK direkte,
   ikke MWh:
   ```csharp
   var premie = ubalansePremieByHour is not null
       ? (ubalansePremieByHour.TryGetValue(h, out var ph) ? ph : snittUbalansetillegg_NokMwh)
       : snittUbalansetillegg_NokMwh;
   ubalanseMwh += planForHour;                 // behold for forklaring/visning
   ubalanseNokAkk += planForHour * premie;     // ny: faktisk NOK per time
   ```
   `ReddetUbalanse_NOK` settes da fra `ubalanseNokAkk` i stedet for
   `ubalanseMwh × skalarpremie`.

3. **Datakilde:** `NedetidQueryService` har allerede `SpotprisNokMwh` og
   `RkPrisNokMwh` per klassifisert time (samme kilde som `GetAvgImbalancePremiumAsync`).
   Lag en `GetImbalancePremiumByHourAsync(...)` som returnerer dictionaryen, og send
   den inn fra `NedetidEndpoints` (linje ~240) og `PortfolioVaktRoiQueryService`
   (linje ~226). Behold `GetAvgImbalancePremiumAsync` som fallback/visning av snitt.

4. **Forklaring:** vis både snittpremien for hendelsens timer og total NOK, slik at
   driftsleder ser om *denne* hendelsen traff dyre eller billige ubalansetimer.

---

## 3. Endring B — vis faktiske tall i stedet for «–»

**Prinsipp:** «–» skal kun bety **«ikke en ROI-kandidat»** (utenfor vakt eller
ikke reddbar). For kandidat-hendelser (`ErInnenforVakt && ErReddbar`) skal NOK-
kolonnene alltid vise det faktiske tallet — inkludert 0 og negativt. Dette er
samme filosofi som allerede er dokumentert i koden («0 NOK reddet er en MENINGSFULL
verdi», `VaktRoi.razor` ca. linje 1326–1327) og som detalj-dialogen allerede følger
(`VaktEventDetailDialog.razor` viser `@FormatNok(RoiEvent.ReddetUbalanse_NOK)` direkte).

### B1. `src/KraftverkUptime.Web/Pages/VaktRoi.razor`

Erstatt mønsteret `verdi > 0 ? FormatNok(verdi) : "–"` med en hjelpefunksjon som
skiller kandidat fra ikke-kandidat. Legg til i `@code`-blokken:

```csharp
// Viser faktisk NOK-verdi (inkl. 0 og negativ) for ROI-kandidater.
// «–» reserveres for hendelser som ikke er kandidat (utenfor vakt / ikke reddbar).
private static string DisplayNok(VaktRoiEventDto e, double verdi)
    => (e.ErInnenforVakt && e.ErReddbar) ? FormatNok(verdi) : "–";
```

Oppdater de tre tabellblokkene (per-plant + portefølje):

| Sted (ca. linje) | Før | Etter |
|---|---|---|
| 308 | `@(context.ReddetProduksjon_NOK > 0 ? FormatNok(context.ReddetProduksjon_NOK) : "–")` | `@DisplayNok(context, context.ReddetProduksjon_NOK)` |
| 309 | `@(context.ReddetUbalanse_NOK > 0 ? ... : "–")` | `@DisplayNok(context, context.ReddetUbalanse_NOK)` |
| 361 | `ReddetProduksjon_NOK > 0 ? ...` | `@DisplayNok(context, context.ReddetProduksjon_NOK)` |
| 362 | `ReddetUbalanse_NOK > 0 ? ...` | `@DisplayNok(context, context.ReddetUbalanse_NOK)` |
| 590 | Overløp NOK | `@DisplayNok(context, context.ReddetProduksjon_NOK)` |
| 591 | Ubalanse NOK | `@DisplayNok(context, context.ReddetUbalanse_NOK)` |
| 592 | Reddet (total) | `@DisplayNok(context, context.ReddetNok)` |

`FormatNok` bruker allerede `"N0"` og formaterer negative tall korrekt («-1 234»).
Ingen endring nødvendig der.

**NB — `ReddetProduksjon_NOK`** er alltid ≥ 0 (MWh × spot, begge ikke-negative),
så den vil nå vise «0» for kandidater uten overløp i stedet for «–». Det er
ønsket (skiller «kandidat, 0 produksjon reddet» fra «ikke kandidat»).

### B2. Negativ total — visuell markering (valgfri, anbefalt)

`ReddetNok` (total) kan nå bli negativ når ubalanse er negativ og produksjon = 0.
Vurder å fargelegge negative totaler (f.eks. `Color.Error`/rød) i totalkolonnen og
i KPI-kortet «Total reddet», så driftsleder ser umiddelbart når vakten netto
**taper** på ubalanse i en periode. Ren visning, ingen logikkendring.

### B3. `VaktRoiCalculator.BuildForklaring` — fortegnsnøytral ubalanse-tekst

**Fil:** `VaktRoiCalculator.cs`

- Senk/utvid `hasUbalanse`-terskelen (ca. linje 655) slik at også negativ ubalanse
  vises i forklaringen. Forslag:
  ```csharp
  var hasUbalanse = Math.Abs(snittUbalansetillegg) > 0.01;
  ```
  (dropp `&& Math.Abs(reddetUbalanseNok) >= 0.5` slik at små/negative beløp ikke
  faller ut.)
- Formuler ubalanse-setningene fortegnsnøytralt. Eksempel når beløpet er negativt:
  > «Ubalanse: ≈ {ubalanseMwh:F1} MWh × {premie:F0} NOK/MWh = {beløp:F0} NOK
  > (negativ → ubalanse var i snitt billigere enn spot; vakten reddet ikke
  > ubalanse-kostnad i denne perioden).»

  Når positivt: behold dagens «Ubalanse-gebyr unngått: … NOK».

---

## 4. Konsekvenser / som skal verifiseres

1. **Totaler bærer fortegn automatisk.** `NedetidEndpoints` (linje 384–407) og
   `PortfolioVaktRoiQueryService` (linje 86–124, 235–246) bruker rene `Sum(...)`.
   Negativ ubalanse trekker nå ned `TotalReddetNok` / `TotalReddetUbalanse_NOK`.
   Ingen kodeendring der, men **bekreft** at ingen mellomledd klipper til `Max(0, …)`.
2. **CSV-eksport** (`NedetidEndpoints` linje 524–526) skriver allerede rå
   `ReddetUbalanse_NOK`/`ReddetNok` — får negative verdier med. OK, men dokumentér
   i kolonneoverskrift/README at verdiene kan være negative.
3. **ROI-ratio / lønnsomhetsklassifisering** (`VaktRoi.razor`, terskler 0.5/1.0):
   sjekk at en negativ `ReddetNok` ikke gir misvisende ratio (f.eks. negativ ÷
   vaktkost). Vis evt. «netto tap» eksplisitt.
4. **Større ubalanse-volum.** Full-periode-endringen øker `ubalanseMwh` for
   hendelser som tidligere ble kappet ved gate closure — typisk hendelser som
   strekker seg over flere døgn (helg). Forventet effekt: større tallverdi (mer
   negativ i NO2 i dag, men positiv i perioder der RK > spot).

---

## 5. Akseptansekriterier

- [ ] For en reddbar hendelse innenfor vakt vises faktisk ubalanse-beløp i
      tabellen, også når det er 0 eller negativt (ikke «–»).
- [ ] «–» vises fortsatt for hendelser utenfor vakt eller ikke-reddbare.
- [ ] Ubalanse-MWh summeres over hele counterfactual-vinduet (leaderStart →
      counterfactualEnd), kun begrenset av faktiske outage-timer — ikke av gate
      closure.
- [ ] `UbalanseEksponeringSlutt` er fjernet og prosjektet kompilerer.
- [ ] Detalj-popup og tabell viser samme ubalanse-beløp for samme hendelse.
- [ ] Total-KPI og porteføljesum reflekterer negativ ubalanse (kan bli < 0).
- [ ] Forklaringsteksten beskriver negativ ubalanse korrekt (ikke skjult).

---

## 6. Testforslag

**Enhetstester — `VaktRoiCalculator`:**
1. *Full periode:* hendelse fredag kveld → counterfactual mandag 08:00, premie
   konstant. Verifiser at `ubalanseMwh` = sum av plan over alle ikke-outage-timer
   i hele vinduet (ikke kappet ved lørdag/søndag-gate-closure). Sammenlign mot
   eksplisitt forventet sum.
2. *Negativ premie:* `snittUbalansetillegg_NokMwh = -25`, overflow = 0.
   Forvent `ReddetProduksjon_NOK = 0`, `ReddetUbalanse_NOK < 0`,
   `ReddetNok = ReddetUbalanse_NOK`.
3. *Regresjon:* hendelse innenfor ett døgn (ingen cap-effekt før eller nå) gir
   uendret ubalanse-MWh mot tidligere — beskytter mot utilsiktet dobbelt-telling.

**UI/komponent:**
4. `DisplayNok`: kandidat med verdi 0 → «0»; kandidat med −1234 → «-1 234»;
   ikke-kandidat → «–».

**Fasit mot importdata (manuell/integrasjon):**
5. Kjør Øgreyfoss feb 2026 (`_multi__settlement_..._20260429131224.xlsx`,
   fane «7 Øgreyfoss»). Forventet premie ≈ −23,6 NOK/MWh; ubalanse-komponenten for
   en flerdøgns-hendelse skal nå være tydelig negativ og synlig i UI.

---

## 7. Filer som berøres

| Fil | Endring |
|---|---|
| `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs` | A1 fjern cap, A2 slett `UbalanseEksponeringSlutt`, A3 doc, B3 forklaringstekst + terskel |
| `src/KraftverkUptime.Web/Pages/VaktRoi.razor` | B1 `DisplayNok` + 7 kolonner, B2 valgfri fargelegging |
| `tests/...` (eksisterende Vakt-ROI-testprosjekt) | nye tester pkt. 6 |
| `NedetidEndpoints.cs` / `PortfolioVaktRoiQueryService.cs` | kun verifisering (pkt. 4), forventet ingen endring |

---

## 7b. Fortegnsvalidering — UTFØRT OG BEKREFTET 2026-06-17

Avstemt mot kjent havari Øgreyfoss 16.01.2026 16:00 (oppgjør i `_multi__settlement_
20260601T081559114_*.xlsx`, fane «7 Øgreyfoss»):

| tid | Elhub | Spotbud | Ubalanse | rk−spot |
|---|---|---|---|---|
| 15:00 (oppe) | 2,448 | 2,000 | −0,448 | +25 |
| 16:00 (trip) | 0,595 | 2,000 | **+1,405** | −98 |
| 17:00 (nede) | 0,148 | 2,000 | **+1,852** | +3 |
| 18:00 (oppe) | 3,626 | 2,000 | −1,626 | −24 |

**Bekreftet:**
1. `Ubalanse = Spotbud − Produksjon`. **Positiv Ubalanse = kort** (under-levering),
   og den blir positiv nøyaktig når anlegget faller ut → vakt-scenariet = kort posisjon.
2. **Hydrogrid holder budet gjennom havariet** (Spotbud = 2,0 i alle nedetidstimer).
   Bekrefter premisset for Endring A: ingen gate-closure-cap.
3. **Koden har riktig fortegn:** `(rk − spot) × kort-MWh`. Positiv når rk > spot
   (dyrt å være kort → vakt sparer), negativ når rk < spot (billig → vakt gir avkall
   på gevinst). **Ingen fortegnsbytte nødvendig.**

Konklusjon: ROI-komponentens fortegn er korrekt. Restpunktet er kun *vekting*
(Variant 1 periodesnitt vs. Variant 2 per-time) — ikke fortegn.

## 8. Åpne spørsmål / antakelser

0. **Variant 1 vs 2:** anbefaling er Variant 2 (per-time) etter funn 1b. Bekreft valg.
1. **Antakelse:** Hydrogrid melder inn produksjon automatisk for *alle* påfølgende
   døgn i counterfactual-vinduet, uten manuell inngripen. Hvis dette i praksis kun
   gjelder N døgn (f.eks. budhorisont), bør cappen erstattes av en
   konfigurerbar horisont i stedet for å fjernes helt. **Bekreft med driftsleder.**
2. Skal full-periode-ubalanse være default for **alle** anlegg/porteføljer, eller
   styres per anlegg via et `AutomatiskBudInnmelding`-flagg (anlegg uten Hydrogrid-
   auto-bud beholder gate-closure-cap)? Default i denne spec: gjelder alle.
3. Fortegn-konvensjon i KPI: skal «Total reddet» kunne vise negativt, eller skal
   produksjon og ubalanse splittes i to KPI-kort så et netto-tap på ubalanse ikke
   maskerer en positiv produksjons-redning? Anbefaling: behold splitt + vis netto.
