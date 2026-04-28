# Virkningsgrad- og effektivitets-analyser

Dato: 2026-04-28
Status: Spec — ikke startet implementasjon
Forutsetning: SCADA-data tilgjengelig som hourly aggregat (eller bedre)
Skala-mål: ~20 anlegg, flere samtidige brukere, server-deployment

## Skopens kontekst

Dette spec'et beskriver hvordan vi går fra en månedlig SCADA-eksport (62 tags
i Drivdal-eksempelet) til operativ innsikt om hvor optimalt anlegget kjører
i forhold til tilgjengelig vann. Resultatet er nye KPI-er i den eksisterende
katalogen og to nye dashboards: **Effektivitet** (per anlegg) og
**Portefølje-effektivitet** (alle anlegg side om side).

Forventet effekt for Dalane Krafts portefølje:
- Identifisere anlegg som driftes utenfor sweet-spot ⇒ taps-tall i kWh og NOK
- Tidlig varsel om fall i virkningsgrad (slitasje, tilstopping) før det blir feil
- Datadrevne grunnlag for revisjons- og oppgraderingsbeslutninger

## Signal-whitelist for SCADA-import

Av 62 tags i master-eksporten beholder vi 39 (~37 % reduksjon). Under er
kategorisering med prioritet for whitelist-tabellen `core.signal_map` som
designet i `ARKITEKTUR-SCADA.md`.

### Essensielle for virkningsgrad (9 tags)

| Tag | Enhet | Rolle |
|---|---|---|
| `G1_TURB_VIRKNGRD_PV` | % | Turbin-η — referanse fra SCADA |
| `G1_TURB_VF_PV` | m³/s | Q gjennom turbin |
| `G1_GEN_P_PV` | kW | Aktiv effekt ut |
| `INNTAK_NIVA_OPPSTROM_KOTE_PV` | moh | Vannstand inntak |
| `INNTAK_NIVA_NEDSTROM_KOTE_PV` | moh | Vannstand utløp |
| `G1_RORGATE_VANN_TRYKK_PV` | bar | Trykk i rørgate (kontroll) |
| `G1_TURB_PADRAG_PV` | % | Turbin-pådrag (settpunkt-respons) |
| `G1_TURB_LEDEAPP_POS_PV` | % | Ledeapparat-posisjon |
| `INNTAK_RIST_FALLTAP_PV` | mm | Falltap over rist |

### Driftstilstand (7 tags)

| Tag | Enhet |
|---|---|
| `G1_GEN_TURTALL_PV` | rpm |
| `G1_GEN_F_PV` | Hz |
| `G1_GEN_COSPHI_PV` | (–) |
| `G1_GEN_Q_PV` | kVar |
| `G1_GEN_S_PV` | kVa |
| `G1_GEN_TIMETELLER_PV` | t |
| `G1_GEN_PROD_I_AR_PV` | kWh |

### Magasin / hydrologi (8 tags)

| Tag | Enhet | Rolle |
|---|---|---|
| `INNTAK_MAGASIN_VOLUM_PV` | Mill m³ | Lagret vann |
| `INNTAK_MAGASIN_FYLLGRAD_PV` | % | Andel av HRV |
| `INNTAK_MAGASIN_TOT_VF_PV` | m³/s | Tilsig |
| `INNTAK_MAGASIN_NED_KAP_PV` | moh | LRV-referanse |
| `INNTAK_NIVA_OPPSTROM_REF_HRV_PV` | moh | HRV-referanse |
| `INNTAK_NIVA_OVERLOP_VF_PV` | m³/s | Overløp |
| `INNTAK_MINVF_LITER_PV` | l/s | Minstevannføring (settpunkt) |
| `INNTAK_MINVF_CM_PV` | cm | Minstevannføring (måling) |

### Tilstand-indikatorer (10 tags)

Lager-, vikling- og olje-temperaturer + hydraulikk-trykk. Brukes til
prediktiv vedlikehold og som anomaly-signaler. Ikke kritisk for
virkningsgrad, men sterkt anbefalt å lagre.

### Redusert elektrisk (5 tags)

Kuttet fra 16 til 5: 1 hovedspenning (`L1_L2`), 1 fasestrøm (`L1_I`),
samt kanskje `NETT_LINJE_F_PV` for nett-frekvens. Fase-symmetri sjekkes
ikke kontinuerlig — alarmer på asymmetri kommer fra SCADA hvis det
faktisk skjer.

### Settpunkt-tags (0 i `sample_facts`, alle via operlog)

`AUTOSTART_NIVA`, `AUTOSTOPP_TID`, `REG_NIVA_SP`, `REG_P_SP`, `AGC_DB_SP`
osv. lever som menneske-utløste endringer. De fanges allerede i
operlog-eksporten. Lagres som `core.scada_event_log`-rader (timestamp +
tag + ny verdi + bruker), ikke som tidsserie.

## KPI-katalog: 8 nye virkningsgrad-nøkkeltall

Disse legges i en ny "effektivitet"-kategori i KPI-katalogen, ved siden
av drift / marked / økonomi. Beregnes av en ny worker-modul
`KraftverkUptime.Modules.Efficiency` som leser fra `sample_facts_1hour`
(continuous aggregate).

### 1. TurbinVirkningsgrad_AVG (%)

```
η_turb_avg = AVG(TURB_VIRKNGRD_PV)  per periode
```

Direkte aggregat. Sammenlignes mellom perioder for å se trend.

### 2. EgenBeregnetVirkningsgrad_AVG (%)

```
η_egen,t = P_gen,t / (ρ · g · Q_t · H_netto,t)

H_brutto = OPPSTROM_KOTE − NEDSTROM_KOTE
H_tap   = RIST_FALLTAP/1000 + K · Q²        (K kalibreres fra historikk)
H_netto = H_brutto − H_tap
ρ = 1000 kg/m³,  g = 9.81 m/s²
```

Egen beregning som verifiserer SCADA-η. Avvik > 1-2 % mellom #1 og #2 = flag.

### 3. AvvikSCADAvsEgen_AVG (%-poeng)

```
Δη = η_turb_avg − η_egen_avg
```

Driftet: SCADA-måler som henger eller falsk-rapporterer.

### 4. SpesifiktVannforbruk_m3_per_kWh

```
SVF = (Σ Q_t · 3600) / (Σ P_t)        [m³/kWh]
```

Operativ "kjørtøy-økonomi". Lavere = mer kWh per liter vann.
Trend over måneder forteller om anlegget gradvis bruker mer vann
for samme produksjon (slitasje på turbin-kniver, lekkasje i ledeapparat).

### 5. SweetSpotAndel_pct

```
P_optimal = argmax_P (η(P))                    fra historisk η(P)-kurve
SweetSpotAndel = timer hvor |P − P_optimal| / P_optimal < 0.10
                ÷ totale produserende timer
```

Andel av drifts-timer der vi kjørte innenfor ±10 % av optimalt
belastningsnivå. Lavt tall = mye drift på dårlig η-belastning.

### 6. EnergitapVsOptimal_NOK

```
Tap_t = (η_optimal − η_t) · ρ · g · Q_t · H_netto · Δt   [kWh]
NOK_t = Tap_t · spotpris_t

EnergitapVsOptimal = Σ NOK_t  for produksjonstimer
```

Tallfester effektivitets-tapet i kroner. Det vi kunne tjent ekstra med
perfekt η på samme vannmengde, vurdert mot timesvis spotpris.

### 7. RorgateFalltapKoeffisient_K

```
Modell: H_tap = K · Q²
Regresjon: K = AVG(H_tap / Q²) for produksjonstimer

K_baseline = K beregnet fra første mnd med ren drift
K_drift    = (K_aktuell − K_baseline) / K_baseline
```

K skal være konstant for ren rørgate. Når K stiger over tid → tilstopping
(grus, blader, isgang). Trigger vedlikeholds-flagg når `K_drift > 15 %`.

### 8. PadragLedeappLinearitet_R2

```
R² for lineær regresjon mellom PADRAG_PV og LEDEAPP_POS_PV
```

Skal være > 0.98. Lavere = regulator-utfordring eller mekanisk slack i
ledeapparatet. Tidlig vedlikeholdsindikator.

## Nye dashboards

### A. Effektivitet (per anlegg) — `/reports/{plantId}/{key}/efficiency`

Samme rapport-id som settlement-rapporten, men ny side:

- **Header-kort:** η_AVG, SVF, sweet-spot-andel, tapt NOK
- **η(P)-kurve** — scatter plot med fitted polynomial
- **η-trend over måneden** — line chart med band for "akseptabelt område"
- **Falltap-kurve** — H_tap mot Q², avvik fra Q²-modell flagges
- **Pådrag-vs-ledeapparat** — scatter med regresjons-linje
- **Sammenligning SCADA-η vs egen-beregnet** — line chart med differanse-spor

### B. Portefølje-effektivitet — `/portefolje/effektivitet`

Cross-anleggs-visning:

- **Tabell:** alle 20 anlegg med nyeste η_AVG, SVF, sweet-spot, tapt NOK
- **Sortering:** klikkbar på hver kolonne (default: tapt NOK descending — mest gevinst først)
- **Heatmap:** η per anlegg per måned (rad = anlegg, kolonne = måned, farge = η)
- **Drilldown:** klikk på rad → anleggets effektivitets-side

Krever `kpi_facts`-tabellen (se `ARKITEKTUR-SCADA.md` § 5).

## Operlog → automatiske annoteringer

Operlog-CSV (222 hendelser i februar for Drivdal) er en gullgruve for
å fylle ut driftstidslinjen uten manuelt arbeid:

| Operlog-tag | Auto-annotering |
|---|---|
| `STARTER_AL` event | Kategori: `auto_start` (ny system-kategori) |
| `STOPPER_AL` event | Kategori: `auto_stop` |
| `FEIL_AL` med category 3 | Kategori: `fault` (system-existing) |
| `AGC_AKTIV` toggle 1→0 | Kategori: `manual_takeover` |
| `REG_P_SP_SP_LAST` endring | Comment-annotering uten state-override |

Implementasjon:
- Ny worker-jobb `ImportOperlogJob` som tar operlog-CSV
- Mapper events til annoteringer via lookup-tabell `core.operlog_to_annotation`
- Setter `created_by = "operlog:" + operlog.username`
- Soft-skipper duplikater (samme plant_id + start_utc + tag)

Dette gjør at brukeren ser detaljerte drifts-events automatisk på
tidslinjen — uten å måtte klikke 222 ganger.

## Skala til 20 anlegg + flere brukere + server-drift

### Datavolum

| Resolusjon | Per anlegg/år | 20 anlegg/5 år |
|---|---|---|
| Hourly aggregate | ~4 MB | ~400 MB |
| 1-min aggregate | ~240 MB | ~24 GB |
| Raw 1-Hz (60-dagers retensjon) | ~17 GB | ~340 GB rå, ~34 GB komprimert |

Alt håndterbart med TimescaleDB-foundation fra `ARKITEKTUR-SCADA.md`.

### Compute-budsjett

KPI-beregning per anlegg per måned:
- Lese ~720 timer × 39 tags = ~28 000 verdier
- Regresjon for K, R² og η(P) — sub-sekund med NumPy/MathNet
- Lagre 8 KPI-er + intermediær η(P)-kurve = trivielt

Per måned for hele porteføljen: ~20 anlegg × ~1 sek = 20 sek total. Worker
kan kjøre dette som scheduled job daglig (idempotent — re-beregner siste
30 dager) uten skala-bekymringer.

### Multi-bruker hensyn

| Aspekt | Plan |
|---|---|
| Auth | Allerede skissert i v1: bytt til Entra ID, RequireAuthorization-policies på endpoints (PlantReader / PlantAnalyst) |
| Rate-limiting | Allerede aktivert i Program.cs (100 req/min per IP). Øk til 1000 ved server-drift med flere brukere |
| Tenant-isolasjon | `IQueryContext` filtrerer `OwnerOrgId` automatisk — alle nye tabeller arver dette ved å implementere `IOwnedEntity` |
| Concurrent annotations | Soft-delete + replaceIds beskytter mot race conditions. To brukere som annoterer samme periode samtidig får én vinner og en 409. |
| Cache | KPI-fakta har naturlig cache-vennlig form (immutable per import-id). API kan sette `Cache-Control: max-age=3600` |
| Read-heavy | Continuous aggregates + kpi_facts fjerner dyre ad-hoc-spørringer. Read-replica av Postgres ved >50 samtidige brukere |

### Server-deployment

- **Database:** TimescaleDB på Azure Database for PostgreSQL Flexible Server eller dedikert VM. Backup hver 24 t. Read-replica klar når trafikken krever det.
- **API + Worker:** Container Apps eller App Service. Auto-skalering på CPU.
- **Web:** Statisk Blazor WASM serverer fra Blob Storage med CDN. Cache-busting via fingerprint-hashing.
- **Blob:** for blob-rapporter (settlement) — kald-lagring etter 90 dager.
- **Observability:** Application Insights (allerede koblet via OpenTelemetry-pakkene i csproj). Definer alarmer på 5xx-rate, query-latency, ingest-lag.
- **Secrets:** Key Vault — allerede satt opp i `AddKraftverkKeyVault`.
- **CI/CD:** GitHub Actions med `dotnet test` + `dotnet ef migrations apply` mot staging før prod-deploy.

### Anleggs-onboarding

For hvert nytt anlegg trengs:
1. Rad i `core.plants` (plant_id, navn, type, MW, time-zone)
2. Rader i `core.signal_map` med 39 tags spesifikt for det anlegget — kan kopieres fra Drivdal-malen og justeres for tag-prefiks
3. SCADA-eksport-konfigurasjon: tags, frekvens, format, levering (CSV-opplastning til /api/v1/plants/{id}/scada eller MQTT-stream)
4. Optional: tilpasset `PlantClassificationConfig` (NominalPowerMw, terskler)

Onboarding tar ~1 time per anlegg når mal er på plass.

## Implementasjonsrekkefølge

| # | Steg | Avhengigheter | Estimat |
|---|---|---|---|
| 1 | TimescaleDB + `sample_facts` + LOD | (foundation) | 1 dag |
| 2 | `signal_map` lookup-tabell + admin-side | 1 | 4 t |
| 3 | CSV-import-pipeline (whitelist-filtrert) | 1, 2 | 1 dag |
| 4 | `KraftverkUptime.Modules.Efficiency` + 8 KPI-er | 1, 3 | 2 dager |
| 5 | `kpi_facts`-tabell + materialisering | 4 | 4 t |
| 6 | Effektivitets-side per anlegg | 4, 5 | 1 dag |
| 7 | Portefølje-effektivitets-side | 5, 6 | 1 dag |
| 8 | Operlog-importer + auto-annotering | 1, 2 | 1 dag |

Totalt: ~9-10 arbeidsdager. Kan deployes i etapper — etter steg 6 har
vi allerede full virkningsgrad-analyse for ett anlegg av gangen, og kan
validere mot Drivdal-data før portefølje-utrullingen.

## Avhengighet til reell SCADA-data

For at noe av dette skal gi mening trenger vi en eksport som faktisk
inneholder verdier (ikke NaN). Når den er på plass er trinn 1-3 raske
fordi datamodellen og pipeline er det samme uavhengig av om filen er
tom eller full.

I mellomtiden kan vi:
- Bygge skjelettet (foundation, signal_map, import-pipeline) klart
- Validere KPI-formler mot syntetisk testdata (vi konstruerer en realistisk
  η(P)-kurve og feeder den gjennom calc-en for å bevise at formlene er
  riktige)
- Sette opp dashboard-skjellettet med dummy-tall

Når reell data kommer er det `dotnet test` + visuell sjekk og du har
analysen levende.

## Risiko og åpne spørsmål

1. **Tag-navn-konvensjon:** Drivdal har `DRIVDAL_G1_*`. For Birkeland og
   andre verk kan prefikset variere. `signal_map` håndterer dette per
   anlegg — ingen kode-endring.
2. **Manglende tags:** Anlegg uten ledeapparat-sensor (Pelton-verk)
   mangler #8. Da rapporteres KPI som `null` — ikke feil, bare ikke
   relevant. Frontend håndterer null grasiøst (allerede pattern).
3. **K-kalibrering:** krever ~1 måneds rene drifts-data uten endringer.
   Første gang en KPI rapporteres for et anlegg vil K_baseline være
   "siste tilgjengelige måned"; senere blir det `K` fra anleggets første
   ren-drift-måned (markerer manuelt).
4. **Spotpris for #6:** finnes i settlement-data. Cross-join på time
   mellom KPI-fakta og settlement-fakta. Noen timer mangler spotpris (helt
   nye importer ennå ikke ferdige) — vi bruker NULL i de cellene.
5. **SCADA-η og egen-η-formel-konsistens:** SCADA bruker en intern formel
   som kan inkludere generator-virkningsgrad (η_total = η_turb · η_gen).
   Vår H-baserte formel er konvensjonelt η_total. Hvis SCADA-η bare er
   turbin (mekanisk) blir #3 systematisk lik (1 − η_gen) ≈ 2-3 %.
   Avklares ved sammenligning mot fabrikat-data fra G1.
