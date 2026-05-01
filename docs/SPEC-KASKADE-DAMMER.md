# Spec: Kaskade-dammer og terminal-dam-overløp

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 1-2 dager (kjernemodell + Haukland-mapping + UI per dam)
**Avhengighet:** Eksisterende vakt-ROI med overløp (Drivdal) — `docs/SPEC-VAKT-ROI-OVERLOP.md`
**Trigger:** Haukland SCADA-eksport mottatt 2026-04-30 (195 tags, 4 dammer i kaskade)

## Bakgrunn

Vakt-ROI med overløps-justering ble levert 2026-04-29 og fungerer for Drivdal. Modellen forutsetter implisitt **én dam per anlegg** — `OverflowQueryService` ser etter alle signaler med rolle `OverflowFlow` for et plant-id og tolker hver av dem som "magasinet er fullt".

Når Haukland SCADA-eksport kom inn viste det seg at:

1. **5 av 11 anlegg er kaskade-anlegg:** Haukland (4 dammer), Lindland, Honnefoss, Liavatn, Øgreyfoss
2. **6 av 11 er én-dam-anlegg:** Drivdal, Logjen, Grødemfoss, Ørsdalen, Vikeså, Stølskraft

For kaskade-anlegg har hver dam sin egen `<DAM>_KONTROLL_MAG_OVLOP_PV`-tag. Dagens modell ville plukket opp alle fire og summert overløpet på tvers — feil tolkning.

**Drifts-leders korreksjon (2026-04-30):** *"Overløpet er kun viktig for siste dam før kraftverket. Det er kun når vi har overløp på Stemmevatn at det koster penger."*

Logikken: overløp på øvre dammer renner videre ned i kaskaden og kan fanges av neste dam. Først når den siste dam i kaskaden flommer er vannet definitivt tapt — det renner forbi turbinen og ut i elva.

## Beslutning

**Innfør eksplisitt `Dam`-entity per anlegg med markør for terminal-dam.** SignalMap utvides med nullable `DamId` slik at hver tag kan knyttes til riktig dam. OverflowQueryService oppdateres til å bare se på OverflowFlow-tags som tilhører dam-en med `IsTurbineIntake = true`.

For én-dam-anlegg: én `Dam` med `IsTurbineIntake = true`. Eksisterende mapping er baklengs-kompatibel — ingen endring i atferd for Drivdal.

## Datamodell

### Ny tabell `core.dams`

```sql
CREATE TABLE core.dams (
    plant_id VARCHAR(64) NOT NULL,
    dam_id VARCHAR(64) NOT NULL,
    name VARCHAR(128) NOT NULL,
    cascade_position INTEGER NOT NULL,
    is_turbine_intake BOOLEAN NOT NULL,
    hrv_moh DOUBLE PRECISION,
    lrv_moh DOUBLE PRECISION,
    volume_mm3 DOUBLE PRECISION,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (plant_id, dam_id),
    FOREIGN KEY (plant_id) REFERENCES core.plants(plant_id),
    CONSTRAINT one_intake_per_plant UNIQUE (plant_id, is_turbine_intake) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_dams_plant ON core.dams(plant_id);
```

`cascade_position`: 1 = øverste i kaskaden, høyere tall = lenger ned. Brukes til UI-visualisering.

`one_intake_per_plant`: deferrable unique constraint slik at vi kan flytte `IsTurbineIntake` mellom dammer i én transaksjon (f.eks. ved omkonfigurering).

### Utvidelse av `core.signal_map`

```sql
ALTER TABLE core.signal_map
    ADD COLUMN dam_id VARCHAR(64) NULL;

ALTER TABLE core.signal_map
    ADD CONSTRAINT fk_signal_map_dam
    FOREIGN KEY (plant_id, dam_id) REFERENCES core.dams(plant_id, dam_id);

CREATE INDEX ix_signal_map_dam ON core.signal_map(plant_id, dam_id, role);
```

Backfill-strategi for migrering:

1. For hvert eksisterende plant: opprett én `Dam` med `dam_id = '<plant_id>_main'`, `name = '<Plant-navn>'`, `cascade_position = 1`, `is_turbine_intake = true`
2. UPDATE `core.signal_map` SET `dam_id = '<plant_id>_main'` WHERE `plant_id = '<plant_id>'` for de roller som er dam-knyttet (`OverflowFlow`, `UpstreamLevel`, `DownstreamLevel`, `ReservoirFillFactor`, `LowestRegulatedLevel`)
3. Generator-relaterte roller (`GeneratorActivePower`, `TurbineWaterFlow`, `GuideVanePosition` osv.) har `dam_id = NULL` — de er per-generator, ikke per-dam

### C#-modeller

**Fil:** `src/KraftverkUptime.Core/Domain/Dam.cs` NY

```csharp
namespace KraftverkUptime.Core.Domain;

/// <summary>
/// En dam i et anleggs kaskade. Hvert anlegg har minst én dam med
/// IsTurbineIntake = true (den siste før turbinen). Kaskade-anlegg
/// har flere dammer der øvre dammer mater inn til lavere via lukeflyt.
///
/// Overløp på terminal-dam er den eneste som koster produksjon — overløp
/// på øvre dammer renner videre ned og kan fanges senere.
/// </summary>
public sealed record Dam(
    string PlantId,
    string DamId,
    string Name,
    int CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3);
```

**Fil:** `src/KraftverkUptime.Core/Domain/SignalMap.cs`

Utvid eksisterende record med ett nullable felt:

```csharp
public sealed record SignalMap(
    string PlantId,
    string SignalId,
    string CsvColumn,
    string Unit,
    SignalRole Role,
    bool StoreSamples,
    bool IsActive,
    string? DamId = null);   // NYTT — null for generator-relaterte roller
```

## Endringer i kodebasen

### 1. Dam-entity og repository

- `src/KraftverkUptime.Core/Domain/Dam.cs` — record (over)
- `src/KraftverkUptime.Infrastructure/Persistence/Entities/DamEntry.cs` — EF entity
- `src/KraftverkUptime.Core/Configuration/IPlantConfiguration.cs` — utvid med `Task<IReadOnlyList<Dam>> GetDamsAsync(string plantId, CancellationToken ct)` og `Task<Dam?> GetTerminalDamAsync(string plantId, CancellationToken ct)`
- `src/KraftverkUptime.Infrastructure/Configuration/DbPlantConfiguration.cs` — implementer

### 2. Migrering med backfill

- `src/KraftverkUptime.Infrastructure/Persistence/Migrations/<timestamp>_AddDams.cs` NY
- Inkluder backfill-SQL i `Up()` slik at alle 11 eksisterende anlegg får én default-dam

### 3. OverflowQueryService — terminal-dam-filter

**Fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/OverflowQueryService.cs`

Endring i `GetOverflowDatasetAsync`:

```csharp
// FØR: hentet alle OverflowFlow-tags for plantet
var signals = await _signalMaps.GetByPlantAndRoleAsync(plantId, SignalRole.OverflowFlow, ct);

// ETTER: bare overflow-tag knyttet til terminal-dam
var terminalDam = await _plantConfig.GetTerminalDamAsync(plantId, ct);
if (terminalDam is null)
{
    _logger.LogWarning("Plant {PlantId} har ingen dam med IsTurbineIntake=true. OverflowDataset returnerer DataAvailable=false.", plantId);
    return OverflowDataset.NoData();
}

var signals = await _signalMaps.GetByPlantDamAndRoleAsync(
    plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct);
```

Logikken for terskel (0.001 m³/s) og time-trunkering bevares uendret. Kun tag-utvelgelsen endres.

### 4. Signal-seedere — utvid med DamId

For hver eksisterende seeder under `src/KraftverkUptime.Infrastructure/Persistence/`:
- `DrivdalSignalMapSeeder.cs` — sett `DamId = "drivdal_main"` på alle dam-relaterte tags
- Lignende for andre én-dam-anlegg når deres seedere lages

For Haukland — ny seeder med 4 dammer (mapping-tabell under).

### 5. Plant-admin UI utvidelse

**Fil:** `src/KraftverkUptime.Web/Pages/PlantAdmin.razor`

Legg til "Dammer"-seksjon under generelle plant-felt:
- Tabell med dam-id, navn, cascade_position, is_turbine_intake (radio-button per anlegg slik at constraint sikres), HRV/LRV/Volum
- Knapp "Legg til dam" som åpner modal
- Validering: minst én dam må være terminal-dam før save

### 6. Magasinstand-widget per dam (nytt)

**Fil:** `src/KraftverkUptime.Web/Pages/MagasinStatus.razor` NY (lavere prioritet, kan deferres)
**Rute:** `/magasin/{plantId}`

Viser kaskade-diagram tilsvarende SCADA-skjermen brukeren delte: dammer som trapeser, koblet med rør, med nivå-/fyllgrad-/flow-bokser ved siden av. Ren visualisering — bruker eksisterende sample-data fra `core.scada_samples`.

## Haukland mapping-tabell (referanse-implementasjon)

### Dammer

| dam_id | name | cascade_position | is_turbine_intake | HRV (moh) | LRV (moh) | Volum (Mill.m³) |
|---|---|---|---|---|---|---|
| `haukland_stolsvt` | Stølsvatn | 1 | false | (krever bruker-input) | – | – |
| `haukland_gjelevt` | Gjelevatn | 1 | false | (krever bruker-input) | – | – |
| `haukland_skrstmvt` | Skårstemmevatn | 2 | false | (krever bruker-input) | – | – |
| `haukland_stemmevt` | Stemmevatn | 3 | **true** | (krever bruker-input) | – | – |

Stølsvatn og Gjelevatn er begge på kaskade-posisjon 1 (parallelle øvre magasiner). Skårstemmevatn (2) mottar fra Gjelevatn. Stemmevatn (3) mottar fra Stølsvatn og Skårstemmevatn, og er inntak til turbin G1.

**BRUKER-INPUT KREVES:** HRV/LRV/Volum per dam. Hentes fra konsesjonsdokumenter eller drifts-håndbok. Kan settes via `/plants/haukland/admin` etter at modellen er på plass.

### Signaler — anbefalt mapping (utdrag av 195 tags)

Generator (DamId = NULL):

| Tag | Role | Unit |
|---|---|---|
| `HAUKLAND_G1_GEN_P_PV` | GeneratorActivePower | kW |
| `HAUKLAND_G1_GEN_TURTALL_PV` | GeneratorRpm | rpm |
| `HAUKLAND_G1_GEN_F_PV` | GeneratorFrequency | Hz |
| `HAUKLAND_G1_TURB_VF_PV` | TurbineWaterFlow | m³/s |
| `HAUKLAND_G1_TURB_VIRKNGRD_PV` | TurbineEfficiency | % |
| `HAUKLAND_G1_TURB_LEDEAPP_POS_PV` | GuideVanePosition | % |
| `HAUKLAND_G1_KONTROLL_TURBINREG_PADRAG_SP_PV` | TurbinePadrag | % |
| `HAUKLAND_G1_TURB_VANN_TRYKK_PV` | HydraulicPressure | mvs |

Per dam (én rad per dam × suffix):

| Suffix-mønster | Role | Unit |
|---|---|---|
| `<DAM>_NIVA_MOH_PV` | UpstreamLevel | moh |
| `<DAM>_KONTROLL_MAG_FYLLGRD_PV` | ReservoirFillFactor | % |
| `<DAM>_KONTROLL_MAG_OVLOP_PV` | OverflowFlow | m³/s |
| `<DAM>_LUKE1_VF_PV` | (ny rolle: `GateFlow`) | m³/s |
| `<DAM>_KONTROLL_TOT_VF_PV` | (ny rolle: `TotalDamFlow`) | m³/s |
| `<DAM>_KONTROLL_MAG_VOLUM_PV` | (ny rolle: `ReservoirVolume`) | Mill.m³ |
| `<DAM>_LUKE1_POS_PV` | (ny rolle: `GatePosition`) | cm |

Stemmevatn har ikke `LUKE1_VF_PV`/`LUKE1_POS_PV` (vannet går rett til turbin) — bekreftet av bruker.

Kommunikasjons-alarmer (DamId = sted hvis dam-tilhørende, ellers NULL):

| Tag | Role | DamId |
|---|---|---|
| `HAUKLAND_STOLSVT_KONTROLL_KOM_AL` | CommunicationAlarm | haukland_stolsvt |
| `HAUKLAND_GJELEVT_KONTROLL_KOM_AL` | CommunicationAlarm | haukland_gjelevt |
| `HAUKLAND_SKRSTMVT_KONTROLL_KOM_AL` | CommunicationAlarm | haukland_skrstmvt |
| `HAUKLAND_STEMMEVT_KONTROLL_KOM_AL` | CommunicationAlarm | haukland_stemmevt |
| `HAUKLAND_KRST_KONTROLL_KOM_AL` | CommunicationAlarm | NULL (sentralanlegg) |

Resterende signaler (lager-temp, kjølevann, met-stasjon, hjelpekraft osv.): Role = `Other` eller `ConditionTemperature`, StoreSamples = false som standard. Aktiveres ved behov senere.

### Nye SignalRole-verdier

Legg til i `SignalRole`-enum:

```csharp
/// <summary>Vannføring gjennom dam-luke (m³/s).</summary>
GateFlow,

/// <summary>Lukens åpningsposisjon (cm eller %).</summary>
GatePosition,

/// <summary>Total vannføring ut av dam (m³/s) — sum av luke + overløp + turbin.</summary>
TotalDamFlow,

/// <summary>Magasinvolum (Mill.m³).</summary>
ReservoirVolume,
```

### Genereringsstrategi for full mapping

I stedet for å hardkode 195 rader manuelt: implementer en `HauklandSignalMapSeeder` som leser CSV-headeren ved første kjøring, parser tag-suffix og dam-prefix, og auto-mapper basert på regelsett:

```csharp
private static (SignalRole role, string? damId) MapTag(string tagId)
{
    // Generator: HAUKLAND_G1_*
    if (tagId.StartsWith("HAUKLAND_G1_GEN_P_PV")) return (GeneratorActivePower, null);
    if (tagId.StartsWith("HAUKLAND_G1_GEN_TURTALL")) return (GeneratorRpm, null);
    // ... osv. for generator

    // Dam-prefix-deteksjon
    var damId = ExtractDamId(tagId);  // "haukland_stolsvt", "haukland_gjelevt", ...
    if (damId is null) return (Other, null);

    // Suffix-basert rolle for dam-tags
    if (tagId.EndsWith("_NIVA_MOH_PV")) return (UpstreamLevel, damId);
    if (tagId.EndsWith("_KONTROLL_MAG_FYLLGRD_PV")) return (ReservoirFillFactor, damId);
    if (tagId.EndsWith("_KONTROLL_MAG_OVLOP_PV")) return (OverflowFlow, damId);
    if (tagId.EndsWith("_LUKE1_VF_PV")) return (GateFlow, damId);
    // ... osv.

    return (Other, damId);
}
```

Dette gir samme mapping deterministisk og er testbart. Manuelle overstyringer for spesielle tilfeller kan legges som override-tabell i seederen.

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt
2. `core.dams`-tabell opprettes med backfill — alle 11 anlegg har minst én dam etter migrering
3. Drivdal vakt-ROI: ingen endring i tall før/etter migrering. Run regresjons-curl mot feb-2026 og verifiser identiske `reddet_nok`-verdier.
4. Haukland: importer SCADA-eksport. `core.signal_map` får 195 rader med korrekt `DamId`-mapping for dam-relaterte tags og `DamId = NULL` for generator-tags.
5. `GET /api/v1/plants/haukland/vakt-roi?from=...&to=...` bruker bare `STEMMEVT_KONTROLL_MAG_OVLOP_PV` for overløps-sjekk — ikke de andre tre dammene.
6. Endring av `IsTurbineIntake` via PlantAdmin-UI: kan flyttes mellom dammer, constraint forhindrer null eller multiple.

### Datakvalitet

7. Hvis et anlegg mangler dam med `IsTurbineIntake = true` (skal ikke kunne skje etter backfill, men beskyttelse mot manuell DB-edit): OverflowQueryService returnerer `DataAvailable = false` med tydelig logging.
8. Hvis Haukland's terminal-dam (Stemmevatn) får 0 OVLOP-samples i en periode: `OverflowDataMissing = true` på vakt-ROI-events.
9. Migreringen er idempotent — kjører på nytt uten å duplisere dammer eller endre eksisterende `IsTurbineIntake`.

### Test-dekning

10. `OverflowQueryServiceTests`: 3 nye tester
    - Kaskade-anlegg med overløp på terminal-dam → telles
    - Kaskade-anlegg med overløp på øvre dam (ikke terminal) → ignoreres
    - Anlegg uten dam med IsTurbineIntake=true → DataAvailable=false

11. `HauklandSignalMapSeederTests`: 13 tester
    - Generator-tags får DamId = null
    - Hver av 4 dammer får riktig sett dam-tags
    - Stemmevatn har overflow + ikke gate-flow
    - Stølsvatn/Gjelevatn/Skårstemmevatn har både overflow + gate-flow
    - Tags uten kjent suffix → role = Other, store_samples = false
    - Idempotent — kjører to ganger uten duplikater

12. `DamRepositoryTests`: 5 tester
    - Hent alle dammer for plant
    - Hent terminal-dam (returnerer eneste IsTurbineIntake=true)
    - Constraint: kan ikke ha to terminal-dammer per plant
    - Kan flytte IsTurbineIntake mellom dammer i én transaksjon
    - Plant uten dammer → tom liste

13. End-to-end: importer Drivdal- og Haukland-data, kjør vakt-ROI for begge, verifiser at Drivdal-tall er identiske med pre-migrering og at Haukland-tall reflekterer kun Stemmevatn-overløp.

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `Dam` entity + `core.dams`-tabell + migrering med backfill | 2-3 t |
| 2 | `SignalMap.DamId` utvidelse + migrering | 1 t |
| 3 | Nye `SignalRole`-verdier (GateFlow, GatePosition, TotalDamFlow, ReservoirVolume) | 30 min |
| 4 | `IPlantConfiguration.GetTerminalDamAsync` + DB-implementasjon + tester | 1 t |
| 5 | `OverflowQueryService` oppdatering + Drivdal-regresjonstest | 1-2 t |
| 6 | `HauklandSignalMapSeeder` med auto-mapping fra CSV-header + 13 tester | 4 t |
| 7 | Drag-drop import av Haukland SCADA-eksport — verifiser at signal-map fylles riktig | 1 t |
| 8 | Haukland vakt-ROI smoke-test (krever at brukerens fyller HRV/LRV via admin-UI) | 1 t |
| 9 | PlantAdmin-UI utvidelse for dammer | 2-3 t |
| 10 | (valgfri) Magasinstand-widget per dam | 3-4 t |

**Stopp etter steg 5** og verifiser at Drivdal-tallene er identiske før/etter migrering. Dette beskytter mot regresjon i live data.

**Stopp etter steg 7** og verifiser at Haukland har 195 signal-map-rader med korrekt DamId-fordeling før vakt-ROI-test.

Estimat totalt: 1.5-2 dager for steg 1-9. Steg 10 (visualisering) er bonus og kan deferres.

## Antakelser

1. **Drivdal er én-dam-anlegg.** Bekreftet via inspeksjon av `DrivdalSignalMapSeeder` — kun én OVERLOP-tag (`DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV`).
2. **5 kaskade-anlegg, 6 én-dam-anlegg** (bekreftet av bruker 2026-04-30):
   - Kaskade: Haukland, Lindland, Honnefoss, Liavatn, Øgreyfoss
   - Én-dam: Drivdal, Logjen, Grødemfoss, Ørsdalen, Vikeså, Stølskraft
3. **Haukland Stemmevatn er terminal-dam** — bekreftet via SCADA-skjerm og at den mangler `LUKE1_VF_PV`-tag (vann går rett til turbin).
4. **Lindland/Honnefoss/Liavatn/Øgreyfoss** krever individuell mapping når deres SCADA-eksport kommer. Spec-en støtter dem generisk via samme seeder-mønster.
5. **`SCADA_eksport_referanse.md` er ikke oppdatert** for kaskade — bør oppdateres etter implementasjon for fremtidige seedere.

## Ut-av-scope for v1

- Vannverdi-modell per dam (alternativkost basert på magasinstand)
- Kaskade-flyt-balanse (Total inn = Total ut + ΔVolum) — krever met-stasjon-tilsig + sammenligning med målt flyt
- Multi-generator per anlegg (alle 11 har én G1)
- Auto-deteksjon av kaskade-topologi fra tag-prefix (manuell konfig per anlegg er enklere og sikrere)

## Verifikasjon

```powershell
# 1. Migrering med backfill
cd src\KraftverkUptime.Api
dotnet ef database update

# 2. Verifiser alle 11 anlegg har minst én dam
psql -d kraftverkuptime -c "
SELECT p.plant_id, COUNT(d.dam_id) AS antall_dammer,
       SUM(CASE WHEN d.is_turbine_intake THEN 1 ELSE 0 END) AS antall_terminal
FROM core.plants p
LEFT JOIN core.dams d ON p.plant_id = d.plant_id
GROUP BY p.plant_id
ORDER BY p.plant_id;
"
# Forventet: 11 rader, alle med antall_dammer >= 1, antall_terminal = 1

# 3. Drivdal regresjon — vakt-ROI feb-2026 før/etter
curl.exe "http://localhost:5080/api/v1/plants/drivdal/vakt-roi?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z"
# Forventet: identiske tall som lagret i OVERLEVERING-2026-04-29-VEIKART.md (10 696 NOK reddet, 13 events)

# 4. Drag-drop Haukland SCADA-eksport via /plants/haukland
# Verifiser via:
psql -d kraftverkuptime -c "
SELECT dam_id, role, COUNT(*) FROM core.signal_map
WHERE plant_id = 'haukland'
GROUP BY dam_id, role
ORDER BY dam_id, role;
"
# Forventet: 4 dam-grupperinger + null-gruppe (generator), 195 totalt

# 5. Haukland vakt-ROI etter at HRV/LRV er satt via admin
curl.exe "http://localhost:5080/api/v1/plants/haukland/vakt-roi?from=2026-03-01T00:00:00Z&to=2026-04-01T00:00:00Z"
# Forventet: bruker bare STEMMEVT-overflow (verifiser i logs)
```

## Referanser

- Drifts-leders korreksjon (2026-04-30): "Overløpet er kun viktig for siste dam før kraftverket"
- Haukland SCADA-eksport: `uploads/export-195-tags-avg-hour-20260430-140353_MASTER.csv`
- Haukland SCADA-skjerm: viser 4 dammer i kaskade, magasinert energi 1974 MWh
- Eksisterende vakt-ROI: `docs/SPEC-VAKT-ROI-OVERLOP.md` + `OVERLEVERING-2026-04-29-OVERLOP.md`
- SignalMap-struktur: `src/KraftverkUptime.Core/Domain/SignalMap.cs`
