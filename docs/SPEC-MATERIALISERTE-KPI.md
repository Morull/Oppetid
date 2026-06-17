# SPEC: Materialiserte KPI-er / rapport-resultater (permanent rask kald-lasting)

**Status:** Forslag (design). Ikke implementert. Krever bekreftelse før migrasjon + backfill på live-data.
**Bakgrunn:** ytelse-arbeidet (memory `rapport-ytelse`). Etter T1 (delt blob-cache) + T4 (parallell vakt-roi) er
kald «alle anlegg, hittil i år» nede i ~8 s (fra 34 s), varm ~0,02 s. Denne spec-en dekker den siste leveren:
gjøre også **første** lasting (kald) tilnærmet momentan.

---

## 1. Problemet i dag

Rapportene regner alt fra rå blob-data ved første forespørsel:

- `/economy` (kald ~8 s) = mest **vakt-roi** (~6 s, parallellisert) + settlement-KPI-aggregering +
  capture-rate (lett) + nedetid + datakvalitet + kaia.
- Hver delkomponent leser `UptimeReport`-blober (nå delt via cache innen forespørselen) og kjører CPU-tung logikk.

Cachene (blob 2 min, economy-DTO 3 min) gjør **gjentatte** lastinger momentane, men **første** lasting etter
cache-utløp betaler full pris.

## 2. Viktig nyanse — hva materialisering faktisk løser

Det finnes to nivåer av «resultat» å materialisere:

| Nivå | Hva lagres | Hvilke rapporter blir momentane | Kostnad/risiko |
|------|-----------|-------------------------------|----------------|
| **A. KPI-snapshot** | `UptimeReport.Kpis` (Spotomsetning, Oppgjør, Ubalanse, MWh, AF, FOR, IEEE-AF …) + `PeriodHours` per import | Economy **settlement-KPI-delen**, evt. portefølje-KPI | Lav–middels |
| **B. Per-anlegg rapport-resultat** | I tillegg: vakt-roi reddet-NOK/reddbare per anlegg, nedetid-sum, capture-rate (times-CR/merverdi), datakvalitet per (anlegg, periode) | Economy **nesten helt** + vakt-roi-rapport | Høy (mange tjenester, mer KPI-korrekthets-overflate) |

Nivå A alene gir **begrenset** kald-gevinst, fordi vakt-roi (dominerende ~70 %) og capture-rate/nedetid
fortsatt prosesserer timesdata. **Momentant kald krever nivå B** — som er der mesteparten av risikoen ligger
(re-deriverer alle de fagvurderte KPI-ene ved klassifiseringstid og stoler på de lagrede verdiene).

## 3. Anbefalt datamodell

Ny tabell `import_kpi_snapshot` (Postgres), nøklet identisk med rapport-bloben:

```
owner_org_id      text      not null
plant_id          text      not null
idempotency_key   text      not null
period_start_utc  timestamptz not null
period_end_utc    timestamptz not null
period_hours      int       not null
classified_at_utc timestamptz not null
kpis              jsonb     not null   -- { "Spotomsetning_NOK": 123, "AvailabilityFactor_AF": 0.97, ... }
-- Nivå B (valgfritt, neste iterasjon):
vakt_reddet_nok   double precision null
vakt_reddbare     int null
nedetid_tap_nok   double precision null
nedetid_timer     double precision null
capture_times_cr  double precision null
merverdi_nok      double precision null
good_hours_pct    double precision null
mangler_import_hours double precision null
primary key (owner_org_id, plant_id, idempotency_key)
index (plant_id, period_start_utc, period_end_utc)   -- overlapp-spørringer
```

- `jsonb` for KPI-dict-en → ingen skjema-churn når KPI-katalogen endres.
- `classified_at_utc` løser samtidig **badge-tellingen** (`report_generated`-flagg, se memory
  `ui-rollebasert-og-kpi-korrekthet`): pending-count blir et billig `COUNT` på imports uten snapshot-rad.
- Samme overlapp-semantikk som i dag (`OverlappingImportResolver` brukes fortsatt på rad-settet).

## 4. Skrive-sti (compute-ved-klassifisering)

I `ClassifyOnImportedHandler.HandleAsync` (etter `_reportStore.SaveAsync`):

```csharp
await _kpiSnapshotStore.UpsertAsync(
    domainEvent.OwnerOrgId, domainEvent.PlantId, domainEvent.IdempotencyKey,
    report.Kpis, report.PeriodHours, period.StartUtc, period.EndUtc, classifiedAtUtc, ct);
```

Nytt seam `IKpiSnapshotStore` (Modules.Reporting) + EF-implementasjon (Infrastructure). Idempotent upsert
(samme nøkkel overskriver), akkurat som blob-storen. Nivå B: utvid med per-anlegg-resultatene (krever at
vakt-roi/capture-rate/nedetid kjøres ved klassifisering — eller lagres når de først beregnes).

## 5. Backfill av eksisterende data

**Anbefalt: lazy read-through** (ingen stor migrasjons-jobb, lavest risiko):

- Når en rapport-query ikke finner snapshot-rad → les bloben som i dag, beregn, **lagre snapshot**, returner.
- Første lasting per import betaler blob-pris én gang; deretter DB-rask for alltid.
- Valgfritt admin-endepunkt `POST /admin/kpi-snapshot/backfill` som itererer alle imports og fyller tabellen
  (kjør én gang manuelt; logg fremdrift; ingen warmer-loop — jf. ADVARSEL i memory `rapport-ytelse`).

## 6. Lese-sti (omskriving)

- **Nivå A:** `EconomyReportQueryService.GatherPlantAsync` — bytt `_reports.GetAsync(...).Kpis`-løkken med ett
  `_kpiSnapshot.ListForPlantsAsync(plantIds, from, to)`-kall (batch, alle anlegg i ett query). MWh-vektingen
  (AF/FOR/IEEE) bruker `period_hours`-kolonnen.
- **Nivå B:** vakt-roi/nedetid/capture-rate-kolonnene leses fra samme rad → `GatherPlantAsync` trenger nesten
  ingen blob-lesing. `PortfolioVaktRoiQueryService` kan returnere fra snapshot når kun aggregat trengs (detalj-
  /topp-event-visning faller fortsatt tilbake på full beregning).

## 7. Korrekthet & test

- KPI-verdiene MÅ være identiske med dagens blob-deriverte. Test: for et utvalg imports, assert at snapshot-
  KPI == blob-KPI (round-trip-test i Infrastructure.Tests).
- Behold blob-storen som kilde til sannhet; snapshot er en avledet cache i DB. Ved tvil/inkonsistens →
  re-materialiser fra blob.
- Kjør full suite i Docker (warnings-as-errors) som vanlig.

## 8. Utrulling (irreversibel del = migrasjon + backfill på live-data)

1. EF-migrasjon (ny tabell) — additiv, ingen endring av eksisterende tabeller → trygg.
2. Deploy skrive-sti (lazy read-through aktiv).
3. Verifiser snapshot==blob på live-data for noen anlegg.
4. (Valgfritt) kjør backfill-endepunktet én gang.
5. Deploy lese-sti-omskrivingen.

## 9. Anbefaling

Gjør **nivå A først** (lav risiko, additiv) og **mål** kald-gevinsten. Hvis vakt-roi fortsatt dominerer kald-
tiden, vurder nivå B for vakt-roi spesifikt (materialiser per-anlegg reddet-NOK) framfor en full omskriving.
Gitt at varm-stien allerede er momentan (~0,02 s) og kald er ~8 s, er kost/nytte for full nivå B moderat —
ta den bevisst, ikke som en hastesak.
