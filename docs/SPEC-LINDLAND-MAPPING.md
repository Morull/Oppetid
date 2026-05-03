# Spec: Lindland SCADA-mapping + multi-generator-støtte

**Status:** Klar til implementasjon (2026-05-03, oppdatert med korrekt topology)
**Estimat:** 1 dag (multi-generator-utvidelse + Lindland-seeder)
**Avhengighet:** Kaskade-dammer-implementasjonen er ferdig (Dam-entity + DamId på SignalMap eksisterer)
**Trigger:** Lindland SCADA-eksport mottatt 2026-05-03 (117 tags)

## Bakgrunn

Lindland SCADA-eksport mottatt og analysert. Korrekt topology (bekreftet av drifts-leder 2026-05-03):

**Kaskade i serie (4 dammer):**

```
HEIGRAVATN  →  EIAVATN  →  BARSTADVATN (ikke regulert)  →  ROSSLANDSHØLEN (terminal, lite inntaksbasseng)  →  G1 + G2
```

SCADA-prefiksene mapper til dammene som følger:

| SCADA-prefix | Dam-navn | Posisjon | Regulert? |
|---|---|---|---|
| HEIGRAVT | Heigravatn | 1 (øverst) | Ja |
| EIAVT | Eiavatn | 2 | Ja |
| BARSTDVT | Barstadvatn | 3 | **Nei — fri vannflyt** |
| INNTAK | Rosslandshølen | 4 (terminal) | Ja (lite inntaksbasseng) |

Min tidligere antakelse om at BARSTDVT var en sensor-stasjon (kun 2 tags) var **feil**. Det er en uregulert dam i serien — derfor færre tags, fordi det ikke er noen luke-/styrings-signaler å rapportere. Bare nivå/kote.

**To viktige funn for implementasjonen:**

1. **Kaskade-modell er allerede levert** — bygger på eksisterende `Dam`-entity + DamId-felt på SignalMap. Ingen datamodell-arbeid kreves for selve kaskaden.

2. **Multi-generator (G1 + G2 som deler ROSSLANDSHØLEN)** er den reelle nyheten. Alle øvrige anlegg har bare G1. Lindland er **første multi-generator-anlegg** i porteføljen og krever ny `Generator`-entity + `signal_map.generator_id`.

## Beslutning

### Datamodell-utvidelse

**Ny entity `Generator`** (parallelt med `Dam`):

```csharp
public sealed record Generator(
    string PlantId,
    string GeneratorId,            // "lindland_g1", "lindland_g2", "drivdal_g1"
    string Name,                   // "G1", "G2"
    double InstalledCapacityMw,
    bool IsActive);
```

```sql
CREATE TABLE core.generators (
    plant_id VARCHAR(64) NOT NULL,
    generator_id VARCHAR(64) NOT NULL,
    name VARCHAR(64) NOT NULL,
    installed_capacity_mw DOUBLE PRECISION NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (plant_id, generator_id),
    FOREIGN KEY (plant_id) REFERENCES core.plants(plant_id)
);

CREATE INDEX ix_generators_plant ON core.generators(plant_id);
```

**SignalMap utvides med `GeneratorId`** (parallelt med `DamId` fra kaskade-spec):

```sql
ALTER TABLE core.signal_map
    ADD COLUMN generator_id VARCHAR(64) NULL;

ALTER TABLE core.signal_map
    ADD CONSTRAINT fk_signal_map_generator
    FOREIGN KEY (plant_id, generator_id) REFERENCES core.generators(plant_id, generator_id);

CREATE INDEX ix_signal_map_generator ON core.signal_map(plant_id, generator_id, role);
```

**Migrasjon-backfill:** alle eksisterende anlegg får én default-generator `<plant_id>_g1` med `installed_capacity_mw` fra `Plant.InstalledCapacityMw`. Eksisterende generator-relaterte SignalMap-rader oppdateres med `generator_id = '<plant_id>_g1'`. Baklengs-kompatibelt.

### KPI-aggregering med multi-generator

Anleggets totale KPI = sum over alle generatorer for produksjons-relaterte tall, weighted average for virkningsgrad:

```csharp
plant.totalProductionMwh = sum(generator.productionMwh for generator in plant.Generators)
plant.installedCapacityMw = sum(generator.InstalledCapacityMw for generator in plant.Generators)
plant.weightedEfficiency = sum(g.efficiency * g.productionMwh) / plant.totalProductionMwh
```

For dam-relaterte KPI-er (overløp, magasinstand): uendret — én terminal-dam per anlegg uavhengig av antall generatorer.

For vakt-ROI: `installertEffektMw` blir nå sum av alle generatorers kapasitet. Hvis bare én generator har feil mens den andre kjører, kompliseres event-tolkning (utenfor v1 — håndteres som "delvis nedetid" senere).

## Lindland-spesifikk mapping

### Dammer

| dam_id | name | cascade_position | is_turbine_intake | is_regulated | Kommentar |
|---|---|---|---|---|---|
| `lindland_heigravatn` | Heigravatn | 1 | false | true | Øverste magasin |
| `lindland_eiavatn` | Eiavatn | 2 | false | true | Magasin i serie under Heigravatn |
| `lindland_barstadvatn` | Barstadvatn | 3 | false | **false** | **Ikke regulert — fri vannflyt** |
| `lindland_rosslandshølen` | Rosslandshølen | 4 | **true** | true | Terminal-dam, lite inntaksbasseng som mater G1+G2 |

KRST har bare 2 tags (KOM_AL + utløpsnivå) — ren sensor-stasjon, ikke dam. Mappes med `DamId = NULL`.

**Ny utvidelse av `Dam`-entity:** legg til `IsRegulated`-felt for å markere uregulerte dammer i kaskaden:

```sql
ALTER TABLE core.dams ADD COLUMN is_regulated BOOLEAN NOT NULL DEFAULT true;
UPDATE core.dams SET is_regulated = false WHERE plant_id = 'lindland' AND dam_id = 'lindland_barstadvatn';
```

**Konsekvens for klassifikator:** uregulerte dammer i kaskaden bidrar til vannflyt men kan ikke styres aktivt. Overløp på en uregulert dam er ikke en drift-feil — det er hydrologisk uunngåelig når tilsiget overstiger kapasiteten oppstrøms. Dette skal flagges separat fra overløp på terminal-dam (Rosslandshølen), som fortsatt teller for vakt-ROI.

**BRUKER-INPUT KREVES:** HRV/LRV/Volum for de tre regulerte dammene fra konsesjonsdokumenter. Settes via PlantAdmin-UI.

### Generatorer

| generator_id | name | installed_capacity_mw |
|---|---|---|
| `lindland_g1` | G1 | (krever bruker-input) |
| `lindland_g2` | G2 | (krever bruker-input) |

Lindland totalt 32.5 MNOK omsetning 2025 / 42 144 MWh produksjon. Ut fra SCADA-data (G1 mean 1988 kW, G2 mean 4457 kW) ser G2 ut til å være ~2x G1.

**BRUKER-INPUT KREVES:** installert effekt per generator. Settes via PlantAdmin-UI som må utvides med generator-tabell.

### Auto-mapping seeder for Lindland

Som for Haukland: implementer `LindlandSignalMapSeeder` som leser CSV-headeren og parser tag-suffix + dam/generator-prefix til riktig SignalRole + DamId/GeneratorId.

**Per-tag mapping-regler (utdrag):**

```csharp
private static (SignalRole role, string? damId, string? generatorId) MapTag(string tagId)
{
    // Generator-tags: LINDLAND_G1_* eller LINDLAND_G2_*
    var genMatch = Regex.Match(tagId, @"^LINDLAND_(G[12])_");
    if (genMatch.Success)
    {
        var genId = $"lindland_{genMatch.Groups[1].Value.ToLower()}";  // "lindland_g1" / "lindland_g2"
        if (tagId.EndsWith("_GEN_P_PV")) return (GeneratorActivePower, null, genId);
        if (tagId.EndsWith("_GEN_TURTALL_PV")) return (GeneratorRpm, null, genId);
        if (tagId.EndsWith("_GEN_F_PV")) return (GeneratorFrequency, null, genId);
        if (tagId.EndsWith("_GEN_COSPHI_PV")) return (ElectricalMeasurement, null, genId);
        if (tagId.EndsWith("_GEN_Q_PV")) return (ElectricalMeasurement, null, genId);
        if (tagId.EndsWith("_GEN_TIMETELLER_PV")) return (Other, null, genId);
        if (tagId.EndsWith("_TURB_VF_PV")) return (TurbineWaterFlow, null, genId);
        if (tagId.EndsWith("_TURB_VIRKNGRD_PV")) return (TurbineEfficiency, null, genId);
        if (tagId.EndsWith("_TURB_LEDEAPP_POS_PV")) return (GuideVanePosition, null, genId);
        if (tagId.EndsWith("_TURB_VANN_TRYKK_PV")) return (HydraulicPressure, null, genId);
        if (tagId.EndsWith("_TURB_FALLHOYDE_TOTAL_PV")) return (FallHeight, null, genId);  // NY rolle
        if (tagId.EndsWith("_RORGATE_VANN_TRYKK_PV")) return (HydraulicPressure, null, genId);
        if (tagId.Contains("_KONTROLL_KOM_AL")) return (CommunicationAlarm, null, genId);
        if (tagId.Contains("_TEMP_PV")) return (ConditionTemperature, null, genId);
        // ... resten av generator-tags
        return (Other, null, genId);
    }

    // Dam-prefix-deteksjon: HEIGRAVT / EIAVT / BARSTDVT / INNTAK / KRST
    var damMatch = Regex.Match(tagId, @"^LINDLAND_(HEIGRAVT|EIAVT|BARSTDVT|INNTAK|KRST)_");
    if (damMatch.Success)
    {
        var sitePrefix = damMatch.Groups[1].Value;
        // KRST er sensor-stasjon (utløp), ikke dam. BARSTDVT er uregulert dam.
        var damId = sitePrefix switch
        {
            "HEIGRAVT" => "lindland_heigravatn",       // Posisjon 1, regulert
            "EIAVT" => "lindland_eiavatn",             // Posisjon 2, regulert
            "BARSTDVT" => "lindland_barstadvatn",      // Posisjon 3, uregulert
            "INNTAK" => "lindland_rosslandshølen",     // Posisjon 4 (terminal), regulert
            _ => null  // KRST (utløp-sensor) → null
        };

        // Suffix-baserte regler (samme som Haukland-mønster, med Lindland-spesifikke tilegg)
        if (tagId.EndsWith("_NIVA_SENSOR_PRI_KOTE_PV") || tagId.EndsWith("_NIVA_OPPSTROM_KOTE_PV"))
            return (UpstreamLevel, damId, null);
        if (tagId.EndsWith("_NIVA_SENSOR_PRI_HRV_PV") || tagId.EndsWith("_NIVA_VTA_PV"))
            return (Other, damId, null);  // Sekundær nivå-info
        if (tagId.EndsWith("_KONTROLL_MAG_FYLLGRD_PV"))
            return (ReservoirFillFactor, damId, null);
        if (tagId.EndsWith("_KONTROLL_MAG_VOLUM_PV"))
            return (ReservoirVolume, damId, null);
        if (tagId.EndsWith("_KONTROLL_MAG_OVLOP_PV"))
            return (OverflowFlow, damId, null);
        if (tagId.EndsWith("_NIVA_OVERLOP_VF_PV"))  // Lindland-spesifikk: faktisk overløps-vannføring
            return (OverflowFlow, damId, null);
        if (tagId.EndsWith("_KONTROLL_TOT_VF_PV"))
            return (TotalDamFlow, damId, null);
        if (tagId.EndsWith("_LUKE1_VF_PV"))
            return (GateFlow, damId, null);
        if (tagId.EndsWith("_LUKE1_POS_PV"))
            return (GatePosition, damId, null);
        if (tagId.EndsWith("_MINVF_LITER_PV"))
            return (Other, damId, null);  // Minstevannføring — egen rolle senere
        if (tagId.Contains("_RIST_FALLTAP_PV") || tagId.Contains("_RISTRENSK_"))
            return (Other, damId, null);  // Rist-data — egen rolle senere
        if (tagId.Contains("_KONTROLL_KOM_AL"))
            return (CommunicationAlarm, damId, null);
        if (tagId.Contains("_MET_NEDBOR_") || tagId.Contains("_MAG_NEDBKAP_"))
            return (Other, damId, null);  // Met-data
        if (tagId.Contains("_MAGASIN_") && tagId.Contains("INNTAK"))
            return (Other, damId, null);  // Døde dublett-tags (verifisert som 0 i hele perioden)
        return (Other, damId, null);
    }

    // Resterende: NETT, ukjente
    if (tagId.StartsWith("LINDLAND_NETT_"))
        return (ElectricalMeasurement, null, null);

    return (Other, null, null);
}
```

### Nye SignalRole-verdier

I tillegg til Haukland-spec'en (GateFlow, GatePosition, TotalDamFlow, ReservoirVolume), trenger Lindland:

```csharp
/// <summary>Total fallhøyde (mvs) — viktig for effektivitets-beregning.</summary>
FallHeight,
```

`MinFlow` (minstevannføring) og `RistFallLoss` (rist-falltap) defineres ikke som egne roller i v1 — de blir `Other` med tag-navn for senere bruk.

### Spesifikke avvik som krever oppmerksomhet

| Avvik | Håndtering |
|---|---|
| Dublett-tags i INNTAK (`INNTAK_MAGASIN_FYLLGRAD_PV` vs `INNTAK_KONTROLL_MAG_FYLLGRD_PV`) | Død parallell sett — verifisert som 0 i hele perioden. Mappes til `Other` med `StoreSamples = false`. Bruk `_KONTROLL_MAG_*` som primær. |
| `_GEN_COSPHI_PV` har enhet "0" | SCADA-konfigfeil. Forutsett dimensjonsløst (cosphi). Logg DQ-warning men aksepter import. |
| `_KOM_AL` har enhet "nan" | Forventet for diskrete alarmsignaler. Aksepter. |
| `INNTAK_NIVA_OVERLOP_VF_PV` ved siden av `INNTAK_KONTROLL_MAG_OVLOP_PV` | Begge er overløp-tags på Rosslandshølen (terminal). Mappes begge til `OverflowFlow`. `OverflowQueryService` summerer eller bruker max — implementeres som dokumentert i kaskade-spec. |
| Fyllgrad > 100 % (Eiavatn max 103.96 %) | Forventet ved overløpssituasjon. Ikke flag som DQ-issue. |
| BARSTDVT har bare 2 tags (kote + total flow) | **Korrekt** — Barstadvatn er uregulert dam. Ingen luker, ingen styrings-signaler. Mappes med `DamId = "lindland_barstadvatn"` og `IsRegulated = false`. |
| KRST har 2 tags | Sensor-stasjon på utløp etter turbinene. Ikke dam. `DamId = null`. |
| Vannfraflyt før G1/G2 (0.59 m³/s mellom Rosslandshølen og turbiner i SCADA-skjerm) | Sannsynlig minstevannføring (`INNTAK_MINVF_LITER_PV`). Ikke turbinflow — dette renner forbi turbinene. Bevares som `Other`-rolle for senere bruk i vannbalanse-modul. |

## Endringer i kodebasen

### 1. Datamodell (multi-generator)

- `src/KraftverkUptime.Core/Domain/Generator.cs` — record (over)
- `src/KraftverkUptime.Infrastructure/Persistence/Entities/GeneratorEntry.cs` — EF entity
- Migrering for `core.generators` + `signal_map.generator_id` + backfill av default-generator per anlegg
- `IPlantConfiguration` utvides: `Task<IReadOnlyList<Generator>> GetGeneratorsAsync(plantId, ct)`

### 2. KPI-aggregering

- `UptimeKpiCalculator`: når flere generatorer eksisterer, summer GeneratorActivePower per anlegg, weighted average for virkningsgrad
- `EffektivitetQueryService`: per-generator-η-kurver + samlet anleggs-virkningsgrad
- `VaktRoiCalculator`: bruk sum-av-generatorer som installertEffektMw

### 3. Lindland-seeder

- `src/KraftverkUptime.Infrastructure/Persistence/LindlandSignalMapSeeder.cs` NY
- Implementer `MapTag`-funksjonen som beskrevet over
- Idempotent — kan kjøres på nytt uten å duplisere

### 4. Plant-admin UI utvidelse

- Vis liste over generatorer per anlegg
- Editor for navn + installert kapasitet per generator
- Validering: minst én generator per aktiv plant

### 5. Effektivitets-side utvidelse

- Hvis plant har > 1 generator: vis dropdown for å velge generator eller "samlet"
- η-kurve, sweet-spot per generator
- Sammenligning mellom generatorer (G1 vs G2 over tid — er én bedre?)

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 18 nye tester passerer
2. `core.generators` opprettes med backfill — alle 11 anlegg har minst én generator
3. Drag-drop av Lindland SCADA-eksport oppdaterer `core.signal_map` med korrekt fordeling: ~24 G1-tags, ~21 G2-tags, ~30 INNTAK-tags, ~17 EIAVT-tags, ~20 HEIGRAVT-tags, resten = Other
4. `GET /api/v1/plants/lindland/effektivitet?from=2026-01-01&to=2026-02-01` returnerer KPI-er for både G1 og G2 + samlet
5. `GET /api/v1/plants/lindland/vakt-roi?...` bruker bare INNTAK-overløp (terminal-dam), ikke EIAVT eller HEIGRAVT
6. Drivdal vakt-ROI: ingen endring i tall før/etter migrering (regresjons-sjekk)

### Datakvalitet

7. Anlegg uten flere generatorer (Drivdal m.fl.): én default-generator opprettes, eksisterende KPI-er er identiske
8. Hvis G2 har 0 produksjon i hele perioden: KPI-er rapporteres som 0 uten exception
9. Når operatøren endrer installert kapasitet for én generator: vakt-ROI og kapasitetsfaktor oppdateres uten manual cache-invalidering

### Test-dekning

10. `LindlandSignalMapSeederTests`: 18 tester
    - G1-tag → genId="lindland_g1", damId=null
    - G2-tag → genId="lindland_g2", damId=null
    - EIAVT-tag → damId="lindland_eiavt", genId=null
    - INNTAK-overløp → SignalRole.OverflowFlow med damId="lindland_inntak"
    - INNTAK_MAGASIN_-dublett → Other, StoreSamples=false
    - BARSTDVT-tag → damId=null (sensor-stasjon)
    - KRST-tag → damId=null
    - Idempotent — kjøre to ganger uten duplikater

11. `MultiGeneratorAggregatorTests`: 5 tester
    - To generatorer, lik produksjon → totalsum × 2
    - Én generator har 0 produksjon → den ekskluderes fra weighted virkningsgrad
    - Både generatorer har full kapasitet → kapasitetsfaktor regnes mot sum
    - Gen-id-mismatch i SignalMap → graceful fallback til "ikke knyttet til generator"

12. `LindlandEndToEndTest`: én test som importerer SCADA-eksporten, kjører full pipeline (klassifisering + KPI + vakt-ROI), verifiserer at samtlige steg fullføres uten exception og at output-tall er innenfor rimelige grenser (Lindland feb-2026 estimert 4-5 MWh/h snitt, dvs ~3 000-3 500 MWh/mnd)

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Forutsetter at `SPEC-KASKADE-DAMMER.md` er implementert (Dam-entity finnes) | – | – |
| 1 | `Generator` entity + `core.generators`-tabell + migrering med backfill | 2 t | – |
| 2 | `SignalMap.GeneratorId` utvidelse + migrering | 30 min | – |
| 3 | Ny `SignalRole.FallHeight` | 15 min | – |
| 4 | `LindlandSignalMapSeeder` + 18 tester | 4 t | – |
| 5 | Drag-drop Lindland SCADA-eksport, verifiser tag-fordeling | 1 t | **Stopp og verifiser at SignalMap-rader er korrekt fordelt** |
| 6 | `IPlantConfiguration.GetGeneratorsAsync` + DB-impl | 1 t | – |
| 7 | UptimeKpiCalculator multi-generator-aggregering + tester | 2-3 t | **Stopp — verifiser at Drivdal-KPI-er er identiske før/etter (regresjon)** |
| 8 | EffektivitetQueryService per-generator-utvidelse | 2-3 t | – |
| 9 | PlantAdmin-UI utvidelse for generator-tabell | 2 t | – |
| 10 | Effektivitets-side med generator-dropdown + sammenligning | 2-3 t | – |

**Estimat totalt:** 1-1.5 dager etter at kaskade-spec er implementert.

## Antakelser

1. **Kaskade-spec er implementert.** Hvis ikke, må Dam-entity og DamId-felt på SignalMap legges til parallelt. Kan håndteres som én sammensatt sesjon hvis Claude Code er ferdig med begge specs.
2. **Lindland INNTAK er terminal-dam.** Bekreftet via SCADA-analyse — det er den eneste dam-en som direkte mater turbinene G1 og G2.
3. **G1 og G2 har felles inntaksmagasin** og deler vannressurs. KPI-aggregering forutsetter dette (ingen vannfordeling-modellering på generator-nivå).
4. **Multi-generator-modell er nyttig for andre anlegg også** — andre anlegg kan ha 2 generatorer (Honnefoss? Liavatn?). Lager strukturen generelt slik at den fungerer for fremtidige flere-aggregat-anlegg.
5. **G1 og G2 har samme bruks-pattern** (begge produserer parallelt, ingen er reserve). Hvis dette ikke stemmer for Lindland: håndteres i v2.

## Ut-av-scope for v1

- Vannfordeling mellom generatorer (G1 vs G2 prioriteringsvalg)
- Per-generator-vakt-ROI (event-tolkning hvor bare én generator har feil)
- Multi-generator-Hydrogrid-integrasjon (Hydrogrid eksponerer plan per generator separat?)
- Cross-generator-effektivitets-sammenligning (sammenligne G1-η og G2-η over tid for slitasje-deteksjon — kan komme i effektivitets-modul senere)

## Verifikasjon

```powershell
# 1. Migrering
cd C:\Morten\00 Oppetid\src\KraftverkUptime.Api
dotnet ef database update

# 2. Verifiser at alle 11 anlegg har minst én generator
psql -d kraftverkuptime -c "
SELECT p.plant_id, COUNT(g.generator_id) AS antall_gen, SUM(g.installed_capacity_mw) AS sum_mw
FROM core.plants p
LEFT JOIN core.generators g ON p.plant_id = g.plant_id
GROUP BY p.plant_id ORDER BY p.plant_id;
"
# Forventet: 11 rader, alle har antall_gen >= 1. Lindland har 2.

# 3. Drag-drop Lindland SCADA-eksport via /plants/lindland-siden
# Verifiser fordeling:
psql -d kraftverkuptime -c "
SELECT generator_id, dam_id, role, COUNT(*) FROM core.signal_map
WHERE plant_id = 'lindland'
GROUP BY generator_id, dam_id, role
ORDER BY generator_id, dam_id, role;
"
# Forventet: ~117 rader fordelt på:
#   lindland_g1 (~24), lindland_g2 (~21),
#   lindland_heigravatn (~20), lindland_eiavatn (~17),
#   lindland_barstadvatn (~2 — bare nivå/kote, uregulert),
#   lindland_rosslandshølen (~30 — inkl. overløp, fyllgrad, minvf),
#   null (~3 — KRST, NETT, AGC)

# 4. Drivdal regresjon — KPI-er feb-2026 før/etter
curl.exe "http://localhost:5080/api/v1/plants/drivdal/nedetid?from=2026-02-01&to=2026-03-01"
# Forventet: identiske tall som dokumentert i OVERLEVERING-2026-04-29-VEIKART.md

# 5. Lindland effektivitet
curl.exe "http://localhost:5080/api/v1/plants/lindland/effektivitet?from=2026-01-01&to=2026-02-01"
# Forventet: Returnerer per-generator-data + samlet anleggs-virkningsgrad

# 6. Lindland vakt-ROI bruker bare INNTAK-overløp
curl.exe "http://localhost:5080/api/v1/plants/lindland/vakt-roi?from=2026-01-01&to=2026-02-01"
# Verifiser i logs at OverflowQueryService kun spør etter signaler med damId='lindland_inntak'
```

## Referanser

- Lindland SCADA-eksport (2026-05-03): `uploads/export-117-tags-avg-hour-20260503-063627_MASTER.csv`
- SCADA-analyse: 117 tags, 2 dammer kaskade + INNTAK terminal, 2 generatorer (G1+G2)
- Forutsetning: `docs/SPEC-KASKADE-DAMMER.md`
- Eksisterende én-generator-modell: `src/KraftverkUptime.Infrastructure/Persistence/DrivdalSignalMapSeeder.cs`
- Tester for kaskade: `tests/KraftverkUptime.Infrastructure.Tests/Scada/ScadaClassifierTests.cs`
