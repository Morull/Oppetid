# Signalliste SCADA — oversikt

Generert ved gjennomgang av seederne i `src/KraftverkUptime.Infrastructure/Persistence/*SignalMapSeeder.cs` og tag-katalogene `Lindland117TagCatalog.cs` og `HauklandTagCatalog.cs`. Honnefoss-tagene er hentet fra CSV-headeren i siste master-eksport.

## Antall tags per anlegg

| plant_id | Antall tags | Kilde |
|---|---:|---|
| drivdal | 37 | `DrivdalSignalMapSeeder.Mappings` |
| grodemfoss | 19 | `GrodemfossSignalMapSeeder.Mappings` |
| honnefoss | 113 | CSV-eksport-header, mappes runtime via `HonnefossSignalMapSeeder.MapTag` |
| lindland | 117 | `Lindland117TagCatalog.Tags` |
| haukland | 195 | `HauklandTagCatalog.AllTags` |
| **Sum** | **481** | |

Fullstendig liste i `signalliste_scada.csv` (semikolon-separert, åpner direkte i Excel).

## SignalRole-enum

Definert i `src/KraftverkUptime.Core/Domain/SignalMap.cs`. Roller som krever `damId` står markert.

| Rolle | Krever damId | Beskrivelse |
|---|:---:|---|
| GeneratorActivePower | nei | kW, primær drifts-indikator |
| GeneratorRpm | nei | rpm, synkron-deteksjon |
| GeneratorFrequency | nei | Hz |
| TurbineWaterFlow | nei | m³/s |
| TurbineEfficiency | nei | % |
| GuideVanePosition | nei | Ledeapparat-posisjon % |
| TurbinePadrag | nei | Pådrag-settpunkt % |
| HydraulicPressure | nei | bar/mvs |
| UpstreamLevel | **ja** | Oppstrøms kote (moh) |
| DownstreamLevel | **ja** | Nedstrøms kote (moh) |
| ReservoirFillFactor | **ja** | Fyllgrad % |
| LowestRegulatedLevel | **ja** | LRV-referanse |
| GridFallLoss | nei | Falltap over rist (mm) |
| OverflowFlow | **ja** | Overløp m³/s — kritisk for Vakt-ROI |
| ConditionTemperature | nei | Lager-/vikling-/olje-temp |
| ElectricalMeasurement | nei | Strøm/spenning/cos phi |
| CommunicationAlarm | nei/ja | true = datahull |
| GateFlow | **ja** | Luke-vannføring m³/s |
| GatePosition | **ja** | Luke-åpning |
| TotalDamFlow | **ja** | Total VF ut av dam |
| ReservoirVolume | **ja** | Mill.m³ |
| Other | nei/ja | Ikke kategorisert |

## API-endepunkter (allerede implementert)

Definert i `src/KraftverkUptime.Api/Endpoints/SignalMapsEndpoints.cs`. Krever `PlantReader`-policy for GET og `PlantAdmin` for POST/DELETE.

```
GET    /api/v1/plants/{plantId}/signal-maps                   Liste alle (filterbart paa role, damId)
GET    /api/v1/plants/{plantId}/signal-maps?role=OverflowFlow Filtrer paa rolle
GET    /api/v1/plants/{plantId}/signal-maps?damId=drivdal_main Filtrer paa dam
POST   /api/v1/plants/{plantId}/signal-maps                   Opprett/oppdater mapping
DELETE /api/v1/plants/{plantId}/signal-maps/{signalId}        Fjern mapping

GET    /api/v1/plants/{plantId}/scada-tags                    Distinct signal-id-er observert i samples
```

### Response-format `SignalMapDto`

```json
{
  "plantId": "drivdal",
  "signalId": "DRIVDAL_G1_GEN_P_PV",
  "csvColumn": "Cluster1.DRIVDAL_G1_GEN_P_PV",
  "unit": "kW",
  "role": "GeneratorActivePower",
  "storeSamples": true,
  "isActive": true,
  "damId": null
}
```

### Eksempel — PowerShell

```powershell
# Hent alle signal-mappings for Drivdal
$response = Invoke-RestMethod -Uri "http://localhost:5080/api/v1/plants/drivdal/signal-maps" `
    -Headers @{ Authorization = "Bearer $token" }
$response | Format-Table signalId, role, damId, unit, isActive

# Bare overflow-tags for kaskade-anlegg
$overflow = Invoke-RestMethod -Uri "http://localhost:5080/api/v1/plants/lindland/signal-maps?role=OverflowFlow" `
    -Headers @{ Authorization = "Bearer $token" }
$overflow | Format-Table signalId, damId

# Eksporter til CSV
$response | Export-Csv -Path "signalliste_drivdal.csv" -Delimiter ';' -NoTypeInformation -Encoding UTF8
```

### Eksempel — Python

```python
import requests
import pandas as pd

BASE = "http://localhost:5080/api/v1"
PLANTS = ["drivdal", "grodemfoss", "honnefoss", "lindland", "haukland"]
headers = {"Authorization": f"Bearer {token}"}

all_signals = []
for plant in PLANTS:
    r = requests.get(f"{BASE}/plants/{plant}/signal-maps", headers=headers)
    r.raise_for_status()
    all_signals.extend(r.json())

df = pd.DataFrame(all_signals)
df.to_excel("signalliste_scada.xlsx", index=False)
print(df.groupby(["plantId", "role"]).size().unstack(fill_value=0))
```

### Eksempel — curl

```powershell
curl.exe -H "Authorization: Bearer $token" `
    "http://localhost:5080/api/v1/plants/honnefoss/signal-maps?role=OverflowFlow"
```

## Kaskade-modell (damId)

Tags med dam-rolle er knyttet til en spesifikk dam. Topologi:

**drivdal** — 1 dam (terminal)
- `drivdal_main`

**grodemfoss** — 1 dam (terminal)
- `grodemfoss_main` (Smievatn)

**honnefoss** — 4 dammer, 2 magasin-prefikser (REVSVT, NODLANDVT) tilhører fysisk Liavatn-kraftverket og har `damId=null`
- `honnefoss_liavatn` (pos 1)
- `honnefoss_spjodevatn` (pos 1)
- `honnefoss_kydlandsvatn` (pos 2)
- `honnefoss_inntak` (pos 3, terminal)

**lindland** — 4 dammer i kaskade
- `lindland_heigravatn` (pos 1)
- `lindland_eiavatn` (pos 2)
- `lindland_barstadvatn` (pos 3, uregulert — bare 2 sensorer)
- `lindland_rosslandshølen` (pos 4, terminal)

**haukland** — 4 dammer (2 parallelle øvre)
- `haukland_stolsvt` (Stølsvatn, pos 1)
- `haukland_gjelevt` (Gjelevatn, pos 1)
- `haukland_skrstmvt` (Skårstemmevatn, pos 2)
- `haukland_stemmevt` (Stemmevatn, pos 3, terminal)

## Merknader

- **store_samples** = false betyr at tagen er whitelistet (lov å importere) men ikke aktiv for KPI-aggregering. Brukes for lager-temp, hjelpestrøm, met-data m.m.
- **Honnefoss-seederen** er self-bootstrapping: leser observerte tags fra `sample_facts` istedenfor en hardkodet katalog. Listen i CSV-en er fra siste master-eksport (113 tags fra 2026-05-03).
- **REVSVT** og **NODLANDVT** under honnefoss har `damId=null` fordi de fysisk tilhører Liavatn-kraftverket; tags lagres for traceability men teller ikke i Honnefoss' Vakt-ROI.
- **Lindland** bruker CSV-kolonneformat `Value (Cluster1.{tag})` — de andre anleggene bruker `Cluster1.{tag}` direkte. Importeren håndterer begge.
- **CSV-en er semikolon-separert** for kompatibilitet med norsk Excel (åpner ved dobbeltklikk).
