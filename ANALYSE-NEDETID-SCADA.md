# Forbedret nedetids-klassifisering med SCADA-data

Dato: 2026-04-28
Status: Spec — ikke startet implementasjon
Posisjon: **Steg 1** før virkningsgrad-analysen (ANALYSE-VIRKNINGSGRAD.md)
Skala-mål: ~20 anlegg, server-deployment

## Hvorfor dette først

Dagens klassifikator (Phase A) bygger på settlement-proxy:

| Tilstand | Trigger i dag |
|---|---|
| `InService` | Elhub > 0 |
| `ForcedOutage` | Elhub = 0 OG Spotbud > 0 |
| `ReserveShutdown` | Elhub = 0 OG Spotbud = 0/null |
| `InformationUnavailable` | Elhub mangler/negativ |

Dette er en _grov_ proxy med systematiske feil:

- **Ingen sub-time-oppløsning** — en 24-minutters trip kl. 14:23-14:47
  forsvinner i timesnittet. Hele timen blir "delvis produsert", ikke
  "trippet 24 min".
- **ResourceUnavailable maskeres som ReserveShutdown** — vi ser ikke
  forskjellen på "valgte å ikke produsere pga lav spotpris" og "kunne
  ikke produsere pga vannmangel". Disse to skal regnes ulikt for
  AF (Availability Factor) — markedsstyrt stopp teller _med_, ressursmangel
  er _OMC (Outside Management Control)_ etter NERC GADS.
- **Ingen havari-/vedlikehold-skille** — alle stopp med markedsforpliktelse
  blir "ForcedOutage", uavhengig av om det var et planlagt vedlikehold
  som vart inn i en spotbud-time.
- **Ingen derating-deteksjon** — å produsere 50 % av kapasitet pga sliten
  turbin er like "drift" som full produksjon. Dette går mot IEEE 762.

SCADA fra Drivdal gir oss alt vi trenger for å fikse alle fire problemene
**uten å endre KPI-katalog-format eller dashboard-strukturen**. Eksisterende
KPI-er bare blir mer presise; nye KPI-er som krever event-data
(MTBF, MTTR, derating-timer) blir mulige.

Som bonus: når dette er på plass, har virkningsgrad-analysen et solid
grunnlag — vi vet med sikkerhet hvilke timer som er ren drift (riktig
sample for η-kurver) vs. stopp (skal ekskluderes).

## Hva SCADA gir oss

Av de 39 whitelisted tags fra `ANALYSE-VIRKNINGSGRAD.md`, er disse
direkte relevante for nedetids-klassifisering:

| Tag | Rolle i klassifisering |
|---|---|
| `G1_GEN_TURTALL_PV` (rpm) | Roterende = drift; 0 rpm = stopp |
| `G1_GEN_F_PV` (Hz) | Synkron drift bekreftes med 50 Hz |
| `G1_GEN_P_PV` (kW) | Aktiv effekt — hovedsignal |
| `G1_TURB_LEDEAPP_POS_PV` (%) | Posisjon > 0 = forsøker å levere |
| `G1_HYDRL_OLJE_TRYKK_PV` (bar) | Lavt = hydraulikk-feil |
| `INNTAK_NIVA_OPPSTROM_KOTE_PV` (moh) | Magasin-status |
| `INNTAK_MAGASIN_FYLLGRAD_PV` (%) | Andel av HRV |
| `INNTAK_MAGASIN_NED_KAP_PV` (moh) | LRV-grense |
| `KRST_KONTROLL_KOM_AL` | Kommunikasjons-alarm = data hull |

Pluss alle event-baserte tags fra **operlog**:
`STARTER_AL`, `STOPPER_AL`, `FEIL_AL`, `AGC_AKTIV`, manuelle endringer
i settpunkter.

## Forbedrede klassifiserings-regler

Klassifikatoren skal nå produsere alle 9 `UnitState`-verdier (vs. 4 i dag).
Reglene evalueres i prioritert rekkefølge per time (eller per minutt om
oppløsning tillater):

```
1. KRST_KONTROLL_KOM_AL aktiv?
       → InformationUnavailable (datahull)

2. TURTALL ≥ 0.95 × nominell  AND  GEN_F ≈ 50 ± 0.2 Hz?
   2a. P ≥ 0.95 × P_forventet(Q, H)?            → InService
   2b. 0.50 × P_forventet ≤ P < 0.95 × P_forventet?
        – Hvis derating-event i operlog               → PlannedDerating
        – Ellers (uvarslet redusert ytelse)          → ForcedDerating
   2c. P < 0.50 × P_forventet (sterk derating)
        – Operlog-flag: vedlikehold/test → PlannedDerating
        – Ellers                                       → ForcedDerating

3. TURTALL ≈ 0  (anlegget står)?
   3a. INNTAK_NIVA_OPPSTROM_KOTE ≤ MAGASIN_NED_KAP + 0.05 m?
        → ResourceUnavailable (vannmangel)
   3b. Operlog: STARTER_AL eller STOPPER_AL nylig?
        – Med varsel ≥ 4 uker (manuell registrering)   → PlannedOutage
        – Med varsel <  4 uker                         → MaintenanceOutage
        – Akutt (FEIL_AL eller alarm-stopp)            → ForcedOutage
   3c. Spotbud_t > 0?
        → ForcedOutage (forpliktet, men leverer ikke, ingen klar årsak)
   3d. Spotbud_t = 0 AND ingen alarm AND magasin OK?
        → ReserveShutdown (markedsstyrt stopp)

4. Ellers (kant-tilfeller)
   → ReserveShutdown (default safe)
```

`P_forventet(Q, H)` er forventet effekt fra η-kurven kalibrert per anlegg.
Initialt: `P_forventet = ρ · g · Q · H_netto · η_referanse` med η_referanse
fra fabrikat-data (f.eks. 0.92 for Drivdal Francis-turbin).

## Sub-time-oppløsning

SCADA gir typisk 1-min eller 1-sek samples. Klassifikatoren bruker
**1-min-oppløsning** internt, ekspandert opp til 1-time for KPI-aggregat:

```csharp
// Per minutt: klassifiser TURTALL, P, etc. til UnitState
// Per time:   ta dominant state, men samle ALT som event-record:
//   { hour: 14:00, dominantState: InService, transitions: [
//       { 14:23: InService → ForcedOutage, cause: "FEIL_AL fired" },
//       { 14:47: ForcedOutage → InService, cause: "STARTER_AL completed" }
//   ] }
```

Effekten:
- KPI-tellere blir time-basert som før (kompatibilitet)
- Event-listen sporer trip-hendelser presist (nye KPI-er)
- Heatmap-tidslinjen kan vise sub-time-detaljer ved zoom

## Event-deteksjon for nye KPI-er

Med sub-time-oppløsning kan vi telle hendelser, ikke bare timer:

### Trip / utløst stopp

```
Trip = (state[t-1] = InService OR ForcedDerating)
       AND (state[t] = ForcedOutage)
       AND (årsak = uvarslet, dvs. ikke matchet av operlog-stopp)
```

Hver trip er én event-rad: timestamp, anlegg, varighet, årsak (FEIL_AL-tag),
operlog-link.

### Mean Time Between Failures (MTBF)

```
MTBF = Σ ServiceTimer over måneden / antall trip-events
```

Lengre = mer pålitelig.

### Mean Time To Repair (MTTR)

```
MTTR = Σ varigheter av trip-events / antall trip-events
```

Kortere = raskere gjenoppretting.

### Forced Outage Rate (FOR)

```
FOR = ForcedOutageHours / (ServiceHours + ForcedOutageHours)
```

NERC-standard. Erstatter den litt løsere AvailabilityFactor.

### Derating-tap

```
DeratingTap_MWh = Σ (P_forventet,t − P_faktisk,t) × Δt   for ForcedDerating-timer
DeratingTap_NOK = DeratingTap_MWh × spotpris
```

Tallfester kostnaden av å kjøre med redusert kapasitet.

## Fusion-logikk: SCADA + Settlement + Annoteringer

I dag: én kilde (settlement) → én tilstand per time.
Etter: tre kilder med ulik confidence og rolle.

```
Per time:
  1. SCADA klassifiserer (confidence 0.95-0.99)
  2. Settlement validerer (confidence 0.5-0.7)
     - Stort avvik mellom SCADA-P og Elhub-MWh? → Flag for review
  3. Manuelle annoteringer overstyrer (confidence 1.0)
     - Annotering med kategori "scheduled_revision"
       overstyrer SCADA-vurderingen "ForcedOutage" til "PlannedOutage"
```

Rekkefølge er viktig: SCADA er primary, settlement er sanity-check,
annoteringer er final-word fra operatør. Eksisterende
`AnnotationOverlayService` håndterer trinn 3 allerede; vi legger til
trinn 1 og 2 i klassifikator-modulen.

Når kilder er uenige, lagres uenigheten som event-rad:
```
{ hour: 14:00,
  scada_state: ForcedOutage,
  settlement_state: InService,        // Elhub > 0 i timesnittet, men SCADA så trip
  resolution: ForcedOutage,           // SCADA vinner
  flag: "Elhub-snitt viser produksjon, men SCADA registrerte 24-min trip" }
```

Disse flags vises som varsler i UI ("Avvik mellom SCADA og settlement —
sjekk timen").

## Operlog som annoterings-kilde

`operlog`-CSV gir oss menneske-utløste hendelser. 222 events i februar
for Drivdal alene. Vi mapper:

| Operlog-event | Auto-tilstand-overstyring | Kategori |
|---|---|---|
| `STARTER_AL` (start-sekvens) | InformationUnavailable → InService | (intet — auto) |
| `STOPPER_AL` med ≥ 4 ukers varsel | * → PlannedOutage | scheduled_revision |
| `STOPPER_AL` < 4 ukers varsel | * → MaintenanceOutage | scheduled_service |
| `FEIL_AL` med category 3 | * → ForcedOutage | fault |
| Manuell `LEDEAPP_POS = 0` | * → ReserveShutdown | (intet — auto) |
| `AGC_AKTIV` toggling | (annoter, ingen state-endring) | comment-only |
| `REG_P_SP_SP_LAST` endring | (annoter, ingen state-endring) | comment-only |

Ny worker-jobb `ImportOperlogJob` leser CSV-en, mapper til annoteringer
via lookup-tabell `core.operlog_to_annotation`, og soft-skipper duplikater.

Resultat: annoterings-tidslinjen fylles automatisk fra operlog. Operatøren
trenger bare å justere kategori om automatikken har gjettet feil
(f.eks. om en stopp registrert som "MaintenanceOutage" i virkeligheten
var "ForcedOutage").

## KPI-konsekvenser

Eksisterende KPI-er får mer presise verdier. Nye KPI-er blir mulige:

### Eksisterende, forbedret presisjon

- `ServiceHours_SH` — basert på TURTALL > nominell, ikke Elhub-snitt
- `ForcedOutageHours_FOH` — kun når årsak er uvarslet/havari
- `OutOfServiceHours` — splitt opp:
  - `ReserveShutdownHours` (markedsstyrt)
  - `ResourceUnavailableHours` (vannmangel — OMC)
- `AvailabilityFactor_AF` — utelukker ResourceUnavailable fra nevneren
  (NERC-konformt)

### Nye KPI-er aktivert av SCADA

- `ForcedOutageEvents` — antall trip-hendelser
- `MTBF_hours` — gjennomsnittlig tid mellom feil
- `MTTR_hours` — gjennomsnittlig reparasjons-tid
- `ForcedDeratingHours_FDH`
- `PlannedDeratingHours_PDH`
- `DeratingTap_NOK`
- `EAF` (Equivalent Availability Factor) — IEEE 762
- `EFOR` (Equivalent Forced Outage Rate) — IEEE 762
- `ScadaSettlementAvvik_count` — kvalitetsmål; jo lavere desto bedre
  data-konsistens

Disse KPI-ene legges i en ny "palitelighet"-kategori i KPI-katalogen.
Drift / marked / økonomi / palitelighet → 4 grupper i KPI-vinduet.

## Klassifikator-arkitektur

### Eksisterende lag (Phase A)

```
SettlementClassifier
   ├── tar UptimePeriod (settlement-timer)
   └── produserer ClassifiedHourlyRow[]    -- 4 states i bruk
```

### Etter SCADA (denne planen)

```
ScadaClassifier
   ├── tar sample_facts_1min (SCADA-data per minutt per anlegg)
   ├── tar PlantClassificationConfig (per-anlegg terskler)
   ├── matcher operlog-events
   └── produserer ClassifiedPeriod[]       -- variabel lengde, 9 states

FusionClassifier
   ├── tar ClassifiedPeriod[] fra SCADA
   ├── tar UptimePeriod (settlement) for sanity-check
   ├── tar DowntimeAnnotation[] for overstyring
   ├── flagger uenigheter mellom SCADA og settlement
   └── produserer endelig ClassifiedHourlyRow[] + EventLog[]
```

`FusionClassifier` returnerer i samme format som
`SettlementClassifier` i dag — slik at KPI-katalog, dashboards og
eksisterende API forblir uendret. Det er kun innsiden av den
"kasse" som blir bedre.

For anlegg uten SCADA tilgjengelig faller vi tilbake til
`SettlementClassifier` (dagens flyt). Ingen funksjonelt regress for
ikke-SCADA-anlegg.

## Multi-anleggs / multi-bruker / server-hensyn

Klassifiserings-jobben kjører i Worker som scheduled task:
- Per anlegg per time: les sample_facts → klassifiser → skriv
  ClassifiedHourlyRow + EventLog → trigger KPI-recompute
- Trigger ved ny SCADA-import eller ved ny annotering
- Skala: 20 anlegg × 720 timer/mnd × ~5 ms compute = 70 sekunder per
  månedlig backfill. Triviell.

Multi-bruker: KPI-resultatene er per-anleggs-immutable (gitt en
import-id). Cache friendly — `Cache-Control: max-age=3600`. Read-replica
av Postgres når > 50 samtidige brukere.

EventLog-tabellen får én rad per state-endring:

```sql
CREATE TABLE core.classified_events (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    plant_id varchar(64) NOT NULL,
    owner_org_id varchar(64) NOT NULL,
    start_utc timestamptz NOT NULL,
    end_utc timestamptz NOT NULL,
    state varchar(32) NOT NULL,
    cause_code varchar(64) NULL,
    confidence double precision,
    sources jsonb,
    rationale text
);
SELECT create_hypertable('core.classified_events', 'start_utc',
    chunk_time_interval => INTERVAL '30 days');
CREATE INDEX ix_classified_events_plant_period ON core.classified_events
    (plant_id, start_utc, end_utc);
```

20 anlegg × ~50 events/mnd × 5 år = 60 000 rader. Triviell volume,
men hypertable-formen gjør den klar for høyere oppløsning senere
(per-minutt events for høyfrekvente trip-detection).

## Implementasjonsrekkefølge

| # | Steg | Avhengigheter | Estimat |
|---|---|---|---|
| 1 | TimescaleDB foundation + sample_facts (fra ARKITEKTUR-SCADA.md) | – | 1 dag |
| 2 | signal_map per anlegg + SCADA-CSV-import-pipeline | 1 | 1 dag |
| 3 | Operlog-import-pipeline + auto-annotering | 1, 2 | 1 dag |
| 4 | `ScadaClassifier` med 9-state-regler | 1, 2 | 2 dager |
| 5 | `FusionClassifier` (SCADA + settlement + annoteringer) | 4 | 1 dag |
| 6 | `classified_events`-tabell + EventLog-skriving | 4, 5 | 4 t |
| 7 | Nye event-baserte KPI-er (MTBF, MTTR, FOR, EAF) | 5, 6 | 1 dag |
| 8 | Avvik-flag i UI ("SCADA vs settlement-divergens") | 5, 6 | 4 t |
| 9 | Sub-time-zoom i driftstidslinjen for trip-events | 6 | 1 dag |

Totalt: ~9 arbeidsdager. Steg 1-3 er foundation (deles med
virkningsgrad-prosjektet). Steg 4-9 er nedetidsspesifikt.

Etter steg 7 har vi:
- 9-state klassifisering presis ned til 1-min-oppløsning
- Event-baserte KPI-er som NERC/IEEE krever
- Auto-utfylling av tidslinjen via operlog
- Klart fundament for virkningsgrad-analyse (steg 2-spec)

## Risiko og åpne spørsmål

1. **Kalibrering av P_forventet(Q, H).** Krever fabrikat-η-kurve for
   hver turbin-modell. Initial tilnærming: bruk η_referanse fra
   fabrikat-datablad (Drivdal Francis ≈ 0.92 ved nominell). Andre
   anlegg trenger tilsvarende verdier i `PlantClassificationConfig`.

2. **Varsel-tids-grense for Planned vs Maintenance.** Spec sier 4 uker
   per IEEE 762. Operlog viser ikke alltid varsel-dato — kan kreve
   manuell admin-input. Default: anta MaintenanceOutage hvis varsel
   ikke kan utledes.

3. **SCADA-kommunikasjons-feil.** `KRST_KONTROLL_KOM_AL` aktiv ⇒
   InformationUnavailable. Hva med stille feil (tag stopper å oppdatere
   uten alarm)? Sjekk samples-eldre-enn-X-minutter som heuristikk.

4. **Trip-event-deduplikering.** En trip kan flagges av både `FEIL_AL`
   og `STOPPER_AL` på sekunder mellom seg. Dedupliseres ved
   import (samme tidspunkt ± 60 sek = samme event).

5. **SCADA-η og egen-η-konsistens** (samme som i ANALYSE-VIRKNINGSGRAD.md):
   SCADA `TURB_VIRKNGRD_PV` kan inkludere eller ikke inkludere
   generator-tap. Verifiseres mot fabrikat før vi setter
   "ForcedDerating"-terskler.

## Avhengighet til reell SCADA-data

Som med virkningsgrad-spec'et: vi kan bygge skjelettet (steg 1-3, 6)
parallelt med at du feilsøker dataeksporten. Klassifikator-reglene
(steg 4-5) trenger ekte data for å valideres mot kjente trip-hendelser
i historikken — uten det er det vanskelig å verifisere at
"FEIL_AL → ForcedOutage"-mappingen faktisk fungerer for et reelt
havari.

I mellomtiden:
- Foundation-skjelettet er klar
- Syntetisk testdata kan verifisere klassifikator-reglene matematisk
- Når data kommer: cherrypicke en kjent trip-hendelse fra februar
  (f.eks. fra operlog-CSV) og bekrefte at klassifikatoren korrekt
  identifiserer den
