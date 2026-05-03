# Neste sesjon — Lindland SCADA-mapping + multi-generator

**Dato opprettet:** 2026-05-03 (oppdatert med korrekt topology)
**Spec:** `docs/SPEC-LINDLAND-MAPPING.md`
**Estimat:** 1 dag (multi-generator-utvidelse er det eneste reelle nye)
**Status:** Kaskade-dammer-implementasjon er ferdig — bygger på den

## Mål

Lindland SCADA-eksport (117 tags) med to spesielle trekk:

1. **Kaskade i serie med 4 dammer** — kaskade-modellen finnes allerede, kun Lindland-spesifikk mapping kreves:
   ```
   Heigravatn → Eiavatn → Barstadvatn (uregulert) → Rosslandshølen (terminal) → G1 + G2
   ```
   Krever en mindre utvidelse: `Dam.IsRegulated`-felt for å markere Barstadvatn.

2. **Multi-generator (G1 + G2 deler Rosslandshølen)** — Lindland er **første multi-generator-anlegg**. Krever ny `Generator`-entity + `signal_map.generator_id`. Dette er hovedarbeidet.

Resultat: Lindland fungerer fullt med alle KPI-moduler (effektivitet per generator + samlet, vakt-ROI med Rosslandshølen-overløp, klassifisering).

## Bekreftet topology

| Element | Detaljer |
|---|---|
| Anlegg | Lindland (32.5 MNOK omsetning 2025, 42 144 MWh) |
| Dammer (i serie) | Heigravatn → Eiavatn → Barstadvatn (uregulert) → Rosslandshølen (terminal) |
| SCADA-prefiks-mapping | HEIGRAVT=Heigravatn, EIAVT=Eiavatn, BARSTDVT=Barstadvatn, INNTAK=Rosslandshølen |
| Generatorer | G1 (mean 1988 kW i SCADA-data) + G2 (mean 4457 kW), deler felles inntak |
| Sensor-stasjon | KRST (2 tags) på utløp etter turbiner — ikke dam |
| Bekreftet via SCADA-skjerm | Heigravatn -382 cm, Eiavatn -104 cm, Barstadvatn 133.85 moh, Rosslandshølen -30 cm |
| Spesielle tags | FallHeight (`_TURB_FALLHOYDE_TOTAL_PV`) — ny SignalRole krevet |
| Kjent observasjon | Mellom Rosslandshølen og turbiner: 0.59 m³/s minstevannføring som renner forbi turbinene |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Multi-generator-modell | `core.generators`-tabell parallelt med `core.dams` |
| SignalMap-utvidelse | `generator_id` nullable felt parallelt med `dam_id` |
| Backfill | Alle eksisterende anlegg får én default-generator `<plant>_g1` med kapasitet fra `Plant.InstalledCapacityMw`. Drivdal-KPI-er forblir identiske. |
| Lindland-seeder | Auto-mapping via suffix-regler, 117 tags, idempotent |
| Effektivitets-UI | Generator-dropdown + sammenligning mellom generatorer |
| Sensor-stasjoner | BARSTDVT, KRST → Other med null DamId og null GeneratorId |
| Dublett-tags i INNTAK | `INNTAK_MAGASIN_*` (døde 0-verdier) → Other med StoreSamples=false. Bruk `INNTAK_KONTROLL_MAG_*` som primær. |
| Multi-generator vakt-ROI | Bruk sum av installert effekt per anlegg. Per-generator-event-tolkning er v2. |

## Brukerinput som kreves

| Punkt | Hvordan |
|---|---|
| HRV/LRV/Volum for EIAVT, HEIGRAVT, INNTAK | Konsesjonsdokumenter — settes via PlantAdmin-UI etter steg 9 |
| Installert kapasitet G1 og G2 | Drifts-leder eller anlegg-typeskilt — settes via samme UI |
| Bekreftelse av topology (G1+G2 deler inntak) | Bekreft før implementasjon |

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Verifiser at kaskade-spec er implementert | – | – |
| 1 | `Generator` entity + `core.generators`-tabell + backfill | 2 t | – |
| 2 | `SignalMap.GeneratorId` utvidelse | 30 min | – |
| 3 | Ny `SignalRole.FallHeight` | 15 min | – |
| 4 | `LindlandSignalMapSeeder` + 18 tester | 4 t | – |
| 5 | Drag-drop Lindland SCADA-import | 1 t | **STOPP — verifiser SignalMap-fordeling** |
| 6 | `IPlantConfiguration.GetGeneratorsAsync` | 1 t | – |
| 7 | Multi-generator-aggregering i UptimeKpiCalculator | 2-3 t | **STOPP — verifiser Drivdal-regresjon (identiske tall)** |
| 8 | EffektivitetQueryService per-generator | 2-3 t | – |
| 9 | PlantAdmin-UI utvidelse for generator-tabell | 2 t | – |
| 10 | Effektivitets-side med generator-dropdown | 2-3 t | – |

## Spørre-policy

- Etter steg 5: rapporter SignalMap-fordeling per generator/dam før neste steg
- Etter steg 7: kjør Drivdal vakt-ROI for feb-2026 og verifiser identisk med tall fra `OVERLEVERING-2026-04-29-VEIKART.md` (13 events, 10 696 NOK)
- Hvis Lindland-seeder mapper en tag som "Other" som åpenbart burde vært noe annet: rapporter for review før commit
- Installert kapasitet for G1/G2 ikke kjent: pause og spør

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 5: SQL-spørring viser ~117 SignalMap-rader fordelt på G1 (~24), G2 (~21), EIAVT (~17), HEIGRAVT (~20), INNTAK (~30), null (~5)
2. Etter steg 7: Drivdal feb-2026 vakt-ROI = 10 696 NOK reddet, 13 events
3. Etter steg 8: `GET /api/v1/plants/lindland/effektivitet` returnerer per-generator-data + samlet
4. Etter steg 10: åpne `/effektivitet/lindland`, dropdown viser G1/G2/Samlet, η-kurver visualiserbare

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-LINDLAND.md` med:
- Kommit-tabell per steg
- SignalMap-fordeling for Lindland (faktisk antall per kategori)
- Drivdal-regresjons-resultat (skal være identisk)
- Hvilke tags ble mappet til Other (for review)
- Sammenligning av G1 vs G2 effektivitet for jan-2026

Foreslåtte oppfølginger:
- Honnefoss/Liavatn/Øgreyfoss SCADA-import (kommende — kanskje også multi-generator?)
- Per-generator-vakt-ROI (når én generator har feil mens andre kjører)
- Generator-slitasje-deteksjon basert på effektivitets-trend
