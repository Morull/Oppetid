# Spec: Capture Rate KPI

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 1-2 dager (kjernemodul + UI)
**Avhengighet:** Settlement-modulen (eksisterer), Portefølje-dashboard (eksisterer)
**Fasit:** `Capture Rate KPI.xlsx` (uploads) — pivot-tall for 2025 brukes som regresjonstest

## Bakgrunn

Drifts-leder Dalane Kraft har en eksisterende Excel-modell som beregner capture rate per anlegg per år. Modellen brukes som rapport-grunnlag og er etablert konvensjon. Vi skal flytte logikken inn i Oppetid-platformen slik at:

1. KPI-en oppdateres automatisk etter hver settlement-import
2. Den eksponeres i portefølje-dashboardet for sammenligning på tvers av anlegg
3. Per-anlegg detaljside gir innsikt i hvordan rate-en utvikler seg over tid og distribuerer per dag/time
4. Eksisterende Excel-tall i `Pivot`-arket fungerer som regresjons-fasit ved første implementasjon

Excel-modellen bruker **daglig** capture rate med 5/95-persentilfilter. Dette er konservativt og robust mot outlier-dager, men taper presisjon for magasinregulering innenfor døgnet (intra-day-effekt). Settlement-modulen vår har **timesoppløsning**, så vi kan også beregne en **times-volumvektet** variant som er bransje-standard for asset-rapportering (Statkraft, Statnett, NVE).

## Beslutning

**Begge varianter implementeres:**

| Variant | Formel | Brukes til |
|---|---|---|
| **Dag-CR** (Excel-replika) | Dag-CR = volumvektet snitt av (dag-pris/spot-dag) over 5–95-persentilfiltrert dager | Regresjonstest mot Excel-fasit; rapportering til drifts-leder som kjenner tallet |
| **Times-CR** (volumvektet) | Σ(MWh_t × spot_t) / (Σ(MWh_t) × Σ(spot_t)/N_t) | Hovedversjon i UI; fanger intra-dag-magasinregulering |

Begge eksponeres som separate KPI-felt. Avviket mellom dem indikerer hvor mye intra-dag-regulering bidrar — pedagogisk verdi for drifts-leder.

## Definisjoner

### Felles input

For et gitt anlegg P og periode [from, to):
- `H_t` = settet av timer i perioden der både `MwhElhub > 0` og `SpotprisNokMwh ≠ null`
- `H_all` = settet av alle timer i perioden uavhengig av produksjon (forutsetter at settlement-fila har komplette timer)
- `mwh_t` = `MwhElhub` for time t
- `nok_t` = `SpotomsetningNok` for time t (= `mwh_t × spot_t` i avregningen)
- `spot_t` = `SpotprisNokMwh` for time t

### Capture price (NOK/MWh) — felles for begge varianter

```
capture_price = Σ_t∈H_t (nok_t) / Σ_t∈H_t (mwh_t)
```

Volumvektet snittpris anlegget faktisk fikk for sin produksjon. Direkte sammenlignbar mellom anlegg, beveger seg med markedsnivå.

### Times-CR (volumvektet) — hovedversjon

```
times_baseline = Σ_t∈H_all (spot_t) / |H_all|
times_cr      = capture_price / times_baseline
```

Forholdstall mellom oppnådd pris og tidsvektet markedssnitt for hele perioden. >1 = produksjonen falt i høypristimer.

### Dag-CR (Excel-replika)

For hver dag d i perioden:
- `mwh_d` = `Σ_{t∈d} mwh_t`
- `nok_d` = `Σ_{t∈d} nok_t`
- `spot_d` = aritmetisk dag-snitt av `spot_t` over alle 24 timer i d (uavhengig av produksjon)
- `oppnådd_d` = `nok_d / mwh_d` (kun for dager der `mwh_d > 0` og `nok_d > 0`)
- `rå_d` = `oppnådd_d / spot_d`

Filter: behold bare dager der `rå_d` ligger mellom 5- og 95-persentilen av `rå_d` for dette anlegget over **hele tilgjengelige tidsserie** (ikke bare den valgte perioden — matcher Excel-arket som beregner persentilen på tvers av 8 år).

```
gyldige_dager = { d : rå_d ∈ [P5, P95] }
dag_cr        = Σ_{d∈gyldige_dager} (mwh_d × rå_d) / Σ_{d∈gyldige_dager} (mwh_d)
```

Volumvektet snitt av filtrert dag-rate. Persentilgrenser konfigurerbare via `CaptureRateOptions.PercentileLow` (default 0.05) og `PercentileHigh` (default 0.95).

### Merverdi (NOK)

```
merverdi = Σ_t∈H_t (nok_t − spot_t × mwh_t)
```

Penger på bordet relativt til ren spotinntekt. Positiv = anlegget tjente mer enn rent spot. Skal være ≈ 0 i teorien hvis avregningen følger spot eksakt — eventuelle avvik skyldes prismekanismer i avregningen (intra-day-justeringer, regulerkraft inkludert i nok_t hvis det er tilfelle, osv.). Holdes som separat KPI for transparens.

## Dataflyt

```
┌──────────────────┐
│ Settlement-import│ (eksisterer — månedlig drag-drop per anlegg)
└────────┬─────────┘
         │ AfterImported event
         ▼
┌──────────────────────────┐
│ MarketPriceUpsertHandler │ NY — extracts unique (time_utc, spotpris) → market_prices
└────────┬─────────────────┘
         │
         ▼
┌──────────────────────────┐      ┌──────────────────────┐
│ core.market_prices       │◄─────│ ENTSO-E backfill-job │ (manuell trigger)
│ (price_area, time, spot) │      │ fyller hull          │
└────────┬─────────────────┘      └──────────────────────┘
         │
         ▼
┌──────────────────────────┐
│ CaptureRateCalculator    │ NY — pure funksjon, beregner begge varianter
└────────┬─────────────────┘
         │
         ▼
┌──────────────────────────┐
│ ICaptureRateQueryService │ NY — orchestrerer DB-queries og kall til calculator
└────────┬─────────────────┘
         │
         ├─► API endpoint /api/v1/plants/{id}/capture-rate
         ├─► Portfolio-dashboard kolonne (kall fra PortfolioQueryService)
         ├─► Effektivitets-side KPI-kort (kall fra EffektivitetQueryService)
         └─► /capture-rate/{plantId}-side
```

**Anlegg-uavhengighet:** alle anlegg som har settlement-data og er i NO2 prisområde får CR automatisk. Prisområde leses fra `Plant.PriceArea` (eksisterende felt — sjekk; hvis mangler, legg til).

## Endringer i kodebasen

### 1. Ny tabell `core.market_prices`

**Fil:** `src/KraftverkUptime.Infrastructure/Persistence/Entities/MarketPrice.cs`

```csharp
public sealed class MarketPriceEntry
{
    public required string PriceArea { get; init; }   // "NO2", "NO5" etc.
    public required DateTimeOffset TimeUtc { get; init; }
    public required double PriceNokMwh { get; init; }
    public required string Source { get; init; }      // "settlement", "entsoe", "manual_csv"
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
```

**Migrering:**

```sql
CREATE TABLE core.market_prices (
    price_area VARCHAR(8) NOT NULL,
    time_utc TIMESTAMPTZ NOT NULL,
    price_nok_mwh DOUBLE PRECISION NOT NULL,
    source VARCHAR(16) NOT NULL,
    recorded_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (price_area, time_utc)
);

CREATE INDEX ix_market_prices_time ON core.market_prices(time_utc);
```

UPSERT-strategi: `ON CONFLICT (price_area, time_utc) DO UPDATE` der nyere `recorded_at_utc` vinner. Settlement-source har prioritet over entsoe (begge matcher hverandre i praksis, men settlement er kontraktuell).

### 2. Plant.PriceArea (verifiser at felt finnes)

**Fil:** `src/KraftverkUptime.Infrastructure/Persistence/Entities/PlantRegistration.cs`

Sjekk om `PriceArea` finnes. Hvis ikke, legg til:

```csharp
public string PriceArea { get; set; } = "NO2"; // Default for Sokndal/Dalane-anleggene
```

Migrering med backfill: alle 11 anlegg → "NO2".

### 3. MarketPriceUpsertHandler

**Fil:** `src/KraftverkUptime.Infrastructure/Events/MarketPriceUpsertHandler.cs` NY

Lytter på `SettlementImportedEvent`. Henter timesrader fra fila, projekterer til `(time_utc, spotpris)` med plantens `PriceArea`, UPSERT til `core.market_prices` med source=`'settlement'`.

Ignorer rader der `SpotprisNokMwh` er null. Logg antall upserterte rader.

### 4. ENTSO-E backfill-jobb

**Fil:** `src/KraftverkUptime.Infrastructure/MarketData/EntsoeBackfillJob.cs` NY

```csharp
public sealed class EntsoeBackfillJob : IJobHandler<EntsoeBackfillRequest>
{
    public async Task ExecuteAsync(EntsoeBackfillRequest req, CancellationToken ct)
    {
        // 1. Finn timer-hull i [req.From, req.To) for req.PriceArea i market_prices
        // 2. For hver hull-periode: kall ENTSO-E /api/v1 transparency endpoint
        //    DocumentType=A44 (Day-ahead Prices)
        //    In_Domain/Out_Domain = NO2 EIC-kode "10YNO-2--------T"
        // 3. Parse XML respons (Period > Point > price.amount i EUR/MWh)
        // 4. Konverter EUR→NOK med daglig snitt fra ECB (eller fast kurs konfigurert)
        // 5. UPSERT med source='entsoe'
    }
}
```

**Konfig:** `appsettings.json`

```json
{
  "MarketData": {
    "EntsoE": {
      "BaseUrl": "https://web-api.tp.entsoe.eu/api",
      "Token": "<security-token>",
      "EurNokRate": 11.5,
      "_comment_rate": "Forenkling v1 — fast kurs. v2 henter daglig kurs fra ECB."
    }
  }
}
```

Token settes via Azure Key Vault i prod, brukerprofil/env-variable i dev.

**Trigger:** UI-knapp på `/capture-rate/{plantId}` "Fyll hull fra ENTSO-E", + cron-job daglig kl 14:30 (etter day-ahead-publisering kl 13:00 CET).

### 5. CaptureRateCalculator (pure funksjon)

**Fil:** `src/KraftverkUptime.Modules.Reporting/CaptureRate/CaptureRateCalculator.cs` NY

```csharp
public static class CaptureRateCalculator
{
    public sealed record HourlyInput(
        DateTimeOffset TimeUtc,
        double? MwhElhub,
        double? SpotprisNokMwh,
        double? SpotomsetningNok);

    public sealed record DailyInput(
        DateOnly Date,
        double MwhDay,
        double NokDay,
        double SpotDayAvg);

    public sealed record CaptureRateResult(
        double CapturePriceNokMwh,
        double TimesCr,
        double TimesBaselineNokMwh,
        double DagCr,
        double DagBaselineNokMwh,
        double MerverdiNok,
        int AntallTimer,
        int AntallDager,
        int AntallDagerEtterFilter);

    public static CaptureRateResult Compute(
        IReadOnlyList<HourlyInput> hours,
        IReadOnlyList<DailyInput> historicalDailyForPercentile,
        double percentileLow = 0.05,
        double percentileHigh = 0.95);
}
```

`historicalDailyForPercentile` er hele tidsserien til anlegget (typisk fra 2018-) for å beregne 5/95-persentilen — matcher Excel-modellen.

Tester per state: full periode komplett, manglende timer, anlegg uten data, kun én dag, dag med 0 produksjon, persentilfilter-edge-cases.

### 6. ICaptureRateQueryService

**Fil:** `src/KraftverkUptime.Modules.Reporting/CaptureRate/ICaptureRateQueryService.cs` NY

```csharp
public interface ICaptureRateQueryService
{
    Task<CaptureRateResult> GetForPlantAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    Task<IReadOnlyList<MonthlyCaptureRate>> GetMonthlySeriesAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    Task<IReadOnlyList<DailyCaptureRate>> GetDailySeriesAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

public sealed record MonthlyCaptureRate(int Year, int Month, CaptureRateResult Result);
public sealed record DailyCaptureRate(DateOnly Date, double RaCr, double SpotDay, double OppnaddDay, bool Filtrert);
```

Henter timesrader fra settlement, daglig spot fra `market_prices` (aggregert til dag), kaller `CaptureRateCalculator.Compute`.

### 7. API-endepunkter

**Fil:** `src/KraftverkUptime.Api/Endpoints/CaptureRateEndpoints.cs` NY

```
GET /api/v1/plants/{plantId}/capture-rate?from=&to=
GET /api/v1/plants/{plantId}/capture-rate/monthly?from=&to=
GET /api/v1/plants/{plantId}/capture-rate/daily?from=&to=
POST /api/v1/plants/{plantId}/capture-rate/backfill-prices  (trigger ENTSO-E job)
```

DTO-er i `Api.Contracts.CaptureRateContracts`. Følg eksisterende mønster fra `EffektivitetEndpoints`.

### 8. Portefølje-dashboard utvidelse

**Fil:** `src/KraftverkUptime.Infrastructure/Reporting/PortfolioQueryService.cs`

Legg til `CaptureRate` (times-versjon) og `CapturePriceNokMwh` per anlegg i `PortfolioPlantKpis`. Beregnes per request via `ICaptureRateQueryService.GetForPlantAsync`.

**Fil:** `src/KraftverkUptime.Web/Pages/Portefolje.razor`

Ny sortbar kolonne "Capture rate" og "Capture price (NOK/MWh)".

### 9. Effektivitets-side utvidelse

**Fil:** `src/KraftverkUptime.Web/Pages/Effektivitet.razor`

Nytt KPI-kort: "Capture rate (times)" med dag-CR i tooltip. Ved siden av snitt η, sweet-spot, SVF.

### 10. Egen capture rate-side

**Fil:** `src/KraftverkUptime.Web/Pages/CaptureRate.razor` NY
**Rute:** `/capture-rate/{plantId}`

Innhold:
1. KPI-rad: capture price, times-CR, dag-CR (Excel-replika), merverdi NOK
2. Linje-graf: månedlig CR siste 24 mnd (times + dag som to serier)
3. Scatterplot: dag-CR vs MWh-produksjon — viser om høy CR korrelerer med produksjonsvolum
4. Histogram: distribusjon av rå dag-CR med persentilgrenser markert (visualiserer hva filteret fjerner)
5. Tabell: månedlig oversikt med capture price, baseline, CR, merverdi
6. Knapp: "Fyll hull fra ENTSO-E" hvis det finnes timer uten pris

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt
2. `core.market_prices` opprettes via migrering
3. Drag-drop av settlement-fil oppdaterer `market_prices` automatisk
4. `GET /api/v1/plants/drivdal/capture-rate?from=2025-01-01T00:00:00Z&to=2026-01-01T00:00:00Z` returnerer `dagCr` ≈ **1.086 ± 0.005** (matcher Excel-pivot)
5. Samme test for alle 11 anlegg som har data for 2025 — avvik fra Excel-pivot ≤ 0.5 %:

   | Anlegg | Excel dagCr 2025 | Toleranse |
   |---|---:|---:|
   | drivdal | 1.0857 | ±0.005 |
   | grodemfoss | 0.9993 | ±0.005 |
   | haukland | 1.0141 | ±0.005 |
   | honnefoss | 1.0199 | ±0.005 |
   | liavatn | 0.9953 | ±0.005 |
   | lindland | 0.9927 | ±0.005 |
   | logjen | 1.0464 | ±0.005 |
   | stolskraft | 0.9937 | ±0.005 |
   | vikesa | 1.0098 | ±0.005 |
   | ogreyfoss | 1.0198 | ±0.005 |
   | orsdalen | 0.99999 | ±0.005 |

6. Times-CR returnerer verdi i samme størrelsesorden (typisk innenfor ±2 % av dag-CR for vannkraft uten ekstrem intra-dag-regulering)
7. `/portefolje` viser CR-kolonne sortbar
8. `/capture-rate/drivdal` rendrer alle widgets uten feil

### Datakvalitet

9. Hvis `market_prices` mangler timer i forespurt periode: returner `CaptureRateResult` med `DataQualityFlags = ["MissingPrices: N timer mangler"]` istedenfor å feile
10. Hvis settlement-fila har 0 produksjon i hele perioden: returner alle felt = 0, ikke null/exception
11. Hvis et anlegg ikke har `PriceArea` satt: feilmelding på `/capture-rate/{plantId}` med lenke til admin-side

### Test-dekning

12. `CaptureRateCalculator` har minst 12 unit-tester:
    - Standard happy path
    - 100% jevn produksjon → CR = 1.0 nøyaktig
    - All produksjon i én høyprist-time → CR > 1.5
    - All produksjon i lavprist-timer → CR < 0.7
    - Persentilfilter fjerner riktig antall dager
    - Tom timesliste
    - Periode kortere enn 1 dag
    - Manglende spotpris i deler
    - Manglende produksjon i deler
    - Negative spot-priser (hender)
    - Krysser månedsskifte
    - Hele tidsserie 2018-2025 for Drivdal → matcher Excel ±0.005

13. `MarketPriceUpsertHandler` har 3 tester: happy path, idempotens (samme fil to ganger → ikke duplikat), nullverdier filtreres ut

14. `EntsoeBackfillJob` har integration-test mot mock HttpClient: parser XML korrekt, konverterer EUR→NOK, UPSERT med source='entsoe'

15. End-to-end-test: importer Drivdal-fil for jan 2026, kall `/capture-rate?from=2026-01-01&to=2026-02-01`, verifiser at både dag-CR og times-CR er innenfor rimelige grenser

## Implementasjons-rekkefølge

1. **Datamodell** — `MarketPrice` entity + migrering (1-2 t)
2. **Settlement → market_prices wiring** — handler + tester (2 t)
3. **CaptureRateCalculator** — pure funksjon + 12 unit-tester (4 t)
4. **CaptureRateQueryService** — orchestrering + DB-queries (3 t)
5. **API-endepunkter** — endpoints + contracts (1 t)
6. **Regresjonstest** mot Excel-pivot for alle 11 anlegg (2 t)
7. **Portefølje-kolonne** — utvid PortfolioQueryService + UI (1-2 t)
8. **Effektivitets-KPI-kort** (30 min)
9. **CaptureRate-side** — Razor-page med graf/scatter/histogram (3-4 t)
10. **ENTSO-E backfill-jobb** — kun nødvendig hvis settlement-data har hull (3-4 t — kan deferres til v2)

Estimat totalt: 1-2 dager for steg 1-9, +0.5 dag for ENTSO-E.

## Antakelser

1. **Settlement-fila inneholder alle timer i måneden** med utfylt `SpotprisNokMwh` for hver time, uavhengig av om anlegget produserte. Dette er bekreftet via brukervalg 2026-04-30. Hvis denne antakelsen brytes for et fremtidig anlegg, vil ENTSO-E backfill-jobben fylle hullene.
2. **Alle 11 anlegg er i prisområde NO2.** Hardkodet default; sjekkes mot `Plant.PriceArea` ved CR-beregning.
3. **EUR→NOK-konvertering for ENTSO-E v1 bruker fast kurs** (11.5). v2 henter daglig kurs fra ECB. Settlement-baserte priser er allerede i NOK, så fast kurs påvirker bare backfill-perioder.
4. **Persentilgrenser 5/95 låses som standard** men er konfigurerbare per anlegg via `CaptureRateOptions` hvis behov oppstår.
5. **Dag-CR beregnes på lokal tidssone (Europe/Oslo)** for å matche Excel-modellens "kalenderdag". Times-CR bruker UTC-timer (irrelevant for resultatet siden volumvektingen er invariant under tidssoneskifte).

## Ut-av-scope for v1

- Vannverdi-modell (alternativkost — egen spec)
- Capture rate for vindkraft (NO2 dekker både; samme formel — krever bare at vindanlegg får `PriceArea = "NO2"`)
- Multi-prisområde-portefølje (alle nåværende anlegg er NO2)
- Forecast-CR mot terminmarkedet (egen spec senere)
- Ubalanse-justert capture rate (netto-versjon) — kun brutto i v1, brukerens valg

## Verifikasjon

```powershell
# 1. Migrering
cd src\KraftverkUptime.Api
dotnet ef database update

# 2. Backfill market_prices fra eksisterende settlement-imports
curl.exe -X POST "http://localhost:5080/api/v1/admin/market-prices/rebuild-from-settlement"

# 3. CR for hvert anlegg 2025
$plants = @('drivdal','logjen','grodemfoss','haukland','honnefoss','lindland','ogreyfoss','orsdalen','liavatn','vikesa','stolskraft')
foreach ($p in $plants) {
    $r = curl.exe "http://localhost:5080/api/v1/plants/$p/capture-rate?from=2025-01-01T00:00:00Z&to=2026-01-01T00:00:00Z" | ConvertFrom-Json
    Write-Host "$p`: dagCr=$($r.dagCr.ToString('0.0000')) timesCr=$($r.timesCr.ToString('0.0000')) merverdi=$($r.merverdiNok.ToString('N0'))"
}

# Forventet output (basert på Excel-pivot 2025):
# drivdal: dagCr=1.0857 timesCr=~1.0857 merverdi=361 371
# lindland: dagCr=0.9927 timesCr=~0.9927 merverdi=190 385
# ogreyfoss: dagCr=1.0198 timesCr=~1.0198 merverdi=1 341 104
# ...

# 4. UI-test
# http://localhost:5180/capture-rate/drivdal
# http://localhost:5180/portefolje  (sjekk ny CR-kolonne)
# http://localhost:5180/effektivitet/drivdal  (sjekk nytt KPI-kort)
```

## Referanser

- Excel-fasit: `Capture Rate KPI.xlsx` (uploads, 2026-04-30)
- ENTSO-E API: https://transparency.entsoe.eu/content/static_content/Static%20content/web%20api/Guide.html
- ENTSO-E EIC-koder: https://www.entsoe.eu/data/energy-identification-codes-eic/eic-approved-codes/
- NO2 EIC: `10YNO-2--------T`
- Statnett om capture rate: https://www.statnett.no/ (standard bransje-definisjon)
