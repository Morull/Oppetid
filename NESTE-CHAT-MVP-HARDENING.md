# Neste sesjon — MVP-hardening

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-MVP-HARDENING.md`
**Estimat:** 2-3 dager
**Bakgrunn:** Statisk kodegjennomgang + brukervalidering 2026-04-30. Fire konkrete forbedringer høyner appens pålitelighet.

## Mål

Fire korte tiltak som henger sammen som "MVP-hardening" — gjør appen mer pålitelig som drifts-verktøy uten å vente på SaaS-arkitektur:

| # | Tiltak | Verdi |
|---|---|---|
| A | AllowAnonymous-audit + auth | Beskytter mot uautorisert API-bruk |
| B | Drivdal-regresjonstest reaktivering | Fanger KPI-regresjon ved fremtidige endringer |
| C | Datakvalitets-widget i UI | Gjør usikkerhet i datagrunnlag synlig |
| D | PlantType aktiv i klassifikator | Run-of-river behandles ikke likt magasin-anlegg |

## Forutsetninger

| Punkt | Hvordan |
|---|---|
| Azure AD-tenant for Entra ID | Bekreft at Dalane Kraft har Microsoft 365-konto |
| Plant-type per anlegg (Tiltak D) | Drifts-leder bekrefter mappingen i specens tabell |
| `IAuditLogger` fungerer | Eksisterer i kodebasen, brukes allerede |

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 1 | A1-A2: Audit-rapport for AllowAnonymous + endpoint-klassifisering | 1 t | **STOPP — gi bruker rapport** |
| 2 | A3: Entra ID-oppsett | 2-3 t | – |
| 3 | A4-A5: AllowAnonymous → RequireAuthorization + audit-logg | 2 t | – |
| 4 | B: Drivdal-regresjonstest reaktivering med fasit | 2-3 t | – |
| 5 | C: DataQualityQueryService + UI-widgets | 3-4 t | – |
| 6 | D: PlantType-forgrening i klassifikator + tester | 4-6 t | **STOPP — drifts-leder bekrefter at run-of-river-klassifisering er riktig** |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Auth-mekanisme | Entra ID med Authenticated/Admin-roller |
| Fallback-policy | `Authenticated` — alle nye endepunkter krever auth som default |
| Drivdal-fasit-data | Hardkodet i test-kode (ikke separat JSON-fil) |
| Fasit-toleranse | ±2-5 % avhengig av KPI |
| DqState-eksponering | Per anlegg per periode i portefølje + detalj på anleggs-side |
| DqState-filter | Toggle-knapp på Nedetid- og Produksjon-sidene |
| Plant-type-heuristikk | RunOfRiver: 0/0 → ResourceUnavailable. PumpedStorage: negativ MWh → InService. RegulatedHydro/Mixed: dagens logikk. |

## Drivdal feb-2026 fasit (for tiltak B)

| KPI | Forventet | Toleranse |
|---|---|---|
| ServiceHours | 580–620 | – |
| AvailabilityFactor | 0.96 | ±0.02 |
| BidDelivery | 0.95–1.0 | – |
| Antall events | 13 | eksakt |
| Reddet NOK (etter overløp) | 10 696 | ±5 % |
| dagCr | 1.0857 | ±0.005 |
| PlanTreffProsent | 0.658 | ±0.02 |
| AndelProdIToppKvartil | 0.147 | ±0.02 |
| HydrogridMerverdiNok | -21 697 | ±10 % |

Kilde: `OVERLEVERING-2026-04-29-VEIKART.md` + Cowork-samtale 2026-04-30.

## Plant-type-mapping (for tiltak D)

Foreslått, krever bekreftelse fra drifts-leder før commit:

| Anlegg | Foreslått type | Bekreftet? |
|---|---|---|
| Drivdal | RegulatedHydro | ⏳ |
| Lindland | RegulatedHydro | ⏳ |
| Haukland | RegulatedHydro (kaskade) | ⏳ |
| Honnefoss | RegulatedHydro (kaskade) | ⏳ |
| Liavatn | RegulatedHydro (kaskade) | ⏳ |
| Øgreyfoss | RegulatedHydro (kaskade) | ⏳ |
| Logjen | RegulatedHydro | ⏳ |
| Grødemfoss | RunOfRiver eller Mixed | ⏳ |
| Ørsdalen | RegulatedHydro | ⏳ |
| Vikeså | RunOfRiver | ⏳ |
| Stølskraft | RunOfRiver | ⏳ |

Hvis drifts-leder er usikker: bruk RegulatedHydro som default. Det er bevarende — gir samme atferd som i dag for anlegget.

## Spørre-policy

- Etter steg 1: rapporter audit-funn til bruker FØR du endrer noe
- Hvis Entra ID ikke kan settes opp i steg 2: pause og foreslå alternativ 2 (API-key)
- Hvis Drivdal-fasit ikke matcher etter steg 4: STOPP og rapporter avvikene — kan indikere reell regresjon
- Etter steg 6: kjør klassifikator-test mot reelt anlegg og vise drifts-leder en time-by-time-prøve før commit
- Plant-type for et anlegg må være bekreftet — ikke gjett

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 3: `curl /api/v1/plants/drivdal/admin -X POST` returnerer 401 uten token
2. Etter steg 4: `dotnet test --filter DrivdalRegression` passerer (ingen Skip)
3. Etter steg 5: portefølje-side viser datakvalitet-kolonne for alle 11 anlegg
4. Etter steg 6: Vikeså 0/0-timer er `ResourceUnavailable` (ikke `ReserveShutdown`)

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-MVP-HARDENING.md` med:
- Audit-rapport: hvilke endepunkter ble endret fra Allow til Require
- Entra ID-config: hvilke roller, hvilke brukere er lagt til (eller "ikke tilgjengelig")
- Drivdal-fasit-resultater: alle KPI-er innenfor toleranse, eller liste over avvik
- Plant-type-mapping: bekreftet av drifts-leder, hvilke anlegg fikk endret klassifisering
- Endrings-rapport for klassifikator: hvor mange timer ble re-klassifisert (Drivdal vs Vikeså sammenligning)
