# SPEC — Manuell overstyring av nedetidshendelsers starttidspunkt

**Status:** Forslag · **Dato:** 2026-06-01
**Eier:** Drifts-leder (manuell korreksjon) · **Implementeres i:** Claude Code

## 1. Mål

Drifts-leder skal kunne korrigere **starttidspunktet** til en nedetidshendelse manuelt, når SCADA-klassifiseringen plasserer starten feil.

> Eksempel: Haukland 28.05 vises med start kl. 17:00, men hendelsen begynte egentlig kl. 10:00.

Når starttiden endres må **alle avledede tall følge med automatisk** — særlig **Vakt-ROI**: kl. 17:00 ligger i vaktvinduet (15–07), kl. 10:00 ligger i ordinær arbeidstid (08–15). Korrigeres starten til 10:00 faller hendelsen **utenfor vakt** → driftspersonell responderer, ikke vakta → **Vakt-ROI for hendelsen skal bli 0**. I tillegg blir nedetiden lengre (mer tap).

## 2. Dagens arkitektur (relevant)

- **Hendelser er avledet, ikke lagret.** `DowntimeEventAggregator.Aggregate(...)` er en ren funksjon som bygger `DowntimeEvent`-rader fra klassifiserte timer + operlog. Det finnes **ingen stabil event-ID** — en hendelse identifiseres naturlig av `(PlantId, StartUtc)`.
- **Vakt-ROI nøkles på `event.StartUtc`.** I `VaktRoiCalculator.Calculate(...)`:
  - `vaktModell.ErInnenforVakt(e.StartUtc)` avgjør innenfor/utenfor vakt.
  - `counterfactualEnd = NesteArbeidsdagOppstart(e.StartUtc)`.
  - Grupper nøkles på `(PlantId, counterfactualEnd)`.
  - `overrides`-oppslag bruker `leaderStart` (= `StartUtc`).
- **Override-tabellen finnes allerede.** `core.vakt_event_overrides` nøkles på `(PlantId, EventStartUtc)` og bærer allerede:
  - `Classification` (Auto / HaddeOverlop / IkkeOverlop)
  - **`ActualEndOverrideUtc`** — manuell *slutt*-tid (presedens! slutt kan allerede overstyres)
  - `GuardResponseOverride` (Auto / Yes / No)
  - `Comment`, `SetAt`, `SetBy`
- **UI:** `VaktEventDetailDialog.razor` er detalj-popup per hendelse. Endepunkt: `PUT /api/v1/plants/{plantId}/vakt-overrides`.

## 3. Kjerneutfordring og designvalg

Override-raden er nøklet på `EventStartUtc`. Lar vi brukeren endre starttiden, endres nøkkelen — og alle andre overstyringer (klassifisering, slutt-tid, vakt-utrykning) for samme hendelse **mister ankeret sitt** (orphaning).

**Løsning: nøkle på *detektert* (opprinnelig) start, ikke visningsstart.**

1. `DowntimeEvent` får et nytt, immutabelt felt **`DetectedStartUtc`** = starten slik SCADA-klassifiseringen beregnet den (før noen override). `StartUtc` blir den **effektive** starten (detektert, eller korrigert hvis override finnes).
2. Override-raden nøkles fortsatt på det samme tidsstempelet, men semantisk er det nå **`DetectedStartUtc`** (stabilt — SCADA endrer det ikke). Bakoverkompatibelt: i dag finnes ingen start-override, så `EventStartUtc == DetectedStartUtc`.
3. Ny kolonne **`ActualStartOverrideUtc`** (nullable) på `vakt_event_overrides`, parallelt med `ActualEndOverrideUtc`.
4. Alle override-oppslag (i kalkulator og aggregator) nøkles på `DetectedStartUtc`. All tids-matematikk (innenfor vakt, counterfactual, tap) bruker `StartUtc` (effektiv).

Da overlever klassifisering/slutt/vakt-override en start-korreksjon, og Vakt-ROI rekalkuleres automatisk fordi kalkulatoren leser `StartUtc`.

## 4. Datamodell

Utvid `VaktEventOverrideEntry` + `core.vakt_event_overrides`:

```
ActualStartOverrideUtc  timestamptz NULL   -- korrigert start, eller NULL = bruk detektert
```

Raden slettes (tilbake til Auto) først når **alle fire** er default:
`Classification = Auto` AND `ActualStartOverrideUtc IS NULL` AND `ActualEndOverrideUtc IS NULL` AND `GuardResponseOverride = Auto`.

Migrasjon: `ALTER TABLE core.vakt_event_overrides ADD COLUMN actual_start_override_utc timestamptz NULL;` (idempotent i `DatabaseBootstrapper`).

## 5. Aggregator-endring

I tjenesten som kaller `DowntimeEventAggregator` (NedetidQueryService / PortfolioVaktRoiQueryService):

1. Bygg events som nå → hver får `DetectedStartUtc = StartUtc`.
2. Hent override-rader for anlegget. For hver hendelse med `ActualStartOverrideUtc` satt:
   - Sett `StartUtc = ActualStartOverrideUtc` (behold `DetectedStartUtc`).
   - **Reberegn `VarighetTimer` og `TapNok`** over det nye vinduet `[StartUtc, EndUtc)` med samme per-time-logikk (`tap_mwh = ProduksjonplanMwh ?? SpotbudMwh ?? 0`, `tap_nok = tap_mwh × SpotprisNokMwh`). For timer som nå er innenfor vinduet men ikke var klassifisert som nedetid, hentes plan/spot på samme måte; mangler data flagges hendelsen `TapDataPartial`.
   - Samme håndtering for `ActualEndOverrideUtc` (eksisterer allerede — samkjør slik at start- og slutt-override virker sammen).
3. Send events videre til `VaktRoiCalculator` som vanlig. Override-ordbøkene (`overrides`, `excludeFromReddbar`) bygges nøklet på **`DetectedStartUtc`**, og kalkulatorens interne oppslag endres tilsvarende fra `e.StartUtc` → `e.DetectedStartUtc`.

> Ingen lagrede aggregater å oppdatere: alt regnes ved spørring, så en ny override slår automatisk gjennom på neste kall. Invalider eventuelle response-cacher på `(plantId)` ved upsert/delete.

## 6. Konsekvens for Vakt-ROI (det brukeren ber om)

Ingen egen ROI-kode trengs utover punkt 5 — fordi kalkulatoren allerede er en ren funksjon av `StartUtc`. Når aggregatoren leverer korrigert start:

- `ErInnenforVakt(10:00)` = false → hendelsen klassifiseres `UtenforVakt` → `ReddetNok = 0`, forklaring «Event startet i ordinær arbeidstid — driftspersonell responderer, ikke vakten.»
- Counterfactual-gruppering reberegnes på ny start.
- Porteføljens «Reddet av vakt»-sum og Økonomi-fanens KPI faller tilsvarende (de re-spør samme tjeneste).
- Nedetidstap øker (lengre varighet).

## 7. API

Utvid eksisterende endepunkt (ikke nytt):

```
PUT /api/v1/plants/{plantId}/vakt-overrides
body: {
  detectedStartUtc,            // nøkkel (het tidligere eventStartUtc — behold alias for bakoverkomp.)
  classification,
  comment?,
  actualStartOverrideUtc?,     // NYTT
  actualEndOverrideUtc?,
  guardResponseOverride?
}
```

- Behold `eventStartUtc` som alias for `detectedStartUtc` i request (bakoverkompatibilitet), men felt-navnet bør migreres.
- `GET` og `DELETE` uendret bortsett fra at de returnerer/nøkler på detektert start og tar med `actualStartOverrideUtc` i DTO.

## 8. UI (`VaktEventDetailDialog.razor`)

- Vis **Detektert start** (read-only) + **Korrigert start** (dato/tid-velger, forhåndsutfylt med effektiv start).
- Påkrevd **begrunnelse** ved start-/slutt-korreksjon (audit).
- Live-varsel når korreksjonen krysser vaktgrensen, f.eks.:
  > «Ny start 28.05 10:00 ligger i ordinær arbeidstid (08–15) → hendelsen faller utenfor vakt. Vakt-ROI settes til 0.»
- «Tilbakestill»-knapp = nullstill `actualStartOverrideUtc` (→ detektert start).

## 9. Validering

- `actualStartOverrideUtc < effektiv slutt` (korrigert slutt hvis satt, ellers detektert slutt) → ellers 400.
- Maks tillatt forskyvning konfigurerbart (f.eks. ± 30 dager fra detektert) for å fange tastefeil; default ± 14 dager.
- Start kan ikke settes før anleggets dataimport-periode begynner (ingen plan/spot å regne tap på) → tillatt, men flagg `TapDataPartial`.
- Begrunnelse påkrevd (ikke-tom) når start- eller slutt-override settes.

## 10. Audit

Gjenbruk `SetAt` / `SetBy` / `Comment`. Logg gammel → ny start i `Comment` eller egen audit-tabell. Vis «sist endret av X den …» i popup.

## 11. Edge-cases

| Tilfelle | Håndtering |
|---|---|
| Korrigert start = etter slutt | 400, avvis. |
| Start flyttes inn i timer uten nedetid-klassifisering | Vinduet utvides, tap reberegnes fra plan/spot for de timene. |
| Start flyttes ut av vaktvindu (eksemplet) | ROI = 0 automatisk (punkt 6). |
| Start flyttes *inn* i vaktvindu (motsatt) | ROI beregnes nå — counterfactual + overflow som vanlig. |
| Hendelsen var «leder» i en vakt-gruppe | Ny start kan endre gruppe-nøkkel/leder; reberegnes i pass 2–3. |
| Eksisterende slutt-/klassifisering-override | Beholdes (nøklet på detektert start). |
| Detektert start endrer seg ved re-import av SCADA | Override de-ankres (kjent begrensning) — se «Åpne spørsmål». |

## 12. Akseptkriterier

- [ ] Haukland-eksemplet: detektert start 28.05 17:00 (innenfor vakt, ROI > 0) → korriger til 10:00 → hendelsen blir `UtenforVakt`, `ReddetNok = 0`, varighet øker med 7 t, nedetidstap øker tilsvarende.
- [ ] Porteføljens «Reddet av vakt» og Økonomi-fanens KPI faller med nøyaktig det fjernede beløpet.
- [ ] Eksisterende klassifisering/slutt/vakt-override på samme hendelse beholdes etter start-korreksjon.
- [ ] «Tilbakestill» gjenoppretter detektert start og opprinnelig ROI.
- [ ] Validering avviser start ≥ slutt og tom begrunnelse.
- [ ] Audit viser hvem/når/begrunnelse.

## 13. Tester

- **Aggregator:** start-override forskyver `StartUtc`, beholder `DetectedStartUtc`, reberegner varighet+tap. (Pure, lett å unit-teste.)
- **VaktRoiCalculator:** event med start korrigert fra innenfor→utenfor vakt gir `ReddetNok = 0`; motsatt retning gir ROI. Override-oppslag treffer på `DetectedStartUtc`.
- **Endepunkt:** upsert med `actualStartOverrideUtc`, validering (start ≥ slutt → 400), sletting når alle fire er default.
- **Regresjon:** eksisterende slutt-/klassifisering-override overlever en start-override (ingen orphaning).

## 14. Åpne spørsmål

1. **Re-import av SCADA endrer detektert start** (f.eks. ny klassifisering) → override basert på gammel detektert start treffer ikke lenger. Bør vi nøkle på en mer robust fingerprint (PlantId + nærmeste klassifiserte nedetidstime + cause) i stedet for eksakt tidsstempel? Anbefaling: behold tidsstempel-nøkkel i v1, men ranger fingerprint som forbedring.
2. Skal start-korreksjon også kunne påvirke **tilgjengelighet/FOR** (KPI-ene fra UptimeReport), eller kun nedetid-/vakt-laget? v1-forslag: kun nedetid + vakt (UptimeReport-KPI er bundet til settlement-timer og reberegnes ikke per manuell hendelse).

## 15. Estimat

| Del | Innsats |
|---|---|
| Datamodell + migrasjon | 0,5 t |
| Aggregator (start-override + tap-reberegning) | 2–3 t |
| Kalkulator (nøkle override på DetectedStartUtc) | 1 t |
| API (felt + validering) | 1 t |
| UI (start-velger + vaktgrense-varsel + begrunnelse) | 2–3 t |
| Tester | 2 t |
| **Sum** | **~1 arbeidsdag** |
