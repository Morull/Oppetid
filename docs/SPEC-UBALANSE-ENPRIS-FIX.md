# SPEC: Ubalansepremie under énprismodellen + Vakt-ROI gate-closure-grense
**Dato:** 2026-06-10
**Målgruppe:** Claude Code
**Grunnlag:** FAGVURDERING-KPI-BEREGNINGER-2026-06-10.md (funn #1, høyest prioritet) og SPEC-VAKT-ROI-UBALANSE.md (foreldet premiss)
**Konsekvens i dag:** Vakt-ROI-ubalansekomponenten kan være **5–20× for høy** og påvirker faktiske vakt-beslutninger (driftsleders KPI).

## Bakgrunn (regelverk)

Norge gikk over til **énprismodell** i nov. 2021: én ubalansepris per avregningsperiode, samme uansett retning, én netto posisjon per BRP. Forventet ubalansekostnad er derfor `E[ubalansepris − spot]` over **alle** perioder — ofte nær null eller negativ i NO2. Det gamle regulerkraftmarkedet (toprismodell) finnes ikke lenger; BRP-en handler ikke aktivt, det er passivt oppgjør.

Dagens kode bygger fortsatt på toprislogikk.

## Funn som skal rettes

### A. `GetAvgImbalancePremiumAsync` snitter kun den positive halen
**Fil:** `NedetidQueryService.GetAvgImbalancePremiumAsync` (ca. l. 211–253; den verifiserte linja er `if (diff <= 0) continue;` ca. l. 245).

I dag: snitt av `(RkPris − Spot)` **kun for timer der diff > 0**. Det er toprislogikk og **overestimerer premien lett faktor 3–10** under énpris.

**Fiks:** beregn signert forventningsverdi over **alle** timer med gyldig (ubalansepris, spot):

```
premie = Σ_t (ubalansepris_t − spot_t) / N_t        // ALLE timer, ikke bare diff>0
```

- Fjern `if (diff <= 0) continue;` — negative bidrag skal telle.
- Behold konservativ fallback (0) kun når det ikke finnes timer med gyldig prisgrunnlag i det hele tatt.
- Navngi om internt fra «premium» til noe retnings-nøytralt (forventet ubalanse-merkost per MWh) — verdien kan nå være negativ.

**Datakvalitet (15-min):** etter 19.3.2025 har Norge ekte 15-min ubalansepris. På timesnivå er `RkPris` og `Spot` vektet med ulike vekter i HourlyAggregator, så premien er en proxy. Dokumentér det i koden; ikke prøv å «rette» det her (eget punkt #11 i fagvurderingen).

### B. Vakt-ROI multipliserer oppblåst premie med plan-MWh for hele counterfactual-vinduet
**Fil:** `VaktRoiCalculator` (ubalanse-komponenten, ca. l. 281–283 og 427–430).

I dag: `ReddetUbalanse_NOK` = premie × plan-MWh over **hele** counterfactual-vinduet (opptil ~60 t). Men reell ubalanse-eksponering stopper ved **neste day-ahead gate closure (12:00 D-1)** — uten vakt nullstilles budene for neste døgn, så det er ingen ubalanse å redde etter det.

**Fiks:**
- Begrens ubalanse-komponentens tidsvindu til `min(counterfactualEnd, nesteGateClosure)`, der nesteGateClosure = første 12:00 lokal tid som inntreffer etter event-start (budene for døgnet etter er da allerede levert; etter gate closure for det påfølgende døgnet finnes ingen forpliktelse).
- **Produksjons-komponenten** (`ReddetProduksjon_NOK`) er uendret — den gjelder hele counterfactual-vinduet (tapt produksjon reddes uansett gate closure).
- Kun ubalanse-grenen får gate-closure-cap.

## Filer

- `src/.../NedetidQueryService.cs` — `GetAvgImbalancePremiumAsync` (punkt A)
- `src/KraftverkUptime.Modules.Reporting/.../VaktRoiCalculator.cs` — ubalanse-komponent (punkt B)
- `docs/SPEC-VAKT-ROI-UBALANSE.md` — oppdater premisset (toprismodell → énpris); behold historikk-noten om hvorfor det var feil.
- Kontrakt `SnittUbalansetilleggNokMwh` i VaktRoiResponse beholder navn, men kan nå være negativ — sjekk at UI (`VaktRoi.razor`) tåler negativ verdi i visning/tooltip.

## Tester (Modules.Reporting/Infrastructure)

1. **Signert premie:** datasett med like mange timer over og under spot → premie ≈ 0 (ikke den positive halens snitt). Verifiser at en NO2-lignende serie med flere negative enn positive gir **negativ** premie.
2. **Gammel vs ny:** en serie der positiv-hale-snittet er kjent (f.eks. 3×) → ny signert verdi skal være vesentlig lavere.
3. **Gate-closure-cap:** event som starter 14:00, counterfactual slutter +50 t → ubalanse-komponenten teller kun timene fram til neste 12:00, produksjons-komponenten teller alle 50 t.
4. **Fallback:** ingen timer med gyldig prisgrunnlag → premie = 0, ingen exception.

## Akseptansekriterier

1. `GetAvgImbalancePremiumAsync` bruker alle gyldige timer (signert), ingen `diff <= 0`-filtrering.
2. Vakt-ROI ubalanse-komponent er begrenset til neste gate closure; produksjons-komponent uendret.
3. Eksisterende tester grønne; nye tester (1–4) grønne.
4. `dotnet build` uten nye warnings (warnings-as-errors).
5. Et representativt anlegg får en Vakt-ROI-ubalanse som er materielt lavere enn før (dokumentér før/etter-tall i overleverings-noten).

## Avgrensning

- **Ikke** rør times-CR, Timing-merverdi, KAIA-baserte `Ubalansekost_NOK` (de er korrekte per fagvurderingen).
- AF-redefineringen (#2) og EAF (#5) er separate specer.
- 15-min-pipeline (#11) er ute av scope; nøy deg med å dokumentere proxy-statusen på timesnivå.
