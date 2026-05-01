# Spec: Hydrogrid plan — kross-anlegg-sammenligning og outlier-deteksjon

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 2-3 dager
**Avhengighet:** `SPEC-HYDROGRID-API.md` må være implementert (krever `core.hydrogrid_plan_hours`-data for flere anlegg)
**Bygger på:** Drifts-leders observasjon at konsistente outlier-anlegg i Hydrogrid-planene ofte indikerer bug i optimaliseringen

## Bakgrunn

Alle 11 anleggene ligger i samme prisområde (NO2). Selv om de har ulik magasin-kapasitet og topologi, *bør* deres Hydrogrid-planer **korrelere** — alle bør planlegge mest produksjon i samme høypris-timer. Hvis ett anlegg konsistent planlegges veldig forskjellig fra de andre — for eksempel høye produksjonsvolumer i timer der alle andre står stille — er det et tegn på at noe i Hydrogrids modell for nettopp det anlegget har gått galt:

- Feil-konfigurert vannverdi-funksjon
- Fastlåste constraints som ikke reflekterer virkeligheten
- Bug i topologi-modellen (kaskade-feil)
- Feil tilsigsprognose for det spesifikke anlegget
- Magasinstand-initialisering mot feil verdi

Per-anlegg-diagnostikken (forecast-vs-faktisk, attribusjon) finner *time-spesifikke* feil. Denne modulen finner *systematiske* avvik som kun blir synlig når man sammenligner på tvers.

**Drifts-leders intuisjon (2026-04-30):** *"Hvis et av anleggene ofte planlegges for høye produksjonsvolumer forskjellig fra de andre, indikerer det ofte at det er noe bug i beregningene til Hydrogrid."*

## Beslutning

Bygg en kross-anlegg-sammenligning som beregner statistiske avvik mellom anleggs-planer, viser dem visuelt, og kjører periodisk anomali-deteksjon med varsling.

| Komponent | Hva |
|---|---|
| **Beregning** | Normaliser plan på installert effekt, beregn per-time z-score mot portefølje-snittet, aggregér til outlier-rate per anlegg |
| **UI** | Heatmap av plan-fraksjoner, statistikk-tabell, outlier-detalj-tabell på `/hydrogrid/sammenligning` |
| **Job** | Ukentlig anomali-detector som flagger anlegg med vedvarende outlier-mønster |
| **Varsling** | UI-banner + valgfri e-post/Slack til drifts-leder ved utløst alert |

Ingen ny database-tabell trengs — bygger på `core.hydrogrid_plan_hours` fra Hydrogrid-API-spec'en.

## Definisjoner

### Plan-fraksjon

```
plan_fraksjon(plant, t) = plan_mwh(plant, t) / installed_capacity_mw(plant)
```

Verdi i [0, 1]. Normaliserer for anlegg-størrelse — gjør Drivdal (2,2 MW) sammenlignbar med Øgreyfoss (større installert effekt).

### Portefølje-fraksjon

```
portefolje_fraksjon(t) = mean(plan_fraksjon(p, t))  for p i alle aktive anlegg
portefolje_std(t)      = std(plan_fraksjon(p, t))   for p i alle aktive anlegg
```

### Per-time z-score

```
z(plant, t) = (plan_fraksjon(plant, t) − portefolje_fraksjon(t)) / portefolje_std(t)
```

Verdi tolkes som "antall standardavvik fra porteføljes snitt for denne timen". `|z| > 2` = uvanlig avvik. `|z| > 3` = ekstrem outlier.

**Robusthet mot små porteføljer:** med 11 anlegg er parametrisk z-score følsom for ekstrem-verdier. Bruk **Modified Z-score** med median og MAD (Median Absolute Deviation) i tillegg:

```
mz(plant, t) = 0.6745 × (plan_fraksjon(plant, t) − median_fraksjon(t)) / MAD(t)
```

Vis begge i UI; bruk MAD-baserte for alert-trigging fordi den er mer robust.

### Outlier-rate per anlegg

```
outlier_rate(plant, periode) = COUNT(timer der |mz(plant, t)| > 2) / COUNT(alle timer)
```

For en sunn portefølje forventer vi `outlier_rate ≈ 5 %` (statistisk normal-fordeling). Anlegg med `outlier_rate > 15 %` over en hel uke = konsistent outlier = mistenkt bug.

### Korrelasjons-matrise

For hvert par av anlegg `(p_i, p_j)`:

```
corr(p_i, p_j) = pearson_corr(plan_fraksjon(p_i, t), plan_fraksjon(p_j, t))
                  for t i periode
```

Verdier nær 1 = anleggene planlegges i takt. Verdier nær 0 eller negative = uavhengige eller motstridende planer. Et anlegg som har konsistent lav korrelasjon med alle andre er mistenkt.

### Anlegg-konsistens-score

```
konsistens(plant) = mean(corr(plant, andre)) for andre i alle aktive anlegg
```

Vises som ett tall i [-1, 1] per anlegg. Verdier under 0.3 over en lengre periode = trolig isolert plan-mønster.

## Datamodell

Ingen nye tabeller. Tre views for query-effektivitet:

```sql
CREATE VIEW core.hydrogrid_latest_plans AS
SELECT DISTINCT ON (plant_id, time_utc)
    plant_id, time_utc, plan_mwh, plan_version_id, generated_at_utc
FROM core.hydrogrid_plan_hours
WHERE generated_at_utc < time_utc                      -- gyldig plan før timen begynte
ORDER BY plant_id, time_utc, generated_at_utc DESC;

CREATE VIEW core.hydrogrid_plan_fractions AS
SELECT
    h.plant_id, h.time_utc,
    h.plan_mwh / NULLIF(p.installed_capacity_mw, 0) AS plan_fraksjon,
    h.plan_mwh,
    p.installed_capacity_mw
FROM core.hydrogrid_latest_plans h
JOIN core.plants p ON p.plant_id = h.plant_id;

-- For per-time z-score: aggregér over portefølje
CREATE VIEW core.hydrogrid_portfolio_stats AS
SELECT
    time_utc,
    AVG(plan_fraksjon) AS portfolio_mean,
    STDDEV_SAMP(plan_fraksjon) AS portfolio_std,
    PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY plan_fraksjon) AS portfolio_median,
    COUNT(*) AS antall_aktive_plants
FROM core.hydrogrid_plan_fractions
GROUP BY time_utc;
```

MAD beregnes i kode (PostgreSQL har ikke direkte støtte) — kan eventuelt implementeres som lagret prosedyre senere hvis ytelse blir et problem.

## Endringer i kodebasen

### 1. `IPortfolioComparisonService`

**Fil:** `src/KraftverkUptime.Modules.Reporting/Hydrogrid/IPortfolioComparisonService.cs` NY

```csharp
public interface IPortfolioComparisonService
{
    /// <summary>
    /// Plan-fraksjoner per anlegg per time + portefølje-statistikk.
    /// Brukes til heatmap-rendering.
    /// </summary>
    Task<PlanFractionMatrix> GetPlanFractionsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Per-anlegg outlier-rate over perioden, sortert deskenderende.
    /// Inkluderer både z-score og MAD-basert mz.
    /// </summary>
    Task<IReadOnlyList<PlantOutlierStat>> GetOutlierRatesAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        double zThreshold = 2.0, CancellationToken ct = default);

    /// <summary>
    /// Korrelasjons-matrise mellom alle anlegg i porteføljen.
    /// </summary>
    Task<PlantCorrelationMatrix> GetCorrelationMatrixAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Detaljliste over de N mest ekstreme outlier-timene i perioden.
    /// </summary>
    Task<IReadOnlyList<OutlierEvent>> GetTopOutlierEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int topN = 20, CancellationToken ct = default);
}

public sealed record PlanFractionMatrix(
    IReadOnlyList<DateTimeOffset> Times,
    IReadOnlyDictionary<string, IReadOnlyList<double?>> FractionsByPlant,
    IReadOnlyList<double> PortfolioMean,
    IReadOnlyList<double> PortfolioMedian);

public sealed record PlantOutlierStat(
    string PlantId,
    string Name,
    double OutlierRate,         // andel timer med |mz| > zThreshold
    double MeanZScore,
    double MeanModifiedZScore,
    double Consistency,         // mean korrelasjon med øvrige anlegg
    int TimerOver2Std,
    int TimerOver3Std,
    string Verdict);            // "OK", "OBSERVASJON", "ALERT"

public sealed record OutlierEvent(
    DateTimeOffset TimeUtc,
    string PlantId,
    double PlanFraksjon,
    double PortfolioMedian,
    double ModifiedZScore,
    double SpotForecastNokMwh,
    double WaterValueNokMwh,
    string? BindingConstraint,
    string Forklaring);

public sealed record PlantCorrelationMatrix(
    IReadOnlyList<string> PlantIds,
    double[,] Matrix);            // PlantIds.Length × PlantIds.Length, [i,j] = corr(p_i, p_j)
```

### 2. Implementasjon

**Fil:** `src/KraftverkUptime.Infrastructure/Hydrogrid/PortfolioComparisonService.cs` NY

- `GetPlanFractionsAsync`: SQL mot `hydrogrid_plan_fractions` + `hydrogrid_portfolio_stats`
- `GetOutlierRatesAsync`:
  1. Hent alle plan-fraksjoner for perioden
  2. Per time: beregn median + MAD i kode (LINQ)
  3. Per plant: beregn outlier-rate, mean z, mean mz
  4. Verdict-regel:
     - `OK` hvis `outlier_rate < 8 %`
     - `OBSERVASJON` hvis `8-15 %`
     - `ALERT` hvis `> 15 %` over hele perioden
- `GetCorrelationMatrixAsync`: enkel Pearson via Math.NET eller manuell impl
- `GetTopOutlierEventsAsync`: sorter etter `|mz|` desc, ta topp N. Hver event berikes med spot-forecast og vannverdi fra `hydrogrid_plan_hours`

### 3. API-endepunkter

**Fil:** `src/KraftverkUptime.Api/Endpoints/HydrogridComparisonEndpoints.cs` NY

```
GET /api/v1/hydrogrid/comparison/plan-fractions?from=&to=
GET /api/v1/hydrogrid/comparison/outlier-rates?from=&to=&threshold=
GET /api/v1/hydrogrid/comparison/correlation?from=&to=
GET /api/v1/hydrogrid/comparison/top-outliers?from=&to=&top=
```

### 4. UI-side `/hydrogrid/sammenligning`

**Fil:** `src/KraftverkUptime.Web/Pages/HydrogridSammenligning.razor` NY

Layout:

1. **Periode-velger** øverst (default: siste 7 dager)
2. **Heatmap** — y-akse: anlegg, x-akse: time, fargekoding: plan-fraksjon (0-100 %). Røde celler markerer outliers (|mz| > 2). Klikk på celle → expanderer detalj.
3. **Outlier-tabell** sortert på outlier-rate desc:

   | Anlegg | Outlier-rate | Konsistens | Mean mz | Verdict |
   |---|---:|---:|---:|---|
   | Honnefoss | 24 % | 0.18 | 1.7 | 🔴 ALERT |
   | Liavatn | 11 % | 0.42 | 1.1 | 🟡 OBSERVASJON |
   | Drivdal | 5 % | 0.71 | 0.3 | 🟢 OK |
   | ... | | | | |

4. **Top-outliers-detalj** — tabell med 20 enkelt-timer som er mest ekstreme. Hver rad inkluderer Hydrogrids forklaring (forecast, vannverdi, constraint).
5. **Korrelasjons-heatmap** mellom alle anleggspar. Lave verdier (mørkerødt) = isolerte plan-mønstre.

### 5. Anomali-deteksjons-job

**Fil:** `src/KraftverkUptime.Infrastructure/Hydrogrid/PortfolioAnomalyJob.cs` NY

Cron: hver mandag kl 07:00.

```csharp
public sealed class PortfolioAnomalyJob : IJobHandler<PortfolioAnomalyRequest>
{
    public async Task ExecuteAsync(PortfolioAnomalyRequest req, CancellationToken ct)
    {
        var stats = await _comparisonService.GetOutlierRatesAsync(
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, 2.0, ct);

        var alerts = stats.Where(s => s.Verdict == "ALERT").ToList();
        if (alerts.Count == 0) return;

        var report = new AnomalyAlertReport
        {
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-7),
            PeriodEnd = DateTimeOffset.UtcNow,
            Plants = alerts,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        // 1. Lagre rapport i hydrogrid_anomaly_reports for UI-banner
        // 2. Send INotificationService.SendAsync — settes opp via plant config
    }
}
```

Ny tabell:

```sql
CREATE TABLE core.hydrogrid_anomaly_reports (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    generated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    payload JSONB NOT NULL,             -- liste av PlantOutlierStat med verdict ALERT
    acknowledged_by VARCHAR(64),
    acknowledged_at_utc TIMESTAMPTZ
);
```

### 6. UI-banner

På `/portefolje`-dashbordet: hvis det finnes uakknowleget alert siste 7 dager, vis et oransje banner:

> ⚠ **Hydrogrid plan-anomali detektert** — Honnefoss (24 % outlier-rate) og Liavatn (15 %) avvikte vesentlig fra portefølje-mønsteret siste uke. [Se detaljer →]

Klikk → åpner `/hydrogrid/sammenligning` med uka pre-utfylt og knapp "Bekreft og lukk varsel".

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt
2. Views opprettes via migrering — query-tid på `hydrogrid_portfolio_stats` for én ukes data < 500 ms
3. `GET /api/v1/hydrogrid/comparison/outlier-rates?from=...&to=...` returnerer 11 rader (én per anlegg) med outlier-rate
4. `/hydrogrid/sammenligning`-siden rendrer heatmap + 3 tabeller + korrelasjons-matrise uten feil
5. Anomali-job kjører og populerer `hydrogrid_anomaly_reports`-tabellen ukentlig
6. Banner på portefølje-dashboard vises når det finnes uakknowleget alert
7. "Bekreft og lukk varsel"-knapp skriver `acknowledged_by` + `acknowledged_at_utc` og fjerner banneret

### Datakvalitet

8. Anlegg uten Hydrogrid-data i perioden ekskluderes fra portefølje-snittet (vises som tom rad i heatmap, ikke nullverdi)
9. Hvis `installed_capacity_mw = 0` (ikke satt for et anlegg): plant ekskluderes fra plan-fraksjon-beregning + UI-warning
10. Med færre enn 4 aktive anlegg i perioden: deaktiver MAD-baserte beregninger (utilstrekkelig data) og vis "for få anlegg for portefølje-analyse"-melding
11. NaN-verdier fra std=0 (alle anlegg planla likt) håndteres som z=0, ikke exception

### Test-dekning

12. `PortfolioComparisonServiceTests` — 12 tester
    - Happy path med 11 anlegg, jevn fordeling → outlier-rate ≈ 5 % per anlegg
    - Ett anlegg med konsistent høye verdier → outlier-rate > 50 %, verdict ALERT
    - To anlegg med motsatt mønster → corr ≈ −1
    - Mindre enn 4 anlegg → tilbakefaller til z-score uten MAD
    - Tom periode → tomme lister, ingen exception
    - Periode med kun ett anlegg → ingen comparison mulig, tydelig melding
    - Konsistens-score for anlegg med corr 0.7 til alle = 0.7
    - Verdict-grenser (8 %, 15 %)
    - MAD = 0 (alle planer like) → mz = 0 for alle
    - Anlegg uten installed_capacity → ekskluderes
    - Top-outlier-event sortering på |mz|
    - End-to-end: gen kunstig data hvor 1 anlegg er buggy → job genererer alert

13. `PortfolioAnomalyJobTests` — 4 tester
    - Ingen alerts → ingen rapport lagret
    - Én alert → rapport lagret + notifikasjon sendt
    - Flere alerts → alle inkludert i rapport
    - Idempotent — kjør to ganger samme uke → andre kjøring skiper hvis allerede lagret

14. End-to-end manuell test:
    - Importer Hydrogrid-data for alle 11 anlegg for én uke
    - Åpne `/hydrogrid/sammenligning?from=...&to=...`
    - Verifiser at heatmap viser 11 rader, korrelasjons-matrise er symmetrisk, top-outliers stemmer mot manuelt utregnet z-score for et par cases

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | DB-views opprettes via migrering | 1 t |
| 2 | `IPortfolioComparisonService` + 4 metoder + 12 tester | 6-8 t |
| 3 | API-endepunkter + DTO-er | 2 t |
| 4 | `PortfolioAnomalyJob` + tabell + 4 tester | 3-4 t |
| 5 | UI-side `/hydrogrid/sammenligning` med heatmap + tabeller | 4-6 t |
| 6 | Banner på portefølje-dashboard + acknowledge-flow | 2 t |
| 7 | End-to-end-test mot ekte data | 1 t |

**Estimat totalt:** 2-3 dager.

## Antakelser

1. **Alle 11 anlegg har `installed_capacity_mw` satt korrekt.** Hvis ikke, kan plan-fraksjoner ikke beregnes. Verifiseres som første steg.
2. **Hydrogrid-API leverer plan for alle anlegg ved samme kadens.** Hvis ett anlegg har lengre intervaller mellom planene blir det "naturlig" outlier i timer mellom oppdateringene. Denne specen håndterer det ikke spesielt — kan adresseres i v2 hvis det viser seg nødvendig.
3. **MAD ≥ 0.01** for å unngå division-by-zero. Hvis 8 av 11 anlegg planlegger akkurat samme verdi (svært usannsynlig i praksis), faller vi tilbake til parametrisk z-score.
4. **NO2-prisområde-antakelse** — alle anlegg eksponert for samme markedsvilkår. Hvis fremtidige anlegg legges til i andre prisområder må sammenligningen segmenteres på prisområde.

## Ut-av-scope

- Sammenligning av faktisk produksjon (Elhub) på tvers — bare Hydrogrid-plan i denne modulen. Faktisk-sammenligning kan bygges på samme mal i en oppfølging.
- Auto-feilsøking ("denne anomalien skyldes X i Hydrogrid") — vi flagger, drift-leder undersøker.
- Cross-portefølje-sammenligning (mot andre kraftselskap) — krever data vi ikke har.
- Sesong-justert outlier-deteksjon (våtsesong vs tørrsesong) — fixed terskler i v1, kan refines.

## Verifikasjon

```powershell
# 1. Verifiser views
psql -d kraftverkuptime -c "SELECT COUNT(*) FROM core.hydrogrid_portfolio_stats WHERE time_utc > NOW() - INTERVAL '7 days';"

# 2. Hent outlier-rate for siste uke
curl.exe "http://localhost:5080/api/v1/hydrogrid/comparison/outlier-rates?from=2026-04-23T00:00:00Z&to=2026-04-30T00:00:00Z" | ConvertFrom-Json | ConvertTo-Json -Depth 3

# 3. Trigger anomali-job manuelt
curl.exe -X POST "http://localhost:5080/api/v1/admin/hydrogrid/anomaly-check"

# 4. UI
# http://localhost:5180/hydrogrid/sammenligning
# Verifiser at heatmap rendrer 11 anlegg og at outlier-tabellen sorterer riktig
```

## Driftsmessig tolkning

Når et anlegg får verdict `ALERT`, drift-leder bør sjekke:

1. **Vannverdi-tracker** for samme anlegg (`/hydrogrid/{plantId}` → Vannverdi-graf): har vannverdien vært unormalt lav eller høy?
2. **Magasinstand** vs Hydrogrids forventning: har modellen feil utgangsstand?
3. **Constraint-aktiveringer** i attribusjons-tabellen: er det en constraint som er konstant aktiv?
4. **Plan-stabilitet** for samme anlegg: oscillerer planen mer enn for andre?
5. Hvis ingenting åpenbart: kontakt Hydrogrid Plant Success Manager med utskrift fra denne siden — de kan sjekke modellen i deres ende.

## Referanser

- Drifts-leders observasjon (Cowork-samtale 2026-04-30)
- `SPEC-HYDROGRID-API.md` (datagrunnlag — `core.hydrogrid_plan_hours`)
- Statistikk-grunnlag: Modified Z-score (Iglewicz & Hoaglin, 1993)
- MAD: median absolute deviation, robust mot outliers vs standard avvik
