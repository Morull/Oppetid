# Instruks til Claude Code — rydd opp i CR og merverdi-målene

Selvstendig oppgave. **Kjør denne som egen økt ETTER at
`NESTE-CHAT-FIKS-KAIA-OG-VAKTROI.md` er ferdig og committet** — den berører
delvis samme filer (Portefolje.razor, contracts), så ikke kjør parallelt.

---

## Bakgrunn — problemet

Tre uavhengige tall i appen er rotet sammen, og to av dem kan motsi hverandre
for samme anlegg og måned. Verifisert på Øgreyfoss april 2026:

- Capture rate-fanen viser CR = 0,93 og «Merverdi vs spot» = −669 000 NOK.
- Produksjon-fanen viser «Faktisk merverdi» = +166 000 NOK.

Samme anlegg, samme måned, motsatt fortegn. Årsaken:

1. **CR blander to ting.** `CaptureRateCalculator` regner capture-prisen som
   `Σ(SpotomsetningNok) / Σ(Elhub)` — faktisk omsetning per levert MWh. Det
   måler både *timing* OG *markedsutførelse* (om dere faktisk fikk spot for alt
   dere leverte). I en høyvanns-måned leverer anlegget mer enn det fikk solgt
   på dagen-før-markedet, så omsetning/MWh faller under spot — og CR faller
   under 1 selv om timingen var god.
2. **«Merverdi» betyr tre ulike ting.** CR-fanens «Merverdi vs spot», Produksjon-
   fanens «Hydrogrid-merverdi» og «Faktisk merverdi» heter alle «merverdi» men
   måler ulike størrelser.
3. **Forholdstall og krone-tall deler ikke teller.** CR (ratio) og «Faktisk
   merverdi» (kroner) burde være to former av samme konsept, men bruker ulik
   teller — derfor kan de få motsatt fortegn.

## Prinsippet for opprydningen

Ett mål = ett spørsmål. Og forholdstallet + krone-tallet for samme konsept
skal dele teller, slik at de aldri kan motsi hverandre.

## Målbildet — fire klart adskilte mål

| Mål | Spørsmål | Formel |
|---|---|---|
| **Capture rate (CR)** | Timet vi produksjonen mot pris? (forholdstall) | `Σ(Elhub×spot) / Σ(Elhub)` ÷ `Σ(spot)/N_timer` |
| **Timing-merverdi** | Hva ga timingen i kroner? | `Σ(Elhub×spot) − Σ(Elhub) × tidsvektet_snittspot` |
| **Realisert vs spot** | Fikk vi faktisk betalt spotverdien av det vi leverte? | `Σ(SpotomsetningNok − Elhub×spot)` |
| **Plan-merverdi** | Hvor godt timet Hydrogrid *planen*? | uendret (`Hydrogrid-merverdi`) |

CR og Timing-merverdi henger nå sammen: `Timing-merverdi = Σ(Elhub) ×
snittspot × (CR − 1)`. CR over 1 ⟺ Timing-merverdi positiv — *alltid*.

---

## Endringene

### Del 1 — `CaptureRateCalculator.cs` (`Modules.Reporting/CaptureRate/`)

I dag: `capturePrice = sumNok / sumMwh` der `sumNok += SpotomsetningNok`.
`MerverdiNok = Σ(SpotomsetningNok − SpotprisNokMwh × MwhElhub)`.

Endre til:

- **Capture-pris blir volumvektet spotpris:** akkumuler `sumElhubSpotValue +=
  MwhElhub × SpotprisNokMwh`, og `capturePrice = sumElhubSpotValue / sumMwh`.
- `timesBaseline` (tidsvektet snittspot) — **uendret**.
- `timesCr = capturePrice / timesBaseline` — formelen står, men capturePrice
  er nå det nye tallet.
- **Nytt felt `TimingMerverdiNok`** = `sumElhubSpotValue − sumMwh × timesBaseline`.
- **Behold dagens `MerverdiNok`-beregning** (`Σ(turnover − Elhub×spot)`) men
  **gi den nytt navn `RealisertVsSpotNok`** — dette er utførelses-gapet.
- **Nytt felt `RealisertPrisNokMwh`** = `Σ(SpotomsetningNok) / Σ(Elhub)` — hva
  dere faktisk fikk betalt per MWh (brukes av «Realisert vs spot»-visningen).
- **Dag-CR må bruke samme grunnlag.** I dag bruker `ComputeDagCr` /
  `AggregateDaily` / `DailyInput.NokDay` omsetning (`oppnaadd = NokDay/MwhDay`).
  Endre slik at dag-«oppnådd» blir `Σ(Elhub×spot)_dag / Σ(Elhub)_dag`. Det
  krever et nytt felt på `DailyInput` (f.eks. `ElhubSpotValueDay`) og at
  tjenesten som bygger `historicalDailyForPercentile` fylles ut tilsvarende.
  Times-CR og Dag-CR skal hvile på samme teller.

Oppdater `CaptureRateResult`-recorden og klasse-dokumentasjonen.

### Del 2 — følg endringen oppover

`CaptureRateResult` → `CaptureRateResultDto` → API-kontrakt →
`CaptureRateQueryService` → Web. Spor alle konsumenter; minst:
`CaptureRate.razor`, `Anlegg.razor`, `Effektivitet.razor`, `Portefolje.razor`.

### Del 3 — UI-navn og -visning

- **`CaptureRate.razor`:** KPI-kortene skal vise CR (rent timing),
  Capture-pris (= volumvektet spot), og **Timing-merverdi** som krone-tvillingen
  til CR. Vis **«Realisert vs spot»** som et eget, tydelig adskilt kort/seksjon
  (med `RealisertPrisNokMwh` + `RealisertVsSpotNok`) — ikke kalt «merverdi».
  Oppdater alle tooltips slik at de forklarer det nye, snevrere innholdet.
- **`Produksjon.razor`:** behold «Hydrogrid-merverdi», men relabel den tydelig
  som et **plan-kvalitetsmål** (f.eks. «Hydrogrid plan-merverdi» med undertekst
  «måler planens timing — ikke faktisk inntekt»). «Faktisk merverdi» er allerede
  riktig beregnet (`Σ(Elhub×spot) − Σ(Elhub)×snittSpot` i
  `ProduksjonAnalyseCalculator.cs` linje 271) — gi den samme navn som CR-fanens
  krone-tall, **«Timing-merverdi»**, så det er åpenbart at det er samme tall.
- **Reserver ordet «merverdi»** for timing-gevinsten. Plan-kvalitet og
  utførelses-gap får egne ord.

---

## Verifisering — Øgreyfoss april 2026

Etter endringen skal disse tallene stemme (hentet/avledet fra kjørende app
20.05.2026):

- **Times-CR ≈ 1,02** (var 0,93). CR flipper over 1,0 — i tråd med at timingen
  var positiv. Capture-pris ≈ 1 054 NOK/MWh, baseline ≈ 1 032 NOK/MWh.
- **Timing-merverdi ≈ +165 000 NOK** — skal matche Produksjon-fanens «Faktisk
  merverdi» (+166 000, liten avrundingsdiff er ok).
- **Realisert vs spot ≈ −669 000 NOK** — dagens «Merverdi vs spot»-tall, bare
  omdøpt.

Invariant som skal holde for alle anlegg/måneder, og som bør dekkes av en
unit-test: **CR > 1 hvis og bare hvis Timing-merverdi > 0.**

Oppdater eksisterende `CaptureRateCalculator`-tester til de nye formlene, og
legg til en test for invarianten over.

---

## Akseptkriterier

- [ ] `dotnet build` grønt; alle CR-tester passerer.
- [ ] CR bruker `Σ(Elhub×spot)/Σ(Elhub)` som teller — rent timing-mål.
- [ ] Times-CR og Dag-CR hviler på samme teller.
- [ ] `TimingMerverdiNok` finnes og har alltid samme fortegn som `CR − 1`.
- [ ] Utførelses-gapet finnes som eget felt/visning kalt «Realisert vs spot»
      — ikke «merverdi».
- [ ] Øgreyfoss april 2026: CR ≈ 1,02, Timing-merverdi ≈ +165k,
      Realisert vs spot ≈ −669k.
- [ ] Ingen UI-element kaller tre ulike størrelser «merverdi» lenger.
- [ ] Hydrogrid-merverdi er tydelig merket som plan-kvalitetsmål.

## Merknader

- Dette **endrer CR-tallet historisk** — alle måneder får ny CR-verdi. Det er
  en bevisst engangskostnad; nevn det i commit-meldingen.
- Bekreft hva `SpotomsetningNok`-kolonnen i KAIA-avregningsfila faktisk
  inneholder (bud × spot, eller noe annet) før du ferdig-navngir «Realisert vs
  spot» — er noe uklart, **stopp og spør Morten**.
- Vurder en kort `docs/SPEC-CR-MERVERDI.md` som dokumenterer de fire målene.
