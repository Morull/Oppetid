# Neste sesjon — Hydrogrid API-integrasjon + diagnostikk

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-HYDROGRID-API.md`
**Estimat:** 6-8 dager (full stack inkludert kryss-anleggs-outlier)
**Forutsetning:** Hydrogrid API-spec hentet fra developer-portalen + auth-credentials i Key Vault

## Mål

Bygg full integrasjon mot Hydrogrid Insight API for å avdekke **hvorfor** Hydrogrid bommer på timing-planene og **hvilke anlegg** som har modellfeil hos Hydrogrid. I dag har vi bare `ProduksjonplanMwh` fra KAIA-Excel — vi ser at planen bommer, men ikke hvorfor.

Fem diagnostikk-moduler bygges på samme datalager:

1. **Forecast-vs-faktisk** — Hydrogrids spot-prognose mot realisert spot. RMSE, bias, korrelasjon med plan-avvik.
2. **Plan-rasjonale-attribusjon** — for hver bommet time: hvorfor planla Hydrogrid det de planla? Diagnose-enum: FORECAST_ERROR / CONSTRAINT / WATER_VALUE_MISMATCH / OPERATIONAL_OVERRIDE / UNKNOWN.
3. **Vannverdi-tracker** — vannverdi-utvikling over tid + korrelasjon mot magasinstand.
4. **Plan-stabilitet** — antall plan-revisjoner per time, sprik mellom versjoner.
5. **Kryss-anleggs-outlier-deteksjon** — peer-sammenligning innen topology-grupper. Anlegg som systematisk får annerledes plan enn peers indikerer Hydrogrid-modellfeil for det anlegget (feil installert kapasitet, feil HRV/LRV, manglende constraint, feil tilsigsmodell).

## Bekreftet fra Hydrogrid (kilde: hydrogrid.ai/implementation)

> "Continuously transmit live values of all plant components (turbines, reservoirs, gates). Receive the optimized results, like inflow forecasts, turbine schedules, and water values (depending on your subscription)."

Bekreftet:
- REST API eksisterer (Developer Resources tilgjengelig for kunder)
- API gir oss tilbake: tilsigsprognose, turbinplaner, vannverdi
- IT-integrasjons-partnere er sertifisert hvis vi vil bruke en

## Pre-requisites før kodingen starter

| Punkt | Hvordan |
|---|---|
| API-spec / OpenAPI | Logg inn på Hydrogrid Insight Dashboard → Developer Resources |
| Client credentials | Be om OAuth2 client_id + client_secret fra Plant Success Manager |
| Plant-UUID-mapping | Hver av våre 11 plant-id må mappes til Hydrogrids interne UUID |
| Abonnementssjekk | Bekreft at abonnementet inkluderer forecast + water value (kan være tier-låst) |
| Rate limits | Sjekk i developer-docs hvor mange API-kall per minutt vi har |

**Hvis disse mangler: STOPP. Send spørsmålene til Hydrogrid og pause sesjonen.**

## Antakelser i specen

Specen forutsetter:
- REST/JSON over HTTPS
- OAuth2 client credentials
- Endpoint: `GET /v1/plants/{id}/plans?from=&to=` returnerer plan-versjoner med struktur `{ plan_version_id, generated_at, hours: [{ time, plan_mwh, spot_forecast, inflow_forecast, water_value, reservoir_forecast, binding_constraint }] }`

**Når faktisk API-spec er hentet: oppdater specens "Datamodell" og "API-klient"-seksjoner FØR koden skrives.**

## Anleggsgruppering for kryss-anleggs-modul

Modul 5 sammenligner anlegg innen samme topology-gruppe. Backfill av `core.plants.comparison_group`:

| Gruppe | Anlegg | Karakteristikk |
|---|---|---|
| `magasin_regulert` | drivdal, lindland | Stor magasinkapasitet, høy regulerings-fleksibilitet |
| `kaskade_regulert` | haukland, honnefoss, liavatn, ogreyfoss | Kaskade-topology, moderat regulering |
| `lite_magasin` | logjen, grodemfoss, orsdalen | Begrenset regulerings-volum |
| `run_of_river` | vikesa, stolskraft | Lite/ingen magasin |

Disse er pragmatiske og kan justeres etter første outlier-rapport.

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Hent API-spec + auth → oppdater spec hvis avvik | utenfor scope | Bruker leverer dette |
| 1 | Datamodell + migreringer (snapshots, plan_hours, comparison_group, outlier_reports) | 3-4 t | – |
| 2 | `IHydrogridApiClient` + implementasjon + auth + retry | 4-6 t | – |
| 3 | `HydrogridSyncJob` + cron + on-demand | 3 t | – |
| 4 | Test-sync mot Drivdal | 1 t | **STOPP — rapporter første respons-struktur** |
| 5 | `IHydrogridDiagnosticsService` + 5 metoder | 8-10 t | – |
| 6 | API-endepunkter + DTO-er | 2-3 t | – |
| 7 | Forecast-vs-faktisk-graf + KPI-rad | 3-4 t | – |
| 8 | Attribusjons-tabell | 2-3 t | – |
| 9 | Vannverdi-graf + plan-stabilitet-heatmap | 3-4 t | – |
| 10 | Status-widget + knapp på Produksjon-siden | 1-2 t | – |
| 11 | End-to-end-test for Drivdal feb-2026 | 1 t | – |
| 12 | Modul 5: kryss-anleggs-outlier — daglig job + /hydrogrid/outliers-side | 8-10 t | **STOPP etter første outlier-rapport — verifiser at flaggede anlegg gir mening drifsmessig** |

**Estimat totalt:** 6-8 dager etter at API-spec er hentet og auth fungerer.

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Auth | OAuth2 client credentials (verifiseres mot API-spec) |
| Polling | Hver time, kl :05. On-demand-knapp i UI også |
| Lagrings-strategi | UPSERT på `(plant_id, plan_version_id)`. Akkumulerer historikk. |
| `raw_response` | Lagres som JSONB for revisjonsspor |
| Diagnose-heuristikk | 5 enum-verdier; terskler dokumentert i spec |
| **Outlier-statistikk** | **MAD-basert robust z-score, terskel 3.0. Mer robust enn standard z-score når én outlier kan dominere.** |
| **Outlier-gruppering** | **4 topology-grupper. Sammenligning kun innen gruppe.** |
| **Outlier-schedulering** | **Daglig kl 06:00, rullende 7-dagers vindu. Persistert for trending.** |
| Multi-tenant | Ikke i v1 — én Hydrogrid-konto for hele Dalane Kraft |
| Skriv-tilbake | Kun read-only i v1 |

## Spørre-policy

- **API-respons-struktur avviker fra antakelsene:** STOPP og oppdater spec før koden skrives
- **Auth feiler 3 ganger:** STOPP og verifiser credentials
- **Et plant returnerer 404 fra Hydrogrid:** logg som warning, fortsett med øvrige plants
- **Vannverdi mangler i abonnement:** vis tom kolonne i UI med "Ikke tilgjengelig på dette abonnementet"-melding, fortsett ellers
- **Outlier-rapport flagger åpenbart upassende anlegg** (drift-leder vil oppdage dette): STOPP og rapporter — terskler eller gruppering kan være feil

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 4: minst én snapshot for Drivdal i `hydrogrid_snapshots`-tabellen, og full plan-respons logget for review
2. Etter steg 5: `GET /api/v1/plants/drivdal/hydrogrid/forecast-vs-actual?from=2026-02-01&to=2026-03-01` returnerer RMSE/MAE/bias for feb-2026 — ikke null, ikke exception
3. Etter steg 8: åpne `/hydrogrid/drivdal`-siden, verifiser at attribusjons-tabellen viser minst én rad med diagnose `FORECAST_ERROR` (Drivdal feb-2026 hadde betydelig timing-feil)
4. Etter steg 12: åpne `/hydrogrid/outliers` for siste 7 dager. Forventet: flest grupper har 0 outliers (normaltilstand). Hvis ett anlegg er flagget gjentatte ganger på `utilization`-metrikken, er det reell mistanke om kapasitets-konfig-feil hos Hydrogrid for det anlegget.

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-HYDROGRID.md` med:
- Kommit-tabell per steg
- Bekreftelse at faktisk Hydrogrid API matcher specens antakelser (eller liste over avvik)
- Drivdal feb-2026 forecast-vs-actual-tall: RMSE, bias
- Første outlier-rapport: hvilke anlegg ble flagget, på hvilke metrikker, og din vurdering om det stemmer eller om terskler bør justeres
- Liste over plant_id som ikke kunne synces (manglende UUID-mapping eller abonnement)

Foreslåtte oppfølginger:
- ML-modell på toppen som vurderer Hydrogrids prognose-pålitelighet over tid
- Webhook-integrasjon i stedet for polling
- Skriv-tilbake av drift-overstyring til Hydrogrid (de kan da lære fra avvik)
- Auto-justering av outlier-terskler basert på rolling statistikk
- Sammenligning på tvers av prisområder hvis Dalane utvider porteføljen utenfor NO2
