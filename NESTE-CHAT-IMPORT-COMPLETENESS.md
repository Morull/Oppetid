# Neste sesjon — Import-completeness-dashboard

**Dato opprettet:** 2026-05-03
**Spec:** `docs/SPEC-IMPORT-COMPLETENESS.md`
**Estimat:** 2-3 dager
**Bakgrunn:** Drifts-leder ber om oversikt over hvilke importer som mangler — med 11 anlegg × 4 datakilder × månedlig kadens er det lett å glemme.

## Mål

Bygg en `core.data_completeness`-modell + dashboard som:

1. **Sporer forventede datakilder** per anlegg (f.eks. settlement månedlig, SCADA månedlig, operlog månedlig, Hydrogrid-plan månedlig)
2. **Logger faktiske importer** i `data_imports`-tabell ved hver import
3. **Vises som matrise** på `/data-status` med anlegg × periode-celler, fargekodet på status (COMPLETE/PARTIAL/PENDING/OVERDUE)
4. **Sender ukentlig e-post-digest** til drifts-leder med oversikt over forfalt og delvis dekning
5. **Konfigurerbart per anlegg** — drifts-leder kan toggle hvilke kilder som er aktive (f.eks. SCADA blir aktiv først når en eksport-flow er på plass)

## Hovedkomponenter

| Komponent | Innhold |
|---|---|
| `core.data_source_expectations` | (plant_id, source_type, cadence, expected_lag_days, is_active) |
| `core.data_imports` | Per import: timestamp, fil, tag-count, dekning, bruker |
| `core.data_completeness_view` | Beregnet view som krysser forventninger × faktiske importer |
| `IDataCompletenessQueryService` | Backend-service for matrise + summary + overdue-liste |
| `/data-status`-side | Matrise-UI med fargekodede celler, drilldown og filter |
| `DataCompletenessWeeklyDigestJob` | Sender ukentlig e-post-rapport |
| Importør-utvidelser | Hver eksisterende import skriver til `data_imports` etter vellykket prosessering |

## Status-tilstander

| Status | Betydning | Farge |
|---|---|---|
| COMPLETE | Importert med ≥ 95 % dekning | grønn |
| PARTIAL | Importert med < 95 % dekning | gul |
| PENDING | Ikke importert ennå, innenfor lag-vindu | grå |
| OVERDUE | Ikke importert, lag-vindu overskredet | rød |

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | Datamodell + migreringer + backfill av expectations | 2-3 t |
| 2 | Importør-utvidelser (settlement, SCADA, operlog) | 2-3 t |
| 3 | Backfill av `data_imports` fra eksisterende historikk | 1-2 t |
| 4 | `IDataCompletenessQueryService` + tester | 3 t |
| 5 | API-endepunkter | 1 t |
| 6 | `/data-status`-side med matrise | 4-5 t |
| 7 | PlantAdmin-utvidelse for å toggle kilder | 1-2 t |
| 8 | Ukentlig digest-job + e-post | 3-4 t |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Periode-granularitet | Månedlig i v1 |
| Coverage-terskel for COMPLETE | ≥ 95 % |
| OVERDUE-trigger | period_end + expected_lag_days < now |
| Default expected_lag_days | 7 dager for settlement, 5 for SCADA |
| Ukentlig digest | Mandag kl 08:00 lokal tid |
| E-post-mottaker | drifts-leder (konfigurerbar) |
| Aktivering av kilder | Toggle per anlegg via PlantAdmin-UI |
| Cross-plant-sammenheng | Ikke håndtert i v1 (cross-plant vannflyt — egen spec) |

## Spørre-policy

- Hvis backfill av `data_imports` fra eksisterende historikk er komplisert (manglende metadata): start uten historikk, bygg fremover
- Hvis SMTP / Microsoft Graph ikke er konfigurert: bygg jobben med stub e-post-sender og logg melding til konsoll. Send via e-post når infrastruktur er på plass.
- Hvis et anlegg har 0 forventede kilder: skal IKKE vises i matrisen som "alle OVERDUE" — vis "ingen aktive kilder"

## Brukerinput som kreves underveis

| Punkt | Når |
|---|---|
| E-post-adresse til drifts-leder | Steg 8 |
| Bekreftelse av default expected_lag_days per kilde | Steg 1 |
| Aktive kilder per anlegg (initial backfill) | Steg 1 — kan endres senere via UI |

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 1: SQL viser 11 plants × 2-3 default-kilder = ~30-40 expectation-rader
2. Etter steg 2: ny settlement-import skriver én rad til `data_imports`
3. Etter steg 3: historikk for siste 6 måneder er på plass for settlement
4. Etter steg 6: `/data-status` viser matrise med Drivdal grønt for alle perioder, mens nye anlegg viser PENDING/OVERDUE riktig
5. Etter steg 8: manuell trigger sender e-post med korrekt summary

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-IMPORT-COMPLETENESS.md` med:
- Antall expectation-rader backfilled
- Antall historiske importer logget
- Skjermbilde av første dashboard-rendering
- Innhold i første ukentlige e-post-digest
- Liste over OVERDUE-rader som ble identifisert ved første kjøring

Foreslåtte oppfølginger:
- Auto-pull fra leverandør-API (KAIA, Hydrogrid) når protokoll er på plass
- Per-dag-granularitet for SCADA (continuous data)
- SLA-sporing med eskalering til leverandør hvis OVERDUE > 14 dager
- Mobile push-varsler
