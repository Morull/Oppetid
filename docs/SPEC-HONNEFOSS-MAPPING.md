# Spec: Honnefoss SCADA-mapping

**Status:** Klar til implementasjon med to BRUKER-INPUT-punkter (2026-05-03)
**Estimat:** 4-6 timer (én-generator + auto-mapping av 113 tags)
**Avhengighet:** Kaskade-dammer-implementasjon ferdig (Dam-entity + DamId på SignalMap eksisterer)

## Bakgrunn

Honnefoss SCADA-eksport mottatt og analysert. 113 tags fordelt på 6 magasin-prefikser + G1 + KRST + NETT. Honnefoss er **én-generator-anlegg** — ingen multi-generator-utvidelse nødvendig.

**Observasjon viktig (oppdatert 2026-05-03):** SCADA-eksporten inkluderer dammer fra **oppstrøms Liavatn-kraftverk** (REVSVT, NODLANDVT, og indirekte STOKKURHOLEN) fordi vannet derfra renner videre ned til Honnefoss. Disse magasinene **tilhører Liavatn-kraftverket**, ikke Honnefoss, og skal ikke registreres som dammer i Honnefoss-anlegget.

For Honnefoss skal kun 4 dammer registreres: **Liavatn (intake-magasin, ikke kraftverket), Spjodevatn, Kydlandsvatn, Inntak**. Vær oppmerksom på navnekollisjon — magasinet "Liavatn" i Honnefoss-anlegget er IKKE samme som "Liavatn-kraftverket" som er et separat anlegg oppstrøms.

REVSVT- og NODLANDVT-tags fra Honnefoss-eksporten skal mappes til Liavatn-kraftverket (egen spec: `SPEC-LIAVATN-MAPPING.md`).

## Topology fra SCADA-skjerm (4 dammer som vises)

```
Liavatn (-183 cm, 67.5%)         Spjodevatn (-58 cm, 93.1%)
       │                                │
       ▼                                ▼
Kydlandsvatn (-29 cm, 92.1%) ⇄  Inntak (-29 cm, 95.8%)
                                       │
                                       ▼
                                     G1 (2043 kW)
```

## Tag-prefiks-fordeling fra eksport

| SCADA-prefiks | Antall tags | Status |
|---|---|---|
| `LIAVT` | 25 | Liavatn (regulert, 2 luker) — vist i skjermbilde |
| `SPJODEVT` | 16 | Spjodevatn (1 luke) — vist i skjermbilde |
| `KYDLNDVT` | 8 | Kydlandsvatn (måle-dam, ingen luke) — vist i skjermbilde |
| `INNTAK` | 5 | Inntaks-aggregater — vist i skjermbilde |
| `NODLANDVT` | 25 | **Nodlandvatn — IKKE i skjermbilde** (regulert, 2 luker) |
| `REVSVT` | 16 | **Revsvatn — IKKE i skjermbilde** (1 luke + signalstyrke) |
| `G1` | 16 | Generator |
| `KRST` | 1 | Kraftstasjon kommunikasjons-alarm |
| `NETT` | 1 | Linjespenning |

**Anlegg-prefiks er `HONNE_` (ikke `HONNEFOSS_`).** Kortet ned i SCADA-konfigurasjonen.

## BRUKER-INPUT KREVES

### 1. Topology for REVSVT og NODLANDVT

Disse to dammene er fullt instrumenterte (regulerte, har luker, har KOM_AL) men er ikke tegnet i skjermbildet. Mulige forklaringer:

- **A:** Skjermbildet viser bare et utvalg — fullt anlegg har 6 dammer i kaskade
- **B:** Revsvatn/Nodlandvatn er separate magasiner som mater inn til Liavatn eller Spjodevatn (nivå-2 oppstrøms)
- **C:** De er parallelle til Liavatn/Spjodevatn og mater inn til Kydlandsvatn/Inntak
- **D:** De er reservedammer som ikke brukes aktivt, men instrumentert

**Foreslått full topology basert på lokal kunnskap (krever bekreftelse):**

```
Revsvatn ──────────┐
                   │
Liavatn ───────┐   ▼
               │  Nodlandvatn ─────┐
               ▼                   │
       Kydlandsvatn ⇄ Inntak ◄────┘
                       │              ▲
                       ▼              │
                      G1   Spjodevatn ┘
```

Dette er gjetning. **Drifts-leder må bekrefte** før seeder-koden låses.

### 2. Hydraulisk kobling KYDLNDVT ↔ INNTAK

Begge måler -29.0 cm samtidig — sannsynligvis felles vannflate. To tolkninger:

- **A: Felles magasin** — KYDLNDVT-tags måler vannlinjen, INNTAK-tags måler aggregater (volum, totalt tilsig). Behandle som **én dam** i datamodellen, fordel tagsene etter funksjon.
- **B: To separate magasiner** med liten forbindelse — behandle som to dammer, hvor Kydlandsvatn er pos N og Inntak er pos N+1 (terminal).

**Anbefaling: alternativ A** — én dam `honnefoss_inntak` (terminal) som mottar tags fra både KYDLNDVT- og INNTAK-prefiksene. Dette matcher det fysiske bildet (samme vannlinje) og forenkler vakt-ROI (én terminal-dam).

## Korrekt dam-mapping (4 dammer)

| dam_id | name | cascade_position | is_turbine_intake | is_regulated | SCADA-prefiks |
|---|---|---|---|---|---|
| `honnefoss_liavatn_magasin` | Liavatn (magasin — ikke kraftverket) | 1 | false | true | LIAVT |
| `honnefoss_spjodevatn` | Spjodevatn | 1 | false | true | SPJODEVT |
| `honnefoss_inntak` | Inntak (Kydlandsvatn + Inntak felles) | 2 (terminal) | **true** | true | KYDLNDVT + INNTAK |

KRST har 1 tag (kraftstasjon kommunikasjons-alarm), mappes med `DamId = NULL`.

**REVSVT- og NODLANDVT-tags fra Honnefoss-eksporten:** mappes til Liavatn-kraftverket via `LiavatnSignalMapSeeder` (`plant_id = 'liavatnkraft'`). Importeren må kunne fordele tags til ulik plant_id basert på prefiks når SCADA-eksporten dekker flere anlegg samtidig.

## Auto-mapping seeder

**Fil:** `src/KraftverkUptime.Infrastructure/Persistence/HonnefossSignalMapSeeder.cs` NY

Bygger på samme mønster som Lindland/Haukland-seedere:

```csharp
private static (SignalRole role, string? damId, string? generatorId) MapTag(string tagId)
{
    // Generator: HONNE_G1_*
    if (tagId.StartsWith("HONNE_G1_"))
    {
        var genId = "honnefoss_g1";
        if (tagId.EndsWith("_GEN_P_PV")) return (GeneratorActivePower, null, genId);
        if (tagId.EndsWith("_GEN_TURTALL_PV")) return (GeneratorRpm, null, genId);
        if (tagId.EndsWith("_GEN_F_PV")) return (GeneratorFrequency, null, genId);
        if (tagId.EndsWith("_TURB_VF_PV")) return (TurbineWaterFlow, null, genId);
        if (tagId.EndsWith("_TURB_VIRKNGRD_PV")) return (TurbineEfficiency, null, genId);
        if (tagId.EndsWith("_TURB_LEDEAPP_POS_PV")) return (GuideVanePosition, null, genId);
        if (tagId.EndsWith("_TURB_VANN_TRYKK_PV")) return (HydraulicPressure, null, genId);
        if (tagId.EndsWith("_RORGATE_VANN_TRYKK_PV")) return (HydraulicPressure, null, genId);
        if (tagId.EndsWith("_GEN_RADLAGER_DE_TEMP_PV") || tagId.EndsWith("_NDE_TEMP_PV"))
            return (ConditionTemperature, null, genId);
        return (Other, null, genId);
    }

    // Dam-prefikser: REVSVT, LIAVT, NODLANDVT, SPJODEVT, KYDLNDVT, INNTAK
    var damMatch = Regex.Match(tagId, @"^HONNE_(REVSVT|LIAVT|NODLANDVT|SPJODEVT|KYDLNDVT|INNTAK)_");
    if (damMatch.Success)
    {
        var sitePrefix = damMatch.Groups[1].Value;
        var damId = sitePrefix switch
        {
            "REVSVT" => "honnefoss_revsvatn",
            "LIAVT" => "honnefoss_liavatn",
            "NODLANDVT" => "honnefoss_nodlandvatn",
            "SPJODEVT" => "honnefoss_spjodevatn",
            "KYDLNDVT" => "honnefoss_inntak",     // Felles magasin med INNTAK
            "INNTAK" => "honnefoss_inntak",       // Felles magasin med KYDLNDVT
            _ => null
        };

        // Suffix-regler — håndterer både nye og gamle navnekonvensjoner
        if (tagId.EndsWith("_NIVA_SENSOR_PRI_KOTE_PV")) return (UpstreamLevel, damId, null);
        if (tagId.EndsWith("_NIVA_SENSOR_PRI_HRV_PV")) return (Other, damId, null);  // Sekundær
        if (tagId.EndsWith("_NIVA_VTA_PV")) return (Other, damId, null);
        if (tagId.EndsWith("_KONTROLL_MAG_FYLLGRD_PV")) return (ReservoirFillFactor, damId, null);
        if (tagId.EndsWith("_MAGASIN_FYLLGRAD_PV")) return (ReservoirFillFactor, damId, null);  // INNTAK gammel
        if (tagId.EndsWith("_KONTROLL_MAG_VOLUM_PV")) return (ReservoirVolume, damId, null);
        if (tagId.EndsWith("_MAGASIN_VOLUM_PV")) return (ReservoirVolume, damId, null);  // INNTAK gammel
        if (tagId.EndsWith("_KONTROLL_MAG_OVLOP_PV")) return (OverflowFlow, damId, null);
        if (tagId.EndsWith("_NIVA_OVERLOP_VF_PV")) return (OverflowFlow, damId, null);  // Terminal-overløp
        if (tagId.EndsWith("_KONTROLL_TOT_VF_PV") || tagId.EndsWith("_MAGASIN_TOT_VF_PV"))
            return (TotalDamFlow, damId, null);
        if (Regex.IsMatch(tagId, @"_LUKE\d+_VF_PV$")) return (GateFlow, damId, null);
        if (Regex.IsMatch(tagId, @"_LUKE\d+_POS_PV$")) return (GatePosition, damId, null);
        if (tagId.EndsWith("_LEKK_VF_PV")) return (Other, damId, null);  // Lekkasje (NODLANDVT)
        if (tagId.EndsWith("_KONTROLL_KOM_AL")) return (CommunicationAlarm, damId, null);
        if (tagId.Contains("_MET_") || tagId.Contains("_NEDBKAP_")) return (Other, damId, null);
        if (tagId.EndsWith("_FORSYN_EFOY_FUEL_NIVA_PV")) return (Other, damId, null);  // Nødstrøm
        if (tagId.EndsWith("_KONTROLL_SIGSTYRK_PV")) return (Other, damId, null);  // REVSVT signal
        if (tagId.EndsWith("_STOKKURSHOLEN_MALING_PV")) return (Other, damId, null);  // LIAVT ekstern
        return (Other, damId, null);
    }

    // Resterende: NETT, KRST
    if (tagId.StartsWith("HONNE_NETT_")) return (ElectricalMeasurement, null, null);
    if (tagId.StartsWith("HONNE_KRST_")) return (CommunicationAlarm, null, null);

    return (Other, null, null);
}
```

## Spesifikke Honnefoss-avvik

| Avvik | Håndtering |
|---|---|
| INNTAK-prefiks bruker eldre navnekonvensjon (`MAGASIN_FYLLGRAD_PV` vs `KONTROLL_MAG_FYLLGRD_PV`) | Mapper begge konvensjoner til samme rolle |
| INNTAK staver feil (`FYLLGRAD` vs `FYLLGRD`, `NED_KAP` vs `NEDBKAP`) | Eksplisitt liste i seederen — ikke regex på "korrekt" stavemåte |
| Nodlandvatn har lekkasjemåling (`NODLANDVT_LEKK_VF_PV`) | Mappes til `Other` med StoreSamples=true (kan brukes for senere vannbalanse) |
| Liavatn har ekstern måling (`STOKKURSHOLEN_MALING_PV`) | Sannsynlig nedstrøms vannmåler. `Other` med StoreSamples=true |
| Revsvatn har signalstyrke-tag (`SIGSTYRK_PV`) | Telekom-relatert, ikke prosess. `Other` med StoreSamples=false |
| Spjodevatn har nødstrøm-aggregat (`FORSYN_EFOY_FUEL_NIVA_PV`) | Ikke produksjon. `Other` med StoreSamples=false |
| G1 mangler `_TURB_FALLHOYDE_TOTAL_PV` (har Lindland, ikke Honnefoss) | Beregn fra `RORGATE_VANN_TRYKK − TURB_VANN_TRYKK` (begge i mVs) hvis trengs for effektivitets-modul. Markeres som "beregnet" istedenfor målt. |

## Endringer i kodebasen

### Hovedfiler

- `src/KraftverkUptime.Infrastructure/Persistence/HonnefossSignalMapSeeder.cs` NY
- `tests/KraftverkUptime.Infrastructure.Tests/Scada/HonnefossSignalMapSeederTests.cs` NY

### Database-backfill

```sql
-- Etter at brukeren har bekreftet topology, kjør backfill av dammer
INSERT INTO core.dams (plant_id, dam_id, name, cascade_position, is_turbine_intake, is_regulated)
VALUES
  ('honnefoss', 'honnefoss_revsvatn', 'Revsvatn', 1, false, true),
  ('honnefoss', 'honnefoss_liavatn', 'Liavatn', 1, false, true),
  ('honnefoss', 'honnefoss_nodlandvatn', 'Nodlandvatn', 2, false, true),
  ('honnefoss', 'honnefoss_spjodevatn', 'Spjodevatn', 2, false, true),
  ('honnefoss', 'honnefoss_inntak', 'Inntak', 3, true, true);

-- Generator (allerede backfilled fra kaskade-spec)
UPDATE core.generators SET name = 'G1', installed_capacity_mw = ___
WHERE plant_id = 'honnefoss' AND generator_id = 'honnefoss_g1';
-- BRUKER-INPUT: kapasitet i MW
```

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 12 nye tester passerer
2. Drag-drop av Honnefoss SCADA-eksport oppdaterer `core.signal_map` med ~113 rader
3. Fordeling i SignalMap:
   - `honnefoss_g1` (~16 generator-tags)
   - `honnefoss_liavatn` (~25)
   - `honnefoss_nodlandvatn` (~25)
   - `honnefoss_revsvatn` (~16)
   - `honnefoss_spjodevatn` (~16)
   - `honnefoss_inntak` (~13 — KYDLNDVT 8 + INNTAK 5)
   - null (~2 — KRST + NETT)
4. `GET /api/v1/plants/honnefoss/vakt-roi` bruker bare overløp-tags på `honnefoss_inntak` (terminal)
5. Drivdal vakt-ROI: ingen endring (regresjons-sjekk)

### Test-dekning

6. `HonnefossSignalMapSeederTests`: 14 tester
   - Hver av de 6 dam-prefiksene mappes riktig
   - INNTAK eldre navnekonvensjon (`MAGASIN_FYLLGRAD_PV`) mappes til ReservoirFillFactor
   - KYDLNDVT-tags og INNTAK-tags begge får `damId = "honnefoss_inntak"`
   - Generator-tags får `damId = null`, `generatorId = "honnefoss_g1"`
   - KRST og NETT får `damId = null`
   - Idempotent — to kjøringer gir ingen duplikater
   - Edge case: tag uten kjent prefiks → Other med null

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Bruker bekrefter topology for REVSVT/NODLANDVT + KYDLNDVT/INNTAK-felles-håndtering | – | **STOPP — vent på bruker** |
| 1 | Backfill `core.dams` med 5 dammer for Honnefoss | 30 min | – |
| 2 | `HonnefossSignalMapSeeder` + 14 tester | 3 t | – |
| 3 | Drag-drop import av SCADA-eksport, verifiser fordeling | 30 min | **Stopp og rapporter SignalMap-fordeling** |
| 4 | Smoke-test for Honnefoss vakt-ROI (krever HRV/LRV satt via admin) | 1 t | – |

**Estimat totalt:** 4-6 timer etter at topology er bekreftet.

## Antakelser

1. **Honnefoss er én-generator-anlegg.** Bekreftet — kun G1 i eksporten.
2. **KYDLNDVT og INNTAK er felles vannflate** og behandles som én dam (`honnefoss_inntak`). Krever bruker-bekreftelse.
3. **REVSVT og NODLANDVT er aktive regulerte dammer** i kaskaden, ikke reservedammer. Krever bruker-bekreftelse på posisjon i kaskaden.
4. **Multi-generator-modell fra Lindland-spec er ikke nødvendig** her — men hvis Lindland-spec er implementert først får Honnefoss automatisk én default-generator via backfill.

## Verifikasjon

```powershell
# 1. Verifiser Honnefoss-dammer
psql -d kraftverkuptime -c "
SELECT dam_id, name, cascade_position, is_turbine_intake, is_regulated
FROM core.dams WHERE plant_id = 'honnefoss'
ORDER BY cascade_position;
"
# Forventet: 5 rader, én med is_turbine_intake=true (honnefoss_inntak)

# 2. Drag-drop import via /plants/honnefoss
# Verifiser fordeling:
psql -d kraftverkuptime -c "
SELECT dam_id, generator_id, role, COUNT(*) FROM core.signal_map
WHERE plant_id = 'honnefoss'
GROUP BY dam_id, generator_id, role
ORDER BY dam_id, generator_id;
"
# Forventet: ~113 totalt fordelt som beskrevet i akseptansekriterier

# 3. Smoke-test vakt-ROI
curl.exe "http://localhost:5080/api/v1/plants/honnefoss/vakt-roi?from=2026-04-01&to=2026-05-01" | ConvertFrom-Json
# Verifiser i logs at OverflowQueryService kun spør etter signaler med damId='honnefoss_inntak'
```

## Referanser

- Honnefoss SCADA-eksport (2026-05-03): `uploads/...export-113-tags-avg-hour-20260503-065415_MASTER.csv`
- Honnefoss SCADA-skjerm: viser 4 dammer (Liavatn, Spjodevatn, Kydlandsvatn, Inntak), G1 = 2043 kW
- SCADA-eksport har 6 magasin-prefikser (REVSVT, LIAVT, NODLANDVT, SPJODEVT, KYDLNDVT, INNTAK) — mer enn skjermbildet viser
- Bygger på allerede implementert kaskade-spec (Dam-entity + DamId)
