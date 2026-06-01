# NESTE CHAT — Øgreyfoss overløp mangler (dam-FK brutt)

**Status:** Bug identifisert, ikke fikset.
**Omfang:** Kun `ogreyfoss`. Verifisert mot 14 anlegg — se tabell nederst.
**Symptom:** Vakt-ROI / Nedetid-rapporter viser «ingen overløpsdata» for Øgreyfoss selv om SCADA-eksporten inneholder `OGREY1_INNTAK_NIVA_OVERLOP_VF_PV` og samples blir importert.

---

## Rotårsak (sekvens-bug mellom tre seedere)

`DatabaseBootstrapper.SeedAsync` kjører seederne i denne rekkefølgen:

```
85:  DefaultDamSeeder           → oppretter ogreyfoss_main (IsTurbineIntake=true)
91:  PlantTopologySeeder        → sletter ogreyfoss_main, setter inn 7 navngitte dammer
                                  med terminal = ogreyfoss_ogreyvatn
121: DalanePortfolioSignalMapSeeder → mapper INNTAK-tags til dam_id = "ogreyfoss_main"
```

Trinn 3 skriver dam-FK som peker på en rad som ble slettet i trinn 2. Idempotens-sjekken i Dalane-seederen er på `(plant_id, signal_id)` — så feilen sementeres ved første oppstart og overskrives aldri.

`OverflowQueryService.QueryNativeTagAsync` (`src/KraftverkUptime.Modules.Reporting/Nedetid/OverflowQueryService.cs:117`) gjør:

```csharp
var terminalDam = await _dams.GetTerminalDamAsync(plantId, ct);     // → ogreyfoss_ogreyvatn
var overflowTags = await _signalMaps
    .GetByPlantDamAndRoleAsync(plantId, terminalDam.DamId, SignalRole.OverflowFlow, ct);
if (overflowTags.Count == 0) return new(..., DataAvailable: false); // ← treffer her
```

Tagen `OGREY1_INNTAK_NIVA_OVERLOP_VF_PV` ligger med `dam_id='ogreyfoss_main'` (foreldreløst) og blir ikke funnet på `ogreyfoss_ogreyvatn`.

### Bekreftende funn

- `DalanePortfolioSignalMapSeeder.cs:64` — `var damId = $"{plantId}_main";` (hardkodet `_main`).
- `PlantTopologySeeder.cs:142` — terminal-dam er `ogreyfoss_ogreyvatn`, ikke `_main`.
- `PlantTopologySeeder.cs:249` — `db.Dams.Remove(defaultDam)` sletter `_main` uten å oppdatere `signal_map.dam_id`.
- `PlantTopologySeeder.cs:30-31` — kommentar sier «Ingen signal_map-rader er avhengig av `_main` for disse anleggene». Stemte før Dalane-seederen kom, ikke nå.
- `DatabaseBootstrapper.cs:118` — kommentar sier «Bruker default 'main'-dam opprettet av DefaultDamSeeder». Sant for 4 av 5 Dalane-anlegg, ikke for Øgreyfoss.
- Test `DalanePortfolioSignalMapSeederTests.cs:66` asserter eksplisitt at overløp-taggen skal mappe til `ogreyfoss_main` — testen er teknisk grønn, men forventningen er feil mot faktisk topologi.

---

## Hvorfor kun Øgreyfoss er rammet

Feilen krever to ting samtidig: (1) MultiDam-topologi (sletter `_main`), OG (2) dedikert SignalMap-seeder som bruker `_main`.

| Anlegg | Topologi | Seeder | Dam-ID i mapping | Status |
|---|---|---|---|---|
| Drivdal | SingleDam | Drivdal | `drivdal_main` | OK |
| Grødemfoss | SingleDam | Grødemfoss | `grodemfoss_main` | OK |
| Løgjen | SingleDam | Dalane | `logjen_main` | OK |
| Ørsdalen | SingleDam | Dalane | `orsdalen_main` | OK (LevelProxy) |
| Stølskraft | SingleDam | Dalane | `stolskraft_main` | OK (ProductionStateProxy) |
| Vikeså | SingleDam | Dalane | `vikesa_main` | OK |
| Honnefoss | MultiDam | Honnefoss | navngitte dam-IDs | OK |
| Liavatn | MultiDam | *ingen* | — | Ingen overløp-detection (separat sak) |
| **Øgreyfoss** | **MultiDam** | **Dalane** | **`ogreyfoss_main`** | **BRUTT** |
| Haukland | utenfor PlantTopologySeeder | Haukland | egne IDs | OK |
| Lindland | utenfor PlantTopologySeeder | Lindland | egne IDs | OK |

---

## Anbefalt løsning — Alt. 1: Riktig dam-ID i kilden

Tre filer endres + én data-migrasjon. Idempotent og uten påvirkning på andre anlegg.

### 1) `src/KraftverkUptime.Infrastructure/Persistence/DalanePortfolioSignalMapSeeder.cs`

I `MapTag` — etter linje 65 (`var withoutPrefix = ...`) — overstyr `damId` for Øgreyfoss-INNTAK slik at det matcher topologi-seederen:

```csharp
// Øgreyfoss er MultiDam: PlantTopologySeeder sletter ogreyfoss_main og
// setter terminal = ogreyfoss_ogreyvatn. INNTAK-tags må peke dit.
// (Andre Dalane-anlegg er SingleDam og beholder _main.)
if (plantId == "ogreyfoss")
{
    damId = "ogreyfoss_ogreyvatn";
}
```

Vurder også å oppdatere klasse-kommentaren på linje 22-23:

```
/// Begge mapper til samme plant_id = "ogreyfoss". INNTAK-tags festes til
/// ogreyfoss_ogreyvatn (terminal-dam i kaskaden — se PlantTopologySeeder).
```

### 2) `tests/KraftverkUptime.Infrastructure.Tests/DalanePortfolioSignalMapSeederTests.cs`

Oppdater 4 InlineData-rader (linje 66-70) fra `ogreyfoss_main` til `ogreyfoss_ogreyvatn`:

```csharp
[InlineData("OGREY1_INNTAK_NIVA_OVERLOP_VF_PV", SignalRole.OverflowFlow,    "ogreyfoss_ogreyvatn")]
[InlineData("OGREY1_INNTAK_MAGASIN_FYLLGRAD_PV", SignalRole.ReservoirFillFactor, "ogreyfoss_ogreyvatn")]
[InlineData("OGREY1_INNTAK_MAGASIN_VOLUM_PV",   SignalRole.ReservoirVolume, "ogreyfoss_ogreyvatn")]
[InlineData("OGREY1_INNTAK_MAGASIN_TOT_VF_PV",  SignalRole.TotalDamFlow,    "ogreyfoss_ogreyvatn")]
[InlineData("OGREY1_INNTAK_NIVA_OPPSTROM_KOTE_PV", SignalRole.UpstreamLevel, "ogreyfoss_ogreyvatn")]
```

Vurder å legge til én ny regresjons-test som krysser sjekker at MapTag-resultatet stemmer med `PlantTopologySeeder.Topologies` for alle MultiDam-anlegg. Pseudokode:

```csharp
[Fact]
public void MapTag_MultiDamAnlegg_DamIdMatcherTopologi()
{
    var multiDamTerminals = PlantTopologySeeder.Topologies
        .Where(t => t.Strategy == TopologyStrategy.MultiDam)
        .ToDictionary(t => t.PlantId,
                      t => t.Dams.Single(d => d.IsTurbineIntake).DamId);

    foreach (var tag in DalanePortfolio72TagCatalog.Tags)
    {
        var (plantId, role, damId, _) = DalanePortfolioSignalMapSeeder.MapTag(tag);
        if (damId is null) continue;
        if (!multiDamTerminals.TryGetValue(plantId, out var expectedTerminal)) continue;

        damId.Should().Be(expectedTerminal,
            $"tag '{tag}' i MultiDam-anlegg må peke på terminal-dam");
    }
}
```

### 3) Data-migrasjon (idempotent SQL)

Eksisterende databaser har allerede de foreldreløse radene. Legg til som ny EF-migrasjon eller som en engangs-fixup i `DefaultDamSeeder` (i samme stil som backfill-blokken på linje 69-87):

```sql
UPDATE core.signal_map
SET dam_id = 'ogreyfoss_ogreyvatn'
WHERE plant_id = 'ogreyfoss'
  AND dam_id   = 'ogreyfoss_main';
```

Hvis det legges i en seeder, logg antall rader oppdatert (forventet: 5 stk per OGREY1_INNTAK_* i 72-tag-katalogen).

### 4) Verifisering etter fiks

1. `dotnet test tests/KraftverkUptime.Infrastructure.Tests` — alle grønne.
2. På en kopi av prod-databasen:
   ```sql
   SELECT signal_id, dam_id FROM core.signal_map
   WHERE plant_id='ogreyfoss' AND role='OverflowFlow';
   -- forventet: 1 rad, dam_id='ogreyfoss_ogreyvatn'
   ```
3. Kjør `GET /api/v1/nedetid/overflow?plantId=ogreyfoss&from=...&to=...` mot et tidsrom med kjent overløp og bekreft at `DataAvailable=true` og at `hours.Count > 0`.

---

## Alternativer som ble vurdert og forkastet

- **Alt. 2 — patch i PlantTopologySeeder:** Når `_main` slettes, omdøp signal_map-radene først. Skjuler problemet og kobler topologi-koden til detaljer om alle dedikerte seedere — blir vanskelig å vedlikeholde når flere MultiDam-anlegg får dedikerte seedere.
- **Alt. 3 — snu rekkefølgen:** Kjør Dalane-seederen før PlantTopologySeeder. Trenger fortsatt FK-omdøping i topologi-seederen, og rører ved alle Dalane-anleggene — unødvendig risiko for 4 anlegg som fungerer i dag.

---

## Akseptkriterier

- [ ] `core.signal_map` for `plant_id='ogreyfoss'`, `role='OverflowFlow'` har `dam_id='ogreyfoss_ogreyvatn'`.
- [ ] `OverflowQueryService.GetOverflowDatasetAsync("ogreyfoss", ...)` returnerer `DataAvailable=true` når samples finnes i intervallet.
- [ ] Vakt-ROI for Øgreyfoss viser overløp-timer i UI (siden 2026-05-18-eksporten).
- [ ] Ny regresjons-test forhindrer at framtidige MultiDam-anlegg får samme bug.
- [ ] Honnefoss og Liavatn er fortsatt grønne (regresjon-sjekk).
