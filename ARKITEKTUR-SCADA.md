# Arkitektur-notat: SCADA-skala og portefølje-analyse

Dato: 2026-04-28
Status: Foundation-design — ikke startet implementasjon
Scope: Punkt 3-5 fra prioriteringslisten (TimescaleDB, kpi_facts, SCADA-pipeline)

## Hvorfor dette notatet finnes

Phase A og B er bygd på en datamodell som er optimal for **timeoppløst settlement-data**:
ParseSettlementJob leser Excel-filen, klassifiserer 24 timer per dag, og lagrer
hele rapporten som JSON-blob. Det fungerer utmerket i nåværende skala (én rapport
≈ 500 KB, klassifisering tar < 1 sekund).

Når SCADA kobles inn endrer datadimensjonene seg radikalt:

| Datatype | Frekvens | Punkter/anlegg/dag |
|---|---|---|
| Settlement (i dag) | 1 t | 24 |
| SCADA-aggregat | 1 min | 1 440 |
| SCADA standard | 1 sek | 86 400 |
| Vibrasjon, vern (høyfrekvent) | 100 Hz | 8 640 000 |

For 20 anlegg × 100 signaler × 86 400 samples × 30 dager:
**~5 milliarder datapunkter per måned**. 1000x+ økning fra settlement.

Dagens arkitektur tåler ikke det. Dette notatet beskriver foundation-skiftet
som må gjøres **før SCADA-importen designes**.

## Hva som ryker uten endring

| Komponent | Funker for settlement | Sprenger ved SCADA |
|---|---|---|
| Blob med full UptimeReport-JSON | ~500 KB | Hver rapport blir 100-500 MB JSON |
| `Classified`-liste i WASM-minnet | 672 timer | Millioner av rader sprenger heap |
| `SettlementImports` som single source | 1 rad/import | Trenger samples i tabell, ikke fil |
| Postgres uten time-series-optimalisering | OK for små volum | B-tree-indekser blir trege på milliarder rader |
| Hourly aggregation hardkodet i klassifikator | Riktig granularitet for settlement | Trenger flere LOD-er (level of detail) |

## Anbefalt målarkitektur

### 1. TimescaleDB istedenfor vanlig Postgres

TimescaleDB er Postgres + en extension. Alle eksisterende SQL-spørringer fungerer.
Vi får i tillegg:

- **Hypertables** — automatisk partisjonering på tid; hver chunk er en separat
  fysisk tabell, så DELETE/INSERT på én uke berører ikke 5 år med data.
- **Continuous aggregates** — auto-oppdaterte materialiserte views på 1-min,
  15-min, 1-time, 1-dag-resolusjon.
- **Compression policies** — komprimerer rå-chunks etter en gitt alder
  (typisk 10x reduksjon).
- **Retention policies** — automatisk DROP av chunks eldre enn N dager.

Bytte er trivielt:

```yaml
# docker-compose.yml
postgres:
  image: timescale/timescaledb:latest-pg17
```

Plus én `CREATE EXTENSION IF NOT EXISTS timescaledb` i bootstrapperen.
Eksisterende tabeller forblir uendret.

### 2. `sample_facts` hypertable

```sql
CREATE TABLE core.sample_facts (
    asset_id varchar(64) NOT NULL,
    signal_id varchar(64) NOT NULL,
    time_utc timestamptz NOT NULL,
    value double precision,
    quality smallint,           -- 0=good, 1=uncertain, 2=bad
    PRIMARY KEY (asset_id, signal_id, time_utc)
);

SELECT create_hypertable('core.sample_facts', 'time_utc',
    chunk_time_interval => INTERVAL '7 days');

-- Komprimere chunks eldre enn 14 dager (10x lagringsreduksjon)
ALTER TABLE core.sample_facts SET (timescaledb.compress);
SELECT add_compression_policy('core.sample_facts', INTERVAL '14 days');
```

Denne tabellen lever side om side med `settlement_imports`. Settlement
fortsetter å skrive blob (uendret) en stund — vi flytter den over til
`sample_facts` når det er praktisk.

### 3. Continuous aggregates (auto-LOD)

```sql
CREATE MATERIALIZED VIEW core.sample_facts_1min
WITH (timescaledb.continuous) AS
SELECT asset_id, signal_id,
       time_bucket('1 minute', time_utc) AS bucket,
       AVG(value) AS avg_value,
       MIN(value) AS min_value,
       MAX(value) AS max_value,
       COUNT(*) AS sample_count
FROM core.sample_facts
GROUP BY asset_id, signal_id, bucket;

SELECT add_continuous_aggregate_policy('core.sample_facts_1min',
    start_offset => INTERVAL '2 days',
    end_offset => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');
```

Tilsvarende for `_15min`, `_1hour`, `_1day`. UI velger resolusjon basert på
zoom-vindu:

| Tidsvindu | Foreslått LOD |
|---|---|
| Siste time | 1 sek (rå) |
| Siste døgn | 1 min |
| Siste uke | 15 min |
| Siste måned | 1 time |
| Lengre | 1 dag |

Read-API-en eksponerer `?resolution=auto|1s|1min|15min|1h|1d` og fallback til
auto basert på from/to.

### 4. Retensjonspolicy

```sql
SELECT add_retention_policy('core.sample_facts', INTERVAL '60 days');
-- Continuous aggregates beholdes uten retensjon — historisk LOD koster nesten ingenting.
```

Resultat: 60 dager rå 1-Hz-data + uavkortet 1-min/1-time/1-dag-historikk.
For 20 anlegg × 100 signaler:

- Rå: 60 dager × 8.64 M punkter/dag = 17 mrd → ~340 GB ukomprimert,
  ~34 GB komprimert.
- 1-min: ubegrenset × 1 440 punkter/dag = 5 år ≈ 26 mrd → ~520 GB.
- 1-time: ubegrenset × 24 punkter/dag = 5 år ≈ 0.4 mrd → ~10 GB.

Akseptabelt på en standard Postgres-vert (1-2 TB SSD).

### 5. `kpi_facts` materialiserte fakta-tabell

For raske portefølje-spørringer på tvers av rapporter:

```sql
CREATE TABLE core.kpi_facts (
    plant_id varchar(64) NOT NULL,
    period_start_utc timestamptz NOT NULL,
    period_end_utc timestamptz NOT NULL,
    period_resolution varchar(16) NOT NULL,   -- 'hour', 'day', 'week', 'month'
    kpi_name varchar(64) NOT NULL,
    value double precision,
    confidence double precision,
    PRIMARY KEY (plant_id, period_start_utc, period_resolution, kpi_name)
);
CREATE INDEX ix_kpi_facts_plant_kpi ON core.kpi_facts (plant_id, kpi_name, period_start_utc);
```

ParseSettlementJob skriver til denne tabellen i tillegg til blob (eller etter
hvert: istedenfor blob). Portefølje-dashboard gjør én SQL-spørring:

```sql
SELECT plant_id, period_start_utc, value
FROM core.kpi_facts
WHERE kpi_name = 'AvailabilityFactor_AF'
  AND period_resolution = 'month'
  AND period_start_utc >= '2024-01-01'
ORDER BY plant_id, period_start_utc;
```

Får ut KPI-trend per anlegg per måned, klar for plotting.

### 6. SCADA-importpipeline

Worker-modul `KraftverkUptime.Modules.Scada` tar imot streamede samples
(MQTT, OPC-UA-bridge, eller batch-CSV/parquet-opplastning) og batch-INSERT-er
i `sample_facts`:

```csharp
public async Task IngestAsync(IAsyncEnumerable<Sample> samples, CancellationToken ct)
{
    var batch = new List<Sample>(1000);
    await foreach (var sample in samples.WithCancellation(ct))
    {
        batch.Add(sample);
        if (batch.Count >= 1000)
        {
            await _writer.BulkInsertAsync(batch, ct);
            batch.Clear();
        }
    }
    if (batch.Count > 0) await _writer.BulkInsertAsync(batch, ct);
}
```

Bruk `Npgsql`'s `BeginBinaryImportAsync` for COPY-protokoll-rate (50-100k rows/s).

### 7. Klassifikator over `sample_facts`

`SettlementClassifier` blir en spesialvariant som leser fra `sample_facts_1hour`
(continuous aggregate) for én asset, akkurat som den i dag leser fra
`SettlementHourlyRow`. Logikken er den samme — inputkilden bytter.

På sikt kan klassifikator brukes med høyere oppløsning (1-min) for å oppdage
korte trip-events som forsvinner i 1-time-aggregering.

## Migrasjonsstrategi

Ikke "big bang" — gjør foundation-skiftet inkrementelt mens settlement-flyten
fortsatt fungerer:

| Steg | Hva | Effekt |
|---|---|---|
| **3a** | Bytt postgres-image til timescaledb. Kjør `CREATE EXTENSION` i bootstrapper. | Ingen funksjonell endring; klar for hypertables. |
| **3b** | Legg til `sample_facts` hypertable og continuous aggregates. Tom tabell foreløpig. | Foundation klar. |
| **4a** | Legg til `kpi_facts`-tabell. Modifiser `ParseSettlementJob` til å skrive der i tillegg til blob. | Settlement-rapporter kan nå spørres relasjonelt. |
| **4b** | Bygg `/portefolje`-dashboard som leser fra `kpi_facts`. | Bruker får portefølje-visning på dagen. |
| **5a** | Bygg SCADA-ingest-modul (MQTT/OPC-bridge). Skrive til `sample_facts`. | SCADA-data flyter inn — ingen klassifisering ennå. |
| **5b** | Bytt SettlementClassifier-input fra Excel-rader til `sample_facts_1hour`. | Settlement og SCADA går gjennom samme klassifikator. |
| **5c** | Fase ut blob-storage for rapporter. JSON kan regenereres on-demand fra `kpi_facts` + `sample_facts`. | En kilde for sannhet. |

Hvert steg er deploybart isolert. Roll-back ved problemer er en docker-image-revert
(steg 3a) eller å droppe den nye tabellen (3b, 4a). Etter steg 5c kan blob-bucketen
arkiveres til kald lagring.

## Effort-estimat

| Steg | Estimat |
|---|---|
| 3a (TimescaleDB) | 1 t |
| 3b (sample_facts + LOD) | 4 t |
| 4a (kpi_facts + skriving) | 6 t |
| 4b (portefølje-dashboard) | 1-2 dager |
| 5a (SCADA-ingest, valgt protokoll) | 1-2 uker |
| 5b (klassifikator over sample_facts) | 3-4 dager |
| 5c (blob-utfasing) | 2-3 dager |

Foundation alene (3a-4b) er en uke. Det gir oss portefølje-visning OG klar
infrastruktur for SCADA. Resten kommer når SCADA-tilgang er på plass.

## Beslutninger som må tas først

1. **TimescaleDB vs InfluxDB vs Azure Data Explorer.**
   Anbefaling: TimescaleDB. Hvorfor: Postgres-kompatibel (én DB-engine),
   SQL-spørringer (ikke Flux/KQL), åpen kildekode + Apache 2-lisens,
   integrerer rett inn i eksisterende EF Core-stack. InfluxDB ville krevd
   en ny driver og separat back-up-strategi. ADX er Azure-bound og dyrere
   for vårt volum.

2. **SCADA-protokoll: MQTT vs OPC-UA bridge vs batch-import.**
   Trenger info om hvordan kraftverkene faktisk eksponerer SCADA.
   - MQTT: lett, krever broker — ofte best for nyere installasjoner.
   - OPC-UA: industristandard, mer komplekse klienter, krever bridge-tjeneste.
   - Batch-CSV/parquet: greit for backfill av historiske data og som fallback.
   Anbefaling: bygg batch-import først (gjenbruker dagens upload-flyt),
   legg til streaming senere.

3. **Multi-tenant isolasjon for samples.**
   `sample_facts` har `asset_id` men ikke `owner_org_id`. Bør vi:
   - (A) Legge til `owner_org_id` direkte → litt dataduplisering, raskere filter
   - (B) Joine til `plants` for filter → renere model, marginalt tregere
   Anbefaling: (A). Tenant-filter må være ekstremt rask når man skanner milliarder
   av rader. Duplisering er trivielt vs spørringskostnad.

## Hva dette IKKE løser

- **Realtime alarmer på SCADA-data.** Continuous aggregates har 1-min
  oppdateringsforsinkelse. For trip-detection trenger vi en separat
  streaming-pipeline (Kafka Streams / Flink / .NET Channels).
- **Dashboarding-ytelse for 20+ samtidige brukere.** Read-replica av
  TimescaleDB kan trengs ved den belastningen.
- **Lagring i Azure.** TimescaleDB kjører fint på Azure Database for
  PostgreSQL Flexible Server (med extension aktivert), men det krever
  bekreftelse fra Azure-fronten.

Disse er kjente begrensninger som adresseres når de blir relevante.
