# Neste sesjon — Hydrogrid kross-anlegg-sammenligning

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-HYDROGRID-PORTEFOLJE-SAMMENLIGNING.md`
**Estimat:** 2-3 dager
**Avhengighet:** `SPEC-HYDROGRID-API.md` må være implementert (krever data i `core.hydrogrid_plan_hours` for flere anlegg)

## Mål

Bygg en kross-anlegg-sammenligning av Hydrogrids planer som finner anlegg som konsistent avviker fra portefølje-mønsteret. Slike avvik indikerer ofte bug i Hydrogrids modell for nettopp det anlegget (feil vannverdi-funksjon, fastlåste constraints, kaskade-feil osv.).

Drifts-leders observasjon: *"Hvis et anlegg ofte planlegges for høye produksjonsvolumer forskjellig fra de andre anleggene, indikerer det ofte at det er noe bug i beregningene til Hydrogrid."*

Per-anlegg-diagnostikken finner *time-spesifikke* feil. Denne modulen finner *systematiske* avvik som kun blir synlig på tvers.

## Hva bygges

| Del | Hva |
|---|---|
| Beregning | Plan-fraksjon (plan/capacity) + per-time MAD-basert z-score + outlier-rate per anlegg + korrelasjons-matrise |
| UI-side | `/hydrogrid/sammenligning` med heatmap, outlier-tabell, korrelasjons-matrise, top-outliers-detaljer |
| Anomali-job | Ukentlig (mandag 07:00) — flagger anlegg med outlier-rate > 15 % over 7 dager |
| Varsling | Banner på `/portefolje` + valgfri e-post/Slack |

Ingen ny database-tabell utover `hydrogrid_anomaly_reports` for varsel-historikk. Bygger på views på `hydrogrid_plan_hours`.

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Statistisk metode | MAD-basert Modified Z-score (robust mot små portefølje-størrelser). Parametrisk z-score vises også i UI som ekstra info. |
| Outlier-terskel | `\|mz\| > 2` for outlier-flagg, `\|mz\| > 3` for ekstrem outlier |
| Verdict-grenser | OK < 8 %, OBSERVASJON 8-15 %, ALERT > 15 % outlier-rate |
| Periode for anomali-job | Siste 7 dager, kjøres ukentlig |
| Min antall anlegg | 4 — ellers fall tilbake til parametrisk z, eller deaktiver med tydelig melding |
| Plant-fraksjon-normalisering | `plan_mwh / installed_capacity_mw` — gjør ulike anleggs-størrelser sammenlignbare |
| Korrelasjons-mål | Pearson over plan-fraksjon-tidsserie |

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | DB-views (`hydrogrid_latest_plans`, `hydrogrid_plan_fractions`, `hydrogrid_portfolio_stats`) | 1 t |
| 2 | `IPortfolioComparisonService` med 4 metoder + 12 tester | 6-8 t |
| 3 | API-endepunkter + DTO-er | 2 t |
| 4 | `PortfolioAnomalyJob` + `hydrogrid_anomaly_reports`-tabell + 4 tester | 3-4 t |
| 5 | UI-side med heatmap + 3 tabeller + korrelasjons-matrise | 4-6 t |
| 6 | Banner på portefølje-dashboard + acknowledge-flow | 2 t |
| 7 | End-to-end-test mot ekte data | 1 t |

## Spørre-policy

- **Færre enn 4 anlegg har Hydrogrid-data:** vis tydelig melding, ikke generér MAD-beregninger
- **Alle anlegg har samme plan (std = 0):** sett z = 0, ikke exception
- **Et anlegg mangler `installed_capacity_mw`:** ekskluder fra fraksjon-beregning, vis warning i UI
- **Outlier-rate > 50 % for et anlegg:** dette er sannsynlig ekstrem-tilfelle. Logg full data for review før alert sendes.

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Spesielt viktig:

1. Manuell sanity-check: med 11 anlegg og normaltfordelt plan, skal outlier-rate per anlegg være ≈ 5 % (ikke mye over eller under)
2. Etter steg 5: åpne `/hydrogrid/sammenligning` for siste uke, verifiser at heatmap viser 11 rader og at korrelasjons-matrise er symmetrisk
3. Etter steg 7: feed inn syntetisk data hvor ett anlegg planlegges aggressivt forskjellig → verifiser at jobb genererer ALERT

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-HYDROGRID-PORTEFOLJE.md` med:
- Kommit-tabell
- Faktisk outlier-rate per anlegg for siste 4 uker (gir oss baseline-data)
- Eventuelle anlegg som havnet i ALERT-status — kontakt Hydrogrid med data for review
- Test-status (forventet 16+ tester)

Foreslåtte oppfølginger:
- Tilsvarende sammenligning av **faktisk produksjon** (Elhub) på tvers — bygger på samme mal
- Sesong-justert outlier-deteksjon (våt vs tørr periode har naturlig forskjellig spredning)
- ML-basert anomali-deteksjon som lærer normal-fordelingen for hvert anlegg over tid
- Cross-portefølje-sammenligning (vår portefølje mot bransjesnitt) — krever ekstern dataleveranse
