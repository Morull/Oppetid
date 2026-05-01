# Neste sesjon — Produksjons-modul fix

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-PRODUKSJON-FIX.md`
**Estimat:** 3-5 timer (én Claude Code-sesjon)
**Avhengighet:** Worktree `beautiful-elbakyan-dd576d` med produksjons-modulen

## Mål

Tre ting i denne rekkefølgen:

1. **Merge worktree til main** — modulen er live-deployed fra worktree, må følge samme arbeidsflyt som resten av kodebasen
2. **Forbedre UI-tooltips** og legge til komplementær KPI som hjelper drifts-leder forstå hva tallene betyr (forhindrer samme tolkningsfeil i fremtiden)
3. **Bygge unit-test-suite** for `ProduksjonAnalyseCalculator` — kritisk siden modulen er live uten testdekning

## Bakgrunn

Drifts-leder så Drivdal feb-2026 og spurte om beregningene var korrekte. Audit avdekket at:
- Tallene er matematisk riktige
- Men UI-tooltips er misvisende — Plan-treff-formelen forklares feil, og "Topp-prisperiode-utnyttelse" er ikke åpenbart at det måler MWh-volum-andel ikke time-andel
- Ingen tester eksisterer

Drivdal feb-2026 fasit (skal være uendret etter fix):
- Plan-treff: 65,8 %
- Topp-prisperiode-utnyttelse: 14,7 %
- Hydrogrid timing-merverdi: -21 697 NOK
- Faktisk timing-merverdi: -20 249 NOK

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | Merge worktree, fjern den, verifiser at filer er på main | 30 min |
| 2 | Utvid `ProduksjonAnalyseResult` med to nye felt | 30 min |
| 3 | Calculator: legg til `AndelTimerProdIToppKvartil` + `AndelTimerProdIBunnKvartil` | 30 min |
| 4 | UI: oppdater subtext og tolkning-metoder i `Produksjon.razor` | 1 t |
| 5 | Unit-tests: 12+ tester, fasit-test for Drivdal feb-2026 | 2 t |
| 6 | Verifiser at eksisterende tall er uendret (regresjons-curl) | 30 min |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Eksisterende formler | **Endres ikke** — bare ekstra felt legges til |
| Ny KPI | `AndelTimerProdIToppKvartil` og `AndelTimerProdIBunnKvartil` (komplementært til eksisterende volum-andel) |
| Subtext på "Topp-prisperiode-utnyttelse" | Skal inneholde **begge** tall: MWh-andel i top-25 % + time-andel i top-25 % |
| Subtext på "Plan-treff" | Korrigert formel: "1 − MAE/Σplan" |
| `AndelProdIBunnKvartil` (eksisterende men ubrukt) | Eksponer i månedstabell-kolonne |
| Tester | Hardkodet input-output, ingen DB-tilgang |
| Drivdal-fasit-test | Verifiser ±2 % avvik mot screenshottall |

## Spørre-policy

- Hvis worktree ikke kan merges rent: **stopp og rapporter** før noe kode endres
- Hvis Drivdal-tallene endrer seg etter step 3: **stopp og rapporter** — det betyr at calculator-logikken har blitt påvirket utilsiktet
- Tooltip-tekst-detaljer (eksakt formulering): bestem selv basert på spec-eksemplene

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritisk: etter fix skal `curl /api/v1/plants/drivdal/produksjon-analyse?from=2026-02-01&to=2026-03-01` returnere identiske eksisterende felt + to nye felt.

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-PRODUKSJON-FIX.md` med:
- Kommit-tabell
- Bekreftelse på at Drivdal feb-2026 har identiske KPI-tall før og etter fix
- Test-status (forventet 12+ nye tester)
- Verifisering at worktree er fjernet

Foreslåtte oppfølginger (lavere prioritet):
- Visualisering av topp-/bunn-vinduer som markerte områder i Plan-vs-faktisk-grafen
- Kobling mellom høy `AndelTimerProdIBunnKvartil` og overløpsrisiko (kaskade-spec)
- Plan-justert capture rate (kombinerer denne modulen med capture rate-spec)
