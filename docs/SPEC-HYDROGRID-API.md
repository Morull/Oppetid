# Spec: Hydrogrid API-integrasjon — diagnostikk-modul

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 5-7 dager (full integrasjon + 4 moduler + UI)
**Avhengighet:** Tilgang til HYDROGRID Insight Developer Resources (RestAPI-spec) og kvalifisert abonnement
**Forutsetning:** Dalane Kraft har API-tilgang med følgende data eksponert: produksjonsplaner (med versjonering), tilsigsprognoser, vannverdi-estimater, og spotpris-prognoser brukt ved planlegging

## Bakgrunn

Drifts-leder så Drivdal feb-2026 (Hydrogrid timing-merverdi −21 697 NOK, topp-pris-utnyttelse 14,7 %) og spurte: *"kan vi avdekke hva som har gått galt og hvorfor Hydrogrid bommer?"*

I dag har vi bare `ProduksjonplanMwh` per time fra KAIA-Excel. Det forteller oss **hva** planen var, ikke **hvorfor** planen ble som den ble. Uten input-dataen Hydrogrid baserte planen på, kan vi bare konstatere at det bommet — ikke om årsaken er feil prognose, feil vannverdi-estimat, eller en aktivert constraint.

Hydrogrid eksponerer disse dataene via REST API (kilde: hydrogrid.ai/implementation, 2026-04-30):

> "Receive the optimized results, like inflow forecasts, turbine schedules, and water values (depending on your subscription)."

Når vi har API-tilgang kan appen bygge fire diagnostikk-moduler som svarer på "hvorfor bommet planen for time t?".

## Beslutning

Bygg fem moduler basert på samme `core.hydrogrid_snapshots`-tabell:

| Modul | Spørsmål den svarer på | Prioritet |
|---|---|---|
| **1. Forecast-vs-faktisk** | "Trodde Hydrogrid det var høypris da det viste seg å være lavpris?" | Høy — direkte svar på drifts-leders spørsmål |
| **2. Plan-rasjonale-attribusjon** | "Hvorfor produserte Hydrogrid akkurat denne timen — på grunn av spot, tilsig, eller constraint?" | Høy — per-time post-mortem |
| **3. Vannverdi-tracker** | "Har vannverdien Hydrogrid bruker konsistens med magasinstand-utviklingen?" | Medium — strategisk innsikt |
| **4. Plan-stabilitet-måler** | "Hvor mange revisjoner gikk planen gjennom? Var den stabil eller usikker?" | Lav — diagnose-supplement |
| **5. Kryss-anleggs-outlier-deteksjon** | "Får ett anlegg systematisk annerledes planer enn de andre under like forhold? Indikerer bug i Hydrogrids modell for det anlegget." | Høy — fanger modell-feil ingen annen analyse vil se |

## Datamodell

### Ny tabell `core.hydrogrid_snapshots`

Hvert snapshot er én plan-versjon for ett anlegg, levert av Hydrogrid på et gitt tidspunkt. Snapshots akkumuleres slik at vi har full historikk.

```sql
CREATE TABLE core.hydrogrid_snapshots (
    plant_id VARCHAR(64) NOT NULL,
    plan_version_id VARCHAR(64) NOT NULL,
    generated_at_utc TIMESTAMPTZ NOT NULL,
    horizon_from_utc TIMESTAMPTZ NOT NULL,
    horizon_to_utc TIMESTAMPTZ NOT NULL,
    fetched_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    raw_response JSONB NOT NULL,
    PRIMARY KEY (plant_id, plan_version_id),
    FOREIGN KEY (plant_id) REFERENCES core.plants(plant_id)
);

CREATE INDEX ix_hgs_plant_horizon ON core.hydrogrid_snapshots(plant_id, horizon_from_utc, horizon_to_utc);
CREATE INDEX ix_hgs_generated ON core.hydrogrid_snapshots(plant_id, generated_at_utc DESC);
```

`raw_response` lagres i sin helhet for revisjonsspor og fremtidig analyse. Strukturerte felt utvides via flatened tabeller:

```sql
CREATE TABLE core.hydrogrid_plan_hours (
    plant_id VARCHAR(64) NOT NULL,
    plan_version_id VARCHAR(64) NOT NULL,
    time_utc TIMESTAMPTZ NOT NULL,
    plan_mwh DOUBLE PRECISION,
    spot_forecast_nok_mwh DOUBLE PRECISION,
    inflow_forecast_m3s DOUBLE PRECISION,
    water_value_nok_mwh DOUBLE PRECISION,
    reservoir_level_forecast_moh DOUBLE PRECISION,
    binding_constraint VARCHAR(64),    -- "min_flow", "max_ramp", "reservoir_max", null osv.
    PRIMARY KEY (plant_id, plan_version_id, time_utc),
    FOREIGN KEY (plant_id, plan_version_id)
        REFERENCES core.hydrogrid_snapshots(plant_id, plan_version_id)
);

CREATE INDEX ix_hgph_plant_time ON core.hydrogrid_plan_hours(plant_id, time_utc);
```

For hver time finnes typisk flere `plan_version_id` (én per Hydrogrid-kjøring). For diagnostikk-spørsmål bruker vi **siste plan før timen begynte** (gyldig plan ved drift-tidspunkt).

### Tabeller for kryss-anleggs-modulen

```sql
ALTER TABLE core.plants
    ADD COLUMN comparison_group VARCHAR(32) NULL;

-- Backfill-mapping basert på topology
UPDATE core.plants SET comparison_group = 'magasin_regulert' WHERE plant_id IN ('drivdal', 'lindland');
UPDATE core.plants SET comparison_group = 'kaskade_regulert' WHERE plant_id IN ('haukland', 'honnefoss', 'liavatn', 'ogreyfoss');
UPDATE core.plants SET comparison_group = 'lite_magasin' WHERE plant_id IN ('logjen', 'grodemfoss', 'orsdalen');
UPDATE core.plants SET comparison_group = 'run_of_river' WHERE plant_id IN ('vikesa', 'stolskraft');

CREATE TABLE core.hydrogrid_outlier_reports (
    report_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    period_from_utc TIMESTAMPTZ NOT NULL,
    period_to_utc TIMESTAMPTZ NOT NULL,
    plant_id VARCHAR(64) NOT NULL,
    comparison_group VARCHAR(32) NOT NULL,
    metric_name VARCHAR(64) NOT NULL,
    plant_value DOUBLE PRECISION NOT NULL,
    group_median DOUBLE PRECISION NOT NULL,
    group_mad DOUBLE PRECISION NOT NULL,
    robust_z_score DOUBLE PRECISION NOT NULL,
    is_outlier BOOLEAN NOT NULL,
    diagnosis_text TEXT,
    computed_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_hgor_plant_period ON core.hydrogrid_outlier_reports(plant_id, period_from_utc DESC);
CREATE INDEX ix_hgor_outliers ON core.hydrogrid_outlier_reports(is_outlier, computed_at_utc DESC) WHERE is_outlier = true;
```

## API-klient

### Konfigurasjon

```json
{
  "Hydrogrid": {
    "BaseUrl": "https://api.hydrogrid.ai/v1",
    "AuthMode": "OAuth2ClientCredentials",
    "TokenUrl": "https://auth.hydrogrid.ai/oauth/token",
    "ClientId": "<from-key-vault>",
    "ClientSecret": "<from-key-vault>",
    "PollingIntervalMinutes": 60,
    "Plants": {
      "drivdal": "hg-plant-uuid-drivdal",
      "lindland": "hg-plant-uuid-lindland",
      "_comment": "Mapping fra vår plant_id til Hydrogrids plant-uuid. Hentes fra Hydrogrid-kontaktperson under onboarding."
    }
  }
}
```

**Antakelser som må verifiseres mot faktisk Hydrogrid API-spec:**
- Auth-mekanisme (OAuth2 client credentials er typisk for B2B; kan også være API-key)
- URL-struktur (`/v1/plants/{id}/plans` er antatt)
- Rate-limits (krever sjekk i developer docs)
- JSON-skjema for plan-respons

Når API-spec er hentet fra developer-portalen oppdateres dette eksplisitt, og kode-stubber under refaktoreres mot faktisk respons.

### Klient-grensesnitt

**Fil:** `src/KraftverkUptime.Modules.Hydrogrid/Api/IHydrogridApiClient.cs` NY

```csharp
public interface IHydrogridApiClient
{
    /// <summary>
    /// Henter siste tilgjengelige plan for et anlegg innen et tidsvindu.
    /// Returnerer alle plan-versjoner som dekker [from, to) eller deler av det.
    /// </summary>
    Task<IReadOnlyList<HydrogridPlanSnapshot>> GetPlansAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>
    /// Henter aktuell plan-status — sist genererte plan og når neste forventes.
    /// Brukes til "Hydrogrid health"-widget.
    /// </summary>
    Task<HydrogridStatus> GetStatusAsync(string plantId, CancellationToken ct);
}

public sealed record HydrogridPlanSnapshot(
    string PlantId,
    string PlanVersionId,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset HorizonFromUtc,
    DateTimeOffset HorizonToUtc,
    IReadOnlyList<HydrogridPlanHour> Hours,
    string RawJson);

public sealed record HydrogridPlanHour(
    DateTimeOffset TimeUtc,
    double? PlanMwh,
    double? SpotForecastNokMwh,
    double? InflowForecastM3s,
    double? WaterValueNokMwh,
    double? ReservoirLevelForecastMoh,
    string? BindingConstraint);

public sealed record HydrogridStatus(
    string PlantId,
    DateTimeOffset? LastPlanGeneratedAtUtc,
    DateTimeOffset? NextPlanExpectedAtUtc,
    string? LastErrorMessage);
```

### Implementasjon

**Fil:** `src/KraftverkUptime.Infrastructure/Hydrogrid/HydrogridApiClient.cs` NY

- Bruker `HttpClient` registrert via `IHttpClientFactory`
- OAuth-token caches med 90 % av expiry-tid
- Retry-policy: Polly med eksponentiell backoff (3 forsøk, 1s/4s/16s)
- Logger hver kall + payload-størrelse + status-kode
- Throttler hvis Hydrogrid returnerer 429 — venter i `Retry-After`-headeren

## Worker-job for periodisk pull

**Fil:** `src/KraftverkUptime.Infrastructure/Hydrogrid/HydrogridSyncJob.cs` NY

```csharp
public sealed class HydrogridSyncJob : IJobHandler<HydrogridSyncRequest>
{
    public async Task ExecuteAsync(HydrogridSyncRequest req, CancellationToken ct)
    {
        // 1. For hvert plant i config.Plants:
        //    a. Kall GetPlansAsync for [now − 7 dager, now + 5 dager]
        //    b. UPSERT i hydrogrid_snapshots og hydrogrid_plan_hours
        //    c. Logg antall nye snapshots, oppdaterte timer
        // 2. Kall GetStatusAsync for hver plant — oppdater hydrogrid_health-tabell (egen)
        // 3. Trigger event HydrogridSnapshotsRefreshed for downstream-konsumenter
    }
}
```

Trigger-strategi:
- **Cron:** hver time, i 5. min etter timeskift (etter at Hydrogrid typisk har levert ny plan)
- **On-demand:** UI-knapp "Synkroniser nå" på `/hydrogrid/{plantId}`-siden
- **Backfill:** ny endepunkt `POST /api/v1/admin/hydrogrid/backfill?from=...&to=...` for historisk data hvis API støtter det

## Diagnostikk-modul (kjernen)

### `IHydrogridDiagnosticsService`

**Fil:** `src/KraftverkUptime.Modules.Reporting/Hydrogrid/IHydrogridDiagnosticsService.cs` NY

```csharp
public interface IHydrogridDiagnosticsService
{
    /// <summary>
    /// Forecast-vs-faktisk for en periode: spot-prognose, faktisk spot, forecast-feil per time.
    /// Bruker siste gyldige plan før hver time som referanse.
    /// </summary>
    Task<ForecastVsActualReport> GetForecastVsActualAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Per-time post-mortem: for hver time, vis plan-rasjonalet
    /// (spot-forecast, vannverdi, constraint) sammen med faktisk utfall.
    /// </summary>
    Task<IReadOnlyList<HourlyAttribution>> GetHourlyAttributionAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Vannverdi-utvikling over tid + magasinstand — viser om de korrelerer.
    /// </summary>
    Task<WaterValueTrackerReport> GetWaterValueTrackerAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// For hver time: hvor mange plan-versjoner ble laget? Hvor mye endret planen seg
    /// mellom versjonene? Høy revisjonsfrekvens = usikker prognose.
    /// </summary>
    Task<PlanStabilityReport> GetPlanStabilityAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Kryss-anleggs-outlier-deteksjon. Identifiserer anlegg som får systematisk
    /// annerledes Hydrogrid-planer enn peer-gruppen — typisk symptom på modell-
    /// feil hos Hydrogrid (feil topology, feil kalibrering, manglende constraint).
    /// Returnerer per-gruppe-statistikk + flaggede anlegg per metrikk.
    /// </summary>
    Task<CrossPlantOutlierReport> GetCrossPlantOutlierAnalysisAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
```

### Forecast-vs-faktisk: formler

```
For hver time t i [fromUtc, toUtc):
  siste_plan(t) = SELECT TOP 1 * FROM hydrogrid_plan_hours
                   WHERE time_utc = t AND generated_at_utc < t
                   ORDER BY generated_at_utc DESC
  
  forecast_t   = siste_plan(t).spot_forecast_nok_mwh
  faktisk_t    = settlement.spotpris_nok_mwh for time t
  feil_t       = faktisk_t − forecast_t

KPI-er:
  RMSE  = sqrt(mean((feil_t)²))
  MAE   = mean(|feil_t|)
  Bias  = mean(feil_t)             // positiv = Hydrogrid undervurderte
  Correlation_planavvik = corr(|feil_t|, |Plan_t − Elhub_t|)
                                    // høy verdi = forecast-feil driver plan-feil
```

### Plan-rasjonale-attribusjon

For hver time t hvor `|Plan_t − ElhubMwh_t| > 0.5 * InstalledCapacity` (signifikant avvik), generer en attribusjons-card med data fra siste gyldige plan:

```
{
  "time": "2026-02-15T03:00:00Z",
  "plan_mwh": 1.4,
  "actual_mwh": 0.0,
  "rationale": {
    "spot_forecast": 1450,
    "actual_spot": 480,
    "forecast_error_pct": -67,
    "water_value": 420,
    "binding_constraint": null,
    "reservoir_level_forecast": 326.5,
    "actual_reservoir_level": 326.4
  },
  "diagnosis": "FORECAST_ERROR",  // enum: FORECAST_ERROR, CONSTRAINT, WATER_VALUE_MISMATCH, OPERATIONAL_OVERRIDE, UNKNOWN
  "explanation": "Hydrogrid forventet 1450 NOK/MWh (tre ganger høyere enn faktisk 480). Planen var optimal gitt prognosen, men prognosen var feil."
}
```

`diagnosis`-enum settes via heuristikk:

| Hvis... | Diagnose |
|---|---|
| `\|forecast_error\| > 30 % AND `\|plan − actual\|` > 0.5 MW` | FORECAST_ERROR |
| `binding_constraint != null` | CONSTRAINT |
| `\|water_value − spot_forecast\|` < 50 NOK/MWh AND `plan_mwh ≈ 0`` | WATER_VALUE_MISMATCH (planen var marginalt lønnsom på papiret) |
| `Plan_t > 0 men actual_t = 0 og ingen forecast-feil og ingen constraint` | OPERATIONAL_OVERRIDE (drift overstyrte) |
| Ellers | UNKNOWN |

### Plan-stabilitet

For hvert døgn d i perioden:

```
versjoner_per_time(t) = COUNT(DISTINCT plan_version_id)
                         FROM hydrogrid_plan_hours
                         WHERE time_utc = t

snitt_versjoner_per_time = mean(versjoner_per_time(t)) for t i d

plan_endring_per_time(t) = stddev(plan_mwh) over alle plan_version_id-er for t

dag_stabilitet = 1 − (mean(plan_endring_per_time) / max(plan_mwh))
                  // 1.0 = helt stabil, 0.0 = full slingring
```

### Modul 5: Kryss-anleggs-outlier-deteksjon

**Hypotese:** Alle 11 anlegg er i samme prisområde (NO2), opplever samme værsystem, og bruker samme Hydrogrid-modell-versjon. Forskjeller mellom planene per anlegg skal derfor reflektere reelle forskjeller i magasinstand, topology og constraints — ikke modellfeil. Hvis ett anlegg systematisk får en plan som avviker fra peer-gruppen under like forhold, indikerer det en bug i Hydrogrids konfigurasjon eller modell-kalibrering for det anlegget.

Kjente symptomer i bransjen som denne modulen skal fange:
- Feil installert kapasitet konfigurert hos Hydrogrid → planlegger overproduksjon
- Feil HRV/LRV → vannverdi miscalibrated → planlegger for hardt eller for forsiktig
- Manglende constraint (f.eks. minstevannføring) → urealistisk lav minimumsproduksjon i plan
- Feil tilsigsmodell → systematisk over/underestimering av tilgjengelig vann
- Kaskade-topology feil registrert → rasjonell sett gal flytfordeling

**Anleggsgruppering for meningsfull peer-sammenligning:**

Anlegg sammenlignes innen samme topologi-gruppe siden run-of-river ikke er ekvivalent med magasin-tunge anlegg. Gruppene defineres i `core.plants` via et nytt felt `comparison_group`:

| Gruppe | Anlegg | Karakteristikk |
|---|---|---|
| `magasin_regulert` | drivdal, lindland | Stor magasinkapasitet, høy regulerings-fleksibilitet |
| `kaskade_regulert` | haukland, honnefoss, liavatn, ogreyfoss | Kaskade-topology, moderat regulering |
| `lite_magasin` | logjen, grodemfoss, orsdalen | Begrenset regulerings-volum |
| `run_of_river` | vikesa, stolskraft | Lite/ingen magasin |

Gruppene er pragmatiske — kan justeres etter at faktiske data analyseres.

**Metrikker som beregnes per anlegg per periode (typisk uke eller måned):**

```
For anlegg p i periode [from, to):

1. Kapasitets-utnyttelse:
   utilization_p = mean(plan_mwh_t / installed_capacity_mw)  for alle t

2. Pris-respons-sensitivitet:
   For timer over og under medianspot i perioden:
     plan_high_p = mean(plan_mwh) for timer der spot_forecast > median(spot_forecast)
     plan_low_p  = mean(plan_mwh) for timer der spot_forecast ≤ median(spot_forecast)
   sensitivity_p = (plan_high_p - plan_low_p) / installed_capacity_mw
   // Forventet > 0 for regulert vannkraft, ≈ 0 for run-of-river

3. Vannverdi-nivå (relativ til spot-forecast):
   wv_ratio_p = mean(water_value_t / spot_forecast_t) for timer der begge er definert
   // Forventet rundt 1.0 for godt kalibrert anlegg (ingen arbitrasje-mulighet)

4. Plan-volum-relativ-til-tilsig:
   inflow_consumption_p = sum(plan_mwh_t × specifikt_vannforbruk) / sum(inflow_forecast_m3s × 3600)
   // Forventet ≈ 1.0 over lange perioder (det vi planlegger å bruke = det vi får inn)

5. Time-of-day-mønster:
   for hver time-i-døgnet h (0-23):
     todProfile_p[h] = mean(plan_mwh_t) for alle t med time = h
   normalized_profile_p = todProfile_p / mean(todProfile_p)
   // Vektor av lengde 24, summerer til 24
```

**Outlier-deteksjon:**

For hver metrikk og hver anleggsgruppe G:

```
For metrikk M og gruppe G:
  values = [M_p for p in G]
  median_M_G = median(values)
  mad_M_G = median(|M_p - median_M_G|)  // Median Absolute Deviation
  
  for hvert anlegg p i G:
    z_robust = 0.6745 × (M_p - median_M_G) / mad_M_G
    if |z_robust| > 3.0:
      flag p som outlier på metrikk M
```

MAD-basert z-score brukes i stedet for vanlig z-score fordi det er robust mot at outlier-en selv påvirker estimatet. Terskel 3.0 er konservativ — fanger reelle anomalier uten å bli støy.

For time-of-day-profile (modul 5e): bruk **euklidsk avstand** mellom anleggets profil og gruppe-medianprofil. Hvis avstanden er > 2× stddev av peer-avstandene, flagges anlegget.

**Multi-metrikk-aggregering:**

```
outlier_score_p = COUNT(metrikker der anlegget er flagget) / TOTAL_METRIKKER (=5)
```

Anlegg med outlier-score ≥ 0.4 (flagget på minst 2 av 5 metrikker) rapporteres som "krever review". Anlegg med score = 1.0 er sterkt mistenkelige og bør eskaleres til Hydrogrid umiddelbart.

**Datakilde:**

Modulen krever at det finnes plan-data for **alle** anlegg i en gruppe samtidig — uten det er sammenligningen meningsløs. Hvis et anlegg mangler data for perioden, ekskluderes det fra outlier-beregningen for den perioden (med tydelig flag i UI).

**API-respons (forenklet):**

```json
{
  "period": "2026-04-01 to 2026-04-28",
  "groups": [
    {
      "groupId": "magasin_regulert",
      "members": ["drivdal", "lindland"],
      "metrics": {
        "utilization": { "median": 0.31, "mad": 0.04 },
        "sensitivity": { "median": 0.42, "mad": 0.08 },
        "wv_ratio": { "median": 0.95, "mad": 0.06 },
        "inflow_consumption": { "median": 0.98, "mad": 0.05 }
      },
      "outliers": [
        {
          "plantId": "drivdal",
          "metric": "utilization",
          "value": 0.52,
          "robustZ": 3.4,
          "diagnosis": "Drivdal har 52 % kapasitets-utnyttelse vs gruppe-median 31 %. 
                        Mulig: feil installert kapasitet i Hydrogrid (sjekk om de bruker 2.2 MW eller 2.7 MW), 
                        eller feil vannverdi som gir over-produksjon."
        }
      ]
    },
    ...
  ],
  "summaryFlags": [
    { "plantId": "drivdal", "outlierScore": 0.4, "severity": "investigate" }
  ]
}
```

**Schedulering:** kjør analysen daglig kl 06:00 (etter at gårsdagens plan-data er stabilisert) på rullende 7-dagers vindu. Lagre resultatet i ny tabell `core.hydrogrid_outlier_reports` for historisk trending — vi vil se om samme anlegg flagges over tid (kronisk bug) eller bare sporadisk (forventet variasjon).

## API-endepunkter

**Fil:** `src/KraftverkUptime.Api/Endpoints/HydrogridEndpoints.cs` NY

```
GET /api/v1/plants/{plantId}/hydrogrid/forecast-vs-actual?from=&to=
GET /api/v1/plants/{plantId}/hydrogrid/attribution?from=&to=
GET /api/v1/plants/{plantId}/hydrogrid/water-value?from=&to=
GET /api/v1/plants/{plantId}/hydrogrid/stability?from=&to=
GET /api/v1/plants/{plantId}/hydrogrid/status
GET /api/v1/hydrogrid/cross-plant-outliers?from=&to=         (alle anlegg, alle grupper)
GET /api/v1/hydrogrid/cross-plant-outliers/history?plantId=  (historikk for ett anlegg)
POST /api/v1/admin/hydrogrid/sync-now                        (manuell trigger sync-job)
POST /api/v1/admin/hydrogrid/run-outlier-analysis            (manuell trigger outlier-job)
POST /api/v1/admin/hydrogrid/backfill?plantId=&from=&to=
```

## UI

### Ny side `/hydrogrid/{plantId}`

**Fil:** `src/KraftverkUptime.Web/Pages/Hydrogrid.razor` NY

Layout:

1. **Status-banner** øverst: "Siste plan generert HH:MM (X timer siden). Neste forventet HH:MM."
2. **KPI-rad:** Forecast RMSE (NOK/MWh), Forecast bias, Plan-stabilitet (%), Andel timer med constraint aktiv
3. **Forecast-vs-faktisk-graf** (ApexChart linjegraf):
   - Linje 1: Hydrogrids spot-forecast
   - Linje 2: Faktisk spot
   - Linje 3: Plan (sekundær akse)
   - Linje 4: Faktisk Elhub (sekundær akse)
4. **Attribusjons-tabell** for timer med signifikant avvik. Sortbar på diagnose. Klikk på rad → expanderer card med full rasjonale.
5. **Vannverdi-graf:** vannverdi over tid sammen med magasinstand på sekundær akse.
6. **Plan-stabilitet-heatmap:** dag på x-akse, time på y-akse, fargekodet på antall versjoner.

### Integrasjon med Produksjon-siden

På eksisterende `/produksjon/{plantId}`-side: legg til knapp "Diagnose Hydrogrid" som åpner `/hydrogrid/{plantId}` med samme periode pre-utfylt. Direkte vei fra "planen bommet" til "her er hvorfor".

### Status-widget på dashboard

Liten widget på `/portefolje` som viser Hydrogrid-helse for alle anlegg:

```
Drivdal     ●  Plan oppdatert 14:03 — RMSE 187 NOK/MWh
Lindland    ●  Plan oppdatert 14:03 — RMSE 142 NOK/MWh
Haukland    ●  Plan oppdatert 13:45 — RMSE 220 NOK/MWh
Logjen      ⚠  Plan oppdatert 09:12 — 5 timer gammel
Vikeså      ✕  Sync-feil siden 12:30 — sjekk auth-token
```

### Ny side `/hydrogrid/outliers` — kryss-anleggs-analyse

**Fil:** `src/KraftverkUptime.Web/Pages/HydrogridOutliers.razor` NY

Layout:

1. **Periode-velger** og gruppe-filter øverst
2. **Sammendrags-banner:** "X av 11 anlegg er flagget som outlier i denne perioden"
3. **Per-gruppe-seksjon** (én per `comparison_group`):
   - Tabell: anlegg som rader, metrikker som kolonner. Hver celle viser anleggets verdi + differanse fra gruppe-median + fargekoding (grønn = innenfor MAD, gul = z 2-3, rød = z > 3)
   - Klikk på rød celle → expander med diagnose-tekst og lenker til relevante andre moduler ("se forecast-vs-faktisk for samme periode")
4. **Historisk trend-graf:** for hvert flagget anlegg, vis robust z-score over tid (siste 90 dager). Anlegg som har konsistent høy z-score over tid er sterk-mistanke for kronisk modellfeil.
5. **Eksport-knapp:** "Last ned outlier-rapport som PDF" — sendes til Hydrogrid-kontaktperson for å diskutere modell-justering.

På Produksjon-siden og hovedsidene: legg til varsel-banner hvis dette anlegget er flagget i siste outlier-rapport, med lenke til full analyse.

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 25 nye tester passerer
2. Migreringer kjører idempotent og baklengs-kompatibelt med eksisterende data
3. `HydrogridSyncJob` puller plan + forecast + vannverdi for Drivdal hver time, lagrer i `hydrogrid_snapshots` og `hydrogrid_plan_hours`
4. `GET /api/v1/plants/drivdal/hydrogrid/forecast-vs-actual?from=2026-02-01&to=2026-03-01` returnerer beregnet RMSE, MAE, bias, og time-for-time data
5. `GET /api/v1/plants/drivdal/hydrogrid/attribution?from=2026-02-01&to=2026-03-01` returnerer attribusjons-card for alle timer der `|Plan − Elhub| > 0.5 MW`
6. `/hydrogrid/drivdal`-siden rendrer alle widgets uten feil for feb-2026

### Datakvalitet

7. Hvis Hydrogrid API er nede: sync-job logger feil og fortsetter — ingen kaskade-feil i UI. Eksisterende snapshots blir brukt.
8. Hvis et anlegg ikke er konfigurert i `Hydrogrid.Plants`-mapping: returner 404 fra hydrogrid-endpointene med tydelig melding "anlegget er ikke aktivert for Hydrogrid-API"
9. Plan-versjoner som overlapper i tid (samme plant, samme time, ulik version_id): UI viser siste gyldige versjon før timen begynte. Tidligere versjoner finnes for stabilitet-analyse.
10. Token-fornyelse fungerer minst 24 timer før manuell intervensjon kreves

### Test-dekning

11. `HydrogridApiClientTests` — 8 tester med mock HttpClient
    - Happy path
    - 401 → token refresh
    - 429 → retry med backoff
    - 5xx → retry til den lykkes
    - Tom respons → tom liste, ingen exception
    - Malformed JSON → loggret error, returner empty
    - Timeout → respekter cancellation token
    - OAuth token cache fungerer (én token-call per N forespørsler)

12. `HydrogridDiagnosticsServiceTests` — 12 tester
    - Forecast-vs-faktisk: kjent inputs → kjente RMSE/MAE/bias
    - Attribusjon: hver av de 5 diagnose-typene har en test
    - Vannverdi-tracker: korrelasjon mellom vannverdi og magasinstand
    - Plan-stabilitet: 1 versjon → stabilitet 1.0
    - Plan-stabilitet: 5 versjoner med høyt sprik → stabilitet < 0.5

13. `HydrogridSyncJobTests` — 5 tester
    - Idempotent (kjør to ganger → ingen duplikater)
    - Plant-error: én feiler, andre fortsetter
    - UPSERT-strategi for nye/oppdaterte snapshots
    - Trigger event ved fullført sync
    - Backfill respekterer Hydrogrids historikk-grense (sjekkes mot API-spec)

14. End-to-end: kjør sync for Drivdal, verifiser at minst én snapshot lagres, kall forecast-vs-actual-endepunktet og verifiser respons-struktur

15. `CrossPlantOutlierAnalyzerTests` — 10 tester
    - To anlegg i samme gruppe, lik plan → ingen outlier
    - Tre anlegg, ett med 3× kapasitets-utnyttelse → flagget på `utilization` med høy z-score
    - Anlegg uten data ekskluderes uten å feile beregningen for andre
    - MAD = 0 (alle peer-verdier identiske) → håndteres uten division-by-zero, returner z = 0
    - Time-of-day-profil-test: anlegg med invertert profil (produserer på natt vs dag) → flagget på profil-avstand
    - Persistens: outlier-rapport lagres med korrekt period-vindu og kan hentes via API
    - Historikk: samme anlegg flagget tre uker på rad → vises som "kronisk" i trend-graf
    - Multi-metrikk-aggregering: anlegg flagget på 2/5 metrikker → outlierScore = 0.4
    - Diagnose-tekst genereres kun for outliers, ikke for hver eneste sammenligning
    - Edge case: gruppe med kun 1 medlem → metrikken hoppes over (ingen peers å sammenligne mot)

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Hent Hydrogrid API-spec fra developer-portalen + bekreft endpoints | (utenfor scope) | Bruker leverer spec til Claude Code |
| 1 | Datamodell: `hydrogrid_snapshots` + `hydrogrid_plan_hours` + migreringer | 2-3 t | – |
| 2 | `IHydrogridApiClient` + implementasjon + auth + retry | 4-6 t | – |
| 3 | `HydrogridSyncJob` + cron-registrering + on-demand-trigger | 3 t | – |
| 4 | Test-sync mot Drivdal — verifiser at data faktisk lagres | 1 t | **Stopp og rapporter første snapshot-respons** |
| 5 | `IHydrogridDiagnosticsService` + 4 metoder + DB-queries | 6-8 t | – |
| 6 | API-endepunkter + DTO-er | 2-3 t | – |
| 7 | Forecast-vs-faktisk-graf + KPI-rad i UI | 3-4 t | – |
| 8 | Attribusjons-tabell med expanderende cards | 2-3 t | – |
| 9 | Vannverdi-graf + plan-stabilitet-heatmap | 3-4 t | – |
| 10 | Status-widget på portefølje + knapp på Produksjon-siden | 1-2 t | – |
| 11 | End-to-end-test for Drivdal feb-2026: åpne /hydrogrid/drivdal og verifiser at alle widgets viser meningsfulle tall | 1 t | – |
| 12 | Modul 5: kryss-anleggs-outlier — `comparison_group`-felt + backfill, `hydrogrid_outlier_reports`-tabell, daglig job, API-endepunkter, /hydrogrid/outliers-side | 8-10 t | **Stopp etter første outlier-rapport — verifiser at flaggede anlegg gir mening drifsmessig** |

**Estimat totalt:** 6-8 dager etter at API-spec er hentet og auth fungerer.

## Antakelser som må verifiseres mot Hydrogrid API-spec

1. **REST/JSON over HTTPS** med OAuth2 client credentials — sannsynlig basert på "secure data exchange" og B2B-kontekst, men kan være API-key
2. **Endepunkter for plan-historikk eksisterer** — Hydrogrid skriver "turbine schedules" + "water values" i materialet, men vi vet ikke om de eksponerer **historiske versjoner** eller bare gjeldende plan
3. **Tilsigsprognose er per time** og inkludert i samme respons som planen (mest sannsynlig — den er en optimaliserings-input)
4. **Vannverdi er per time** i NOK/MWh (kan være EUR/MWh — sjekk konvertering)
5. **Constraint-info er strukturert** — kan være enum-basert (vår modell antar) eller fri tekst (krever parsing)
6. **Plant-id-mapping** kreves — vår `drivdal` ↔ Hydrogrids interne UUID

Når API-spec er på plass: oppdater dette dokumentet (spesielt JSON-skjema og endpoint-paths) **før** kode skrives. Spec'en skal alltid speile faktisk API.

## Ut-av-scope for v1

- Skriv-tilbake til Hydrogrid (manuell plan-overstyring fra UI) — ren read-only i v1
- Forecast-prognose for andre prisområder (Hydrogrid eksponerer trolig kun for sitt anlegg, ikke generelt)
- Maskinlærings-modell på toppen av Hydrogrid-data (f.eks. "vår vurdering av Hydrogrids prognose-pålitelighet")
- Real-time push-integrasjon (webhook-mottak fra Hydrogrid) — polling holder for v1
- Multi-tenant (flere selskaper med ulike Hydrogrid-kontoer)

## Verifikasjon

```powershell
# 1. Migrering
cd C:\Morten\00 Oppetid\src\KraftverkUptime.Api
dotnet ef database update

# 2. Kjør sync-job manuelt
curl.exe -X POST "http://localhost:5080/api/v1/admin/hydrogrid/sync-now"

# 3. Verifiser snapshots
psql -d kraftverkuptime -c "
SELECT plant_id, COUNT(*) AS snapshots, MAX(generated_at_utc) AS siste
FROM core.hydrogrid_snapshots GROUP BY plant_id;
"

# 4. Diagnostikk for Drivdal feb-2026
curl.exe "http://localhost:5080/api/v1/plants/drivdal/hydrogrid/forecast-vs-actual?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json

# 5. UI-verifikasjon
# http://localhost:5180/hydrogrid/drivdal
# Forventet: status-banner, fire KPI-er, forecast-vs-faktisk-graf med begge linjer,
# attribusjons-tabell med rader for timer der plan ≠ faktisk
```

## Referanser

- HYDROGRID Insight implementation-side: https://www.hydrogrid.ai/implementation
- Drivdal feb-2026 audit: `SPEC-PRODUKSJON-FIX.md` + Cowork-samtale 2026-04-30
- Eksisterende ProduksjonAnalyseCalculator: `src/KraftverkUptime.Modules.Reporting/Produksjon/ProduksjonAnalyseCalculator.cs`
- Settlement-modul (referanse-mønster for periodisk import): `src/KraftverkUptime.Modules.Settlement/`
