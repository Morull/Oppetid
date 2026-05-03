# Spec: Import-completeness-dashboard og varsling

**Status:** Klar til implementasjon (2026-05-03)
**Estimat:** 2-3 dager (datamodell + backend + dashboard + ukentlig varsel)
**Avhengighet:** Eksisterende settlement-import + planlagte SCADA/operlog/Hydrogrid-importer

## Bakgrunn

Med 11 anlegg, fire datakilder per anlegg (settlement, SCADA, operlog, Hydrogrid-plan) og månedlige eller ukentlige importer for hver, blir det fort 30-50 separate filer per måned. Drifts-leder har observert at det er lett å glemme noen — særlig når en eksport venter på at en annen er ferdig, eller når et anlegg er ute av drift en periode.

Dagens system har **ingen oversikt** over hvilke datakilder som er importert per anlegg per periode, eller hvilke som mangler. KPI-rapporter bygger på det som faktisk er der, uten å si noe om hva som burde vært der.

## Beslutning

Bygg en `core.data_completeness`-modell som per anlegg per måned holder oversikt over:

1. **Forventede datakilder** (per anleggs-konfigurasjon — alle anlegg har f.eks. settlement, men ikke alle har SCADA enda)
2. **Faktiske importer** (timestamps, tag-counts, dekning)
3. **Status per kilde:** komplett / delvis / mangler / forfalt

Eksponer dette som:

- **Dashboard-side `/data-status`** — matrise med anlegg på y-aksen og perioder på x-aksen, fargekodet per celle
- **Ukentlig e-post-varsel** til drifts-leder med summary av hva som mangler
- **API-endepunkt** for integrasjon med andre verktøy

## Datamodell

### Tabell `core.data_source_expectations`

Per (plant_id, source_type) — hvilke datakilder forventes for hvilke anlegg:

```sql
CREATE TABLE core.data_source_expectations (
    plant_id VARCHAR(64) NOT NULL,
    source_type VARCHAR(32) NOT NULL,    -- "settlement", "scada", "operlog", "hydrogrid_plan"
    cadence VARCHAR(16) NOT NULL,        -- "monthly", "weekly", "daily", "continuous"
    expected_lag_days INTEGER NOT NULL,  -- f.eks. settlement forventes 7 dager etter månedsskifte
    is_active BOOLEAN NOT NULL DEFAULT true,
    activated_at_utc TIMESTAMPTZ,
    deactivated_at_utc TIMESTAMPTZ,
    PRIMARY KEY (plant_id, source_type)
);
```

Backfill-eksempel:

```sql
-- Alle 11 anlegg forventes å ha settlement månedlig
INSERT INTO core.data_source_expectations (plant_id, source_type, cadence, expected_lag_days, activated_at_utc)
SELECT plant_id, 'settlement', 'monthly', 7, '2024-01-01' FROM core.plants;

-- Drivdal har SCADA — andre legges til etter hvert
INSERT INTO core.data_source_expectations VALUES
  ('drivdal', 'scada', 'monthly', 5, true, '2025-01-01', null),
  ('lindland', 'scada', 'monthly', 5, true, '2026-05-01', null),
  ('haukland', 'scada', 'monthly', 5, true, '2026-04-30', null);

-- Hydrogrid-plan via KAIA månedlig (kommer i settlement-fila)
INSERT INTO core.data_source_expectations
SELECT plant_id, 'hydrogrid_plan', 'monthly', 7, true, '2024-01-01', null FROM core.plants;
```

Drifts-leder kan via PlantAdmin-UI aktivere/deaktivere kilder per anlegg når situasjonen endres.

### Tabell `core.data_imports`

Hver gjennomført import logges:

```sql
CREATE TABLE core.data_imports (
    import_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plant_id VARCHAR(64) NOT NULL,
    source_type VARCHAR(32) NOT NULL,
    period_from_utc TIMESTAMPTZ NOT NULL,    -- f.eks. 2026-02-01 for feb-import
    period_to_utc TIMESTAMPTZ NOT NULL,
    imported_at_utc TIMESTAMPTZ NOT NULL,
    file_name VARCHAR(255),
    file_hash VARCHAR(64),
    rows_imported INTEGER,
    coverage_pct DOUBLE PRECISION,           -- f.eks. settlement med 670/672 timer = 99.7
    user_id VARCHAR(128),
    notes TEXT
);

CREATE INDEX ix_imports_plant_source_period ON core.data_imports(plant_id, source_type, period_from_utc DESC);
```

Hver eksisterende importør (SettlementImportHandler, SCADA-import, operlog-import, etc.) utvides med å skrive en rad her etter vellykket import.

### Tabell `core.data_completeness_digests`

Hver gang ukentlig digest beregnes (uavhengig av om e-post sendes), persisteres en snapshot av status. Gjør at brukeren kan se historiske digester direkte i appen og spore utvikling over tid:

```sql
CREATE TABLE core.data_completeness_digests (
    digest_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    generated_at_utc TIMESTAMPTZ NOT NULL,
    iso_year INTEGER NOT NULL,
    iso_week INTEGER NOT NULL,
    total_expected INTEGER NOT NULL,
    complete_count INTEGER NOT NULL,
    partial_count INTEGER NOT NULL,
    pending_count INTEGER NOT NULL,
    overdue_count INTEGER NOT NULL,
    summary_text TEXT NOT NULL,             -- Den humanlesbare setningen ("23 importer komplette ...")
    overdue_details JSONB NOT NULL,         -- Detaljert liste med plant_id, source_type, days_overdue
    partial_details JSONB NOT NULL,         -- Detaljert liste med plant_id, source_type, coverage_pct
    email_sent_to VARCHAR(255),             -- Null hvis e-post ikke ble sendt eller deaktivert
    email_sent_at_utc TIMESTAMPTZ,
    UNIQUE (iso_year, iso_week)
);

CREATE INDEX ix_digests_generated_desc ON core.data_completeness_digests(generated_at_utc DESC);
```

Idé: hver ukes-digest er én rad. Sammenfatningen som drifts-leder ser i innboksen er identisk med det som vises i appen — én sannhetskilde, samme tekst.

### View `core.data_completeness_view`

Beregnet view som krysser forventninger med faktiske importer:

```sql
CREATE VIEW core.data_completeness_view AS
WITH expected_periods AS (
    -- Generer alle (plant, source, period) som er forventet siden source ble aktivert
    SELECT 
        e.plant_id,
        e.source_type,
        e.cadence,
        e.expected_lag_days,
        period_start AS period_from_utc,
        period_start + INTERVAL '1 month' AS period_to_utc
    FROM core.data_source_expectations e
    CROSS JOIN LATERAL generate_series(
        date_trunc('month', e.activated_at_utc),
        date_trunc('month', NOW()),
        '1 month'::interval
    ) AS period_start
    WHERE e.is_active = true
),
imported AS (
    SELECT plant_id, source_type, period_from_utc,
           MAX(imported_at_utc) AS last_imported_at,
           MAX(coverage_pct) AS coverage_pct,
           COUNT(*) AS import_count
    FROM core.data_imports
    GROUP BY plant_id, source_type, period_from_utc
)
SELECT 
    e.plant_id,
    e.source_type,
    e.period_from_utc,
    e.period_to_utc,
    i.last_imported_at,
    i.coverage_pct,
    i.import_count,
    CASE
        WHEN i.last_imported_at IS NULL AND e.period_to_utc + (e.expected_lag_days || ' days')::interval < NOW()
            THEN 'OVERDUE'
        WHEN i.last_imported_at IS NULL
            THEN 'PENDING'
        WHEN i.coverage_pct < 0.95
            THEN 'PARTIAL'
        ELSE 'COMPLETE'
    END AS status
FROM expected_periods e
LEFT JOIN imported i USING (plant_id, source_type, period_from_utc);
```

Status-tilstander:

| Status | Betydning |
|---|---|
| `COMPLETE` | Importert med ≥ 95 % dekning |
| `PARTIAL` | Importert men < 95 % dekning (timer mangler) |
| `PENDING` | Ikke importert ennå, men det er innenfor forventet leveringsvindu |
| `OVERDUE` | Ikke importert og det er gått over `expected_lag_days` etter periodeslutt |

## Backend-tjenester

### `IDataCompletenessQueryService`

**Fil:** `src/KraftverkUptime.Modules.Reporting/DataCompleteness/IDataCompletenessQueryService.cs` NY

```csharp
public interface IDataCompletenessQueryService
{
    Task<DataCompletenessMatrix> GetMatrixAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<IReadOnlyList<MissingImport>> GetOverdueAsync(CancellationToken ct);

    Task<DataCompletenessSummary> GetWeeklySummaryAsync(CancellationToken ct);

    /// <summary>
    /// Henter siste persisterte digest. Brukes til banner på dashboard
    /// så drifts-leder ser samme tekst som ble sendt i forrige e-post.
    /// </summary>
    Task<DataCompletenessDigest?> GetLatestDigestAsync(CancellationToken ct);

    /// <summary>
    /// Henter historiske digester for arkiv-side. Default siste 12 uker.
    /// </summary>
    Task<IReadOnlyList<DataCompletenessDigest>> GetDigestHistoryAsync(int weeks = 12, CancellationToken ct = default);
}

public sealed record DataCompletenessMatrix(
    IReadOnlyList<string> PlantIds,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<DateTimeOffset> Periods,
    IReadOnlyDictionary<(string PlantId, string SourceType, DateTimeOffset Period), DataCompletenessCell> Cells);

public sealed record DataCompletenessCell(
    string Status,             // "COMPLETE", "PARTIAL", "PENDING", "OVERDUE"
    DateTimeOffset? LastImportedAt,
    double? CoveragePct,
    int ImportCount);

public sealed record MissingImport(
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    int DaysOverdue);

public sealed record DataCompletenessSummary(
    int TotalExpected,
    int Complete,
    int Partial,
    int Pending,
    int Overdue,
    IReadOnlyList<MissingImport> TopOverdue);

public sealed record DataCompletenessDigest(
    Guid DigestId,
    DateTimeOffset GeneratedAtUtc,
    int IsoYear,
    int IsoWeek,
    int TotalExpected,
    int CompleteCount,
    int PartialCount,
    int PendingCount,
    int OverdueCount,
    string SummaryText,                       // Banner-tekst og e-post-første-linje
    IReadOnlyList<MissingImport> Overdue,
    IReadOnlyList<PartialImport> Partial,
    string? EmailSentTo,
    DateTimeOffset? EmailSentAtUtc);

public sealed record PartialImport(
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    double CoveragePct);
```

### Importør-utvidelser

Hver importør (settlement, SCADA, operlog) skriver en `data_imports`-rad etter vellykket prosessering:

```csharp
// Eksempel i SettlementImportHandler etter at parsing er ferdig
await _dataImportsRepo.LogAsync(new DataImportRecord
{
    PlantId = plantId,
    SourceType = "settlement",
    PeriodFromUtc = parsed.PeriodFrom,
    PeriodToUtc = parsed.PeriodTo,
    FileName = file.Name,
    FileHash = ComputeHash(file),
    RowsImported = parsed.HourlyRows.Count,
    CoveragePct = parsed.HourlyRows.Count / (double)ExpectedHoursIn(parsed.PeriodFrom, parsed.PeriodTo),
    UserId = httpContext.User?.Identity?.Name ?? "system"
}, ct);
```

## API-endepunkter

```
GET /api/v1/data-status/matrix?from=&to=          (full matrise for dashboard)
GET /api/v1/data-status/overdue                   (kun overdue-rader)
GET /api/v1/data-status/summary                   (sammendrag for ukentlig varsel)
GET /api/v1/data-status/plants/{plantId}/timeline (per-anlegg-tidslinje)
GET /api/v1/data-status/digests/latest            (siste persisterte digest — for banner)
GET /api/v1/data-status/digests?weeks=12          (historiske digester for arkiv-side)
GET /api/v1/data-status/digests/{digestId}        (én spesifikk digest med full detalj)
PUT /api/v1/admin/data-source-expectations        (aktiver/deaktiver kilde per anlegg)
POST /api/v1/admin/data-completeness/regenerate-digest  (manuell trigger — for testing)
```

## UI

### Hovedside `/data-status`

**Fil:** `src/KraftverkUptime.Web/Pages/DataStatus.razor` NY

Layout:

1. **Digest-banner øverst** — viser samme tekst som sist sendte ukentlig e-post:
   ```
   Siste statusrapport (uke 18, mandag 06.05): 23 importer komplette,
   4 delvise, 1 forfalt — Honnefoss settlement for mars mangler fortsatt.
   [Se hele rapporten] [Vis arkiv]
   ```
   Hvis siste digest ble lest av brukeren tidligere: vises kollapset med liten tekst. Ny digest siden sist innlogging: utvidet med fargekoding (grønn hvis 0 overdue, gul hvis 1-3 overdue, rød hvis > 3 eller > 14 dager forfalt).

2. **"Live status nå"-sammendrag** — beregnet on-demand fra dagens dato, ikke fra siste digest. Kan avvike fra digest hvis ny import ble gjort etter mandagens kjøring:
   ```
   Live status (oppdatert nå): 24 komplette, 3 delvise, 1 forfalt.
   ```

3. **Matrise** — anlegg på y-akse, måneder på x-akse, fire celler per anlegg per måned (én per kilde-type)

4. **Filter:** vis kun overdue, eller filtrer på kilde-type

5. **Klikk på celle** → drilldown med detaljer (last imported, coverage, file name)

Eksempel-visning:

```
              2026-02   2026-03   2026-04   2026-05
              S O H Y   S O H Y   S O H Y   S O H Y
drivdal       █ █ █ █   █ █ █ █   █ █ █ █   ░ ░ ░ ░
lindland      █ . █ █   █ . █ █   █ . █ █   ░ . ░ ░
haukland      █ . . █   █ . . █   █ ░ . █   ░ ░ . ░
honnefoss     █ . . █   █ . . █   ! . . █   ░ . . ░
liavatnkraft  ! . . █   ! . . █   ! . . █   ░ . . ░
...

Legende:
█ = COMPLETE    ░ = PARTIAL    . = ikke aktiv kilde
! = OVERDUE     o = PENDING

S = Settlement   O = Operlog   H = Hydrogrid-plan   Y = SCADA
```

`liavatnkraft` har OVERDUE settlement i alle perioder fordi det er nytt anlegg uten settlement-flow ennå.

### Arkiv-side `/data-status/digests`

**Fil:** `src/KraftverkUptime.Web/Pages/DataStatusDigests.razor` NY

Liste over alle ukentlige digester siden funksjonen ble aktivert. Tabell:

```
Uke   Generert        Komplette  Delvise  Forfalt  E-post sendt   
─────────────────────────────────────────────────────────────────
18    06.05.2026 08:00    23         4        1     drift@...      [Se]
17    29.04.2026 08:00    21         5        2     drift@...      [Se]
16    22.04.2026 08:00    18         3        7     drift@...      [Se]
...
```

Klikk "Se" → expanderer rad med full digest-detaljer (samme format som e-posten):

```
Uke 18, 2026 (generert mandag 06.05.2026 kl 08:00)

✓ KOMPLETT (23 av 28 forventede)

⚠ DELVIS (4):
  - lindland settlement 2026-04: 670/672 timer (99.7 %)
  - drivdal scada 2026-04: 740/744 timer (99.5 %)
  - ...

✕ FORFALT (1):
  - honnefoss settlement 2026-03: forventet 2026-04-07, mangler fortsatt (28 dager)

E-post sendt til: drift@dalanekraft.no kl 08:00:14
```

Sortering: nyeste først. Filter for år/måned. Søk på plant_id eller source_type i tabellen.

### Plant-admin-utvidelse

På `/plants/{plantId}/admin`: ny seksjon "Forventede datakilder" der drifts-leder kan toggle hvilke kilder som er aktive for anlegget. Eksempel: aktiver SCADA fra 2026-05-01 når en eksport-flow settes opp.

## Ukentlig varsel

### Worker-job `DataCompletenessWeeklyDigestJob`

Kjører hver mandag kl 08:00. To-stegs-prosess:

1. **Generer digest** og persister i `core.data_completeness_digests`. Dette er én sannhetskilde — tekst, antall og detaljer som vises i UI er identiske med det som sendes per e-post.
2. **Send e-post** til konfigurerte mottakere. Hvis e-post-konfig mangler eller SMTP-feil: digesten persisteres uansett (tilgjengelig i UI), men `email_sent_to` og `email_sent_at_utc` forblir null.

Konsekvens: hvis e-post-leveransen feiler en uke, mister du ikke informasjonen — den er fortsatt synlig i appen og dukker opp som "ny digest" i banner-en neste gang du logger inn.

E-post-format (genereres fra digestens `summary_text` + `overdue_details`/`partial_details`):

```
Subject: KraftverkUptime — datakilder ukentlig status (uke 18, 2026)

Hei Morten,

Status på dataimporter for forrige periode:

✓ KOMPLETT (23 av 28 forventede): se /data-status

⚠ DELVIS (4):
  - lindland settlement 2026-04: 670/672 timer (99.7 %)
  - drivdal scada 2026-04: 740/744 timer (99.5 %)
  - haukland scada 2026-03: ikke importert ennå (innenfor frist)
  - honnefoss scada 2026-04: ikke importert ennå

✕ FORFALT (1):
  - honnefoss settlement 2026-03: forventet 2026-04-07, mangler fortsatt

Topp prioritet å hente inn:
  1. Honnefoss settlement for mars (28 dager forfalt)

Åpne dashboard: https://uptime.dalanekraft.no/data-status
```

Konfig:

```json
{
  "DataCompleteness": {
    "WeeklyDigestEmail": "drift@dalanekraft.no",
    "RunDayOfWeek": "Monday",
    "RunHourLocal": 8
  }
}
```

E-post sendes via SMTP eller Microsoft Graph (samme oppsett som eventuelle eksisterende e-post-utgående tjenester).

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 14 nye tester
2. Tabellene `data_source_expectations`, `data_imports` og view-en opprettes via migrering
3. Backfill av `data_source_expectations`: alle 11 anlegg har settlement + hydrogrid_plan-rader. SCADA legges til kun for anlegg med eksisterende SignalMap-data.
4. Backfill av `data_imports`: scan eksisterende `settlement_imports`-tabell + blob-storage og lag historisk import-logg
5. Settlement-import skriver til `data_imports` etter vellykket parsing
6. SCADA-import skriver til `data_imports` etter vellykket parsing
7. `GET /api/v1/data-status/matrix?from=2026-01-01&to=2026-05-01` returnerer matrise med korrekte celler
8. `/data-status`-siden rendrer matrisen med fargekoding
9. Plant-admin har "Forventede datakilder"-seksjon med toggles
10. Ukentlig job kjører og sender e-post med summary

### Datakvalitet

11. Anlegg uten aktive SCADA-kilder vises som "ikke aktiv" (`.`-tegn), ikke OVERDUE
12. Importer for fremtidige måneder vises som PENDING, ikke OVERDUE
13. Hvis to importer er gjort for samme periode (f.eks. korrigert versjon), siste vinner
14. Sletting av et anlegg setter `is_active = false` på alle expectation-rader, ikke hard delete

### Test-dekning

15. `DataCompletenessQueryServiceTests` — 8 tester
    - Komplett dekning → status COMPLETE
    - 90 % dekning → status PARTIAL
    - Manglende uten lag → PENDING
    - Manglende med lag overskredet → OVERDUE
    - Inaktiv kilde returnerer null
    - Cross-period-aggregering for summary
    - Ukentlig digest sorterer etter days_overdue desc
    - Edge case: nytt anlegg uten import-historikk

16. `DataCompletenessWeeklyDigestJobTests` — 4 tester (med mock e-post-sender)
    - Genererer riktig sammendrag
    - Sender til konfigurert e-post
    - Hopper over kjøring hvis ingen overdue/partial
    - Logger feil hvis SMTP ikke svarer, fortsetter

17. `DataImportLoggingTests` — 2 tester
    - Settlement-importør skriver rad etter vellykket import
    - Importør skriver IKKE rad ved feil

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | Datamodell + migreringer + backfill av expectations | 2-3 t |
| 2 | Importør-utvidelser (settlement først, så SCADA, så operlog) | 2-3 t |
| 3 | Backfill av `data_imports` fra eksisterende historikk | 1-2 t |
| 4 | `IDataCompletenessQueryService` + tester | 3 t |
| 5 | API-endepunkter | 1 t |
| 6 | `/data-status`-side med matrise + fargekoding | 4-5 t |
| 7 | PlantAdmin-utvidelse for å toggle kilder | 1-2 t |
| 8 | Ukentlig digest-job: generer + persister i `data_completeness_digests` | 2 t |
| 9 | E-post-utsendelse fra persistert digest | 2 t |
| 10 | `/data-status`-banner med siste digest + arkiv-side `/data-status/digests` | 3-4 t |

**Estimat totalt:** 2-3 dager.

## Antakelser

1. **E-post-utsendelse er tilgjengelig** — enten via SMTP eller Microsoft Graph. Hvis ikke, falle tilbake til intern notifikasjon i Cowork eller annen kanal.
2. **Kildene er per-anlegg-konfigurerbare** — drifts-leder vet hvilke anlegg som har SCADA, hvilke som har operlog osv. Toggling skjer via admin-UI.
3. **Periode = måned** for v1. Ukentlig granularitet for SCADA kan komme senere.
4. **Coverage = ratio** — settlement med 670/672 timer = 0.997. SCADA med < 95 % dekning trigger PARTIAL.
5. **Backfill-historikk for `data_imports`** kan utledes fra `core.settlement_imports`-tabellen + filnavnskonvensjoner i blob storage. Hvis dette er for komplisert, starter vi fra dagens dato uten historikk.

## Ut-av-scope for v1

- Forecast av når ny import er forventet basert på leverandør-mønster (f.eks. KAIA leverer typisk 5. virkedag)
- Automatisk pull av filer fra leverandør-endepunkt (e.g. KAIA API direkte)
- Per-time eller per-dag-granularitet for completeness (kun måned i v1)
- Multi-tenant-utvidelse
- SLA-sporing med automatisk eskalering til leverandør
- Push-varsler til mobil

## Verifikasjon

```powershell
# 1. Migrering
cd C:\Morten\00 Oppetid\src\KraftverkUptime.Api
dotnet ef database update

# 2. Backfill av expectations
psql -d kraftverkuptime -c "SELECT plant_id, source_type, is_active FROM core.data_source_expectations ORDER BY plant_id;"
# Forventet: 11 settlement + 11 hydrogrid_plan + N SCADA-rader

# 3. Backfill av imports
curl.exe -X POST "http://localhost:5080/api/v1/admin/data-completeness/backfill-from-settlement-history"

# 4. Sjekk matrisen
curl.exe "http://localhost:5080/api/v1/data-status/matrix?from=2026-01-01&to=2026-05-01" | ConvertFrom-Json

# 5. Trigger ukentlig digest manuelt
curl.exe -X POST "http://localhost:5080/api/v1/admin/data-completeness/trigger-weekly-digest"
# Sjekk e-postinnboks

# 6. UI-test
# http://localhost:5180/data-status — matrise vises med fargekoding
```

## Referanser

- Drifts-leders forespørsel 2026-05-03: behov for oversikt over import-status
- Eksisterende settlement-imports-tabell som mal
- Microsoft Graph API for e-post hvis SMTP ikke er tilgjengelig
