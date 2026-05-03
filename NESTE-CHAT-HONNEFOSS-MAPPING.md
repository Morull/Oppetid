# Neste sesjon — Honnefoss SCADA-mapping

**Dato opprettet:** 2026-05-03
**Spec:** `docs/SPEC-HONNEFOSS-MAPPING.md`
**Estimat:** 4-6 timer (etter at bruker har bekreftet topology)
**Status:** Kaskade-dammer-implementasjon ferdig — bygger på den

## Mål

Importere Honnefoss SCADA-eksport (113 tags) og få anlegget operativt med alle KPI-moduler. Honnefoss er **én-generator-anlegg** så multi-generator-utvidelse ikke nødvendig.

## To viktige funn fra eksport-analysen

1. **Eksporten har 6 magasin-prefikser, ikke 4** — REVSVT (Revsvatn) og NODLANDVT (Nodlandvatn) er fullt instrumenterte regulerte dammer som ikke vises i SCADA-skjermbildet brukeren sendte. Krever bekreftelse på topology.

2. **KYDLNDVT og INNTAK er sannsynligvis felles vannflate** (begge -29.0 cm samtidig). Anbefalt å behandle som én dam (`honnefoss_inntak`) der tags fra begge prefikser fordeles etter funksjon.

## BRUKER-INPUT REQUIRED før implementasjon

| Punkt | Spørsmål | Default-antakelse |
|---|---|---|
| Revsvatn topology | Hvor i kaskaden er den? Mater den inn til Liavatn (oppstrøms) eller direkte til Inntak? | Pos 1 (parallelt med Liavatn) |
| Nodlandvatn topology | Samme spørsmål | Pos 2 (mellom Liavatn og Inntak) |
| KYDLNDVT/INNTAK-håndtering | Felles magasin (én dam) eller to separate? | Felles — én dam `honnefoss_inntak` |
| G1 installert kapasitet | I MW | Krever input |
| HRV/LRV per dam | Konsesjonsdata | Krever input — settes via PlantAdmin etter steg 1 |

**Stopp-policy:** ikke fortsett med seeder-koding før topology er bekreftet. Feil topology gir feil overløps-rapportering og dermed feil vakt-ROI for Honnefoss.

## Bekreftet topology (fra SCADA-skjerm — 4 dammer)

```
Liavatn (-183 cm, 67.5%)         Spjodevatn (-58 cm, 93.1%)
       │                                │
       ▼                                ▼
Kydlandsvatn (-29 cm, 92.1%) ⇄ ⇄ Inntak (-29 cm, 95.8%)
                                       │
                                       ▼
                                     G1 (2043 kW, 247 kVAr)
```

## Foreslått full topology (krever bruker-bekreftelse)

```
Revsvatn ──────────┐
                   │
Liavatn ───────┐   ▼
               │  Nodlandvatn ─────┐
               ▼                   │
       Kydlandsvatn ⇄ Inntak ◄────┘
                       │
                       ▼
                      G1   (Spjodevatn ─→ ?)
```

## Tag-prefiks-fordeling fra eksport

| Prefiks | Tags | Type |
|---|---|---|
| LIAVT | 25 | Liavatn (regulert, 2 luker) |
| NODLANDVT | 25 | Nodlandvatn (regulert, 2 luker) — IKKE i skjermbilde |
| REVSVT | 16 | Revsvatn (1 luke) — IKKE i skjermbilde |
| SPJODEVT | 16 | Spjodevatn (1 luke) |
| G1 | 16 | Generator |
| KYDLNDVT | 8 | Kydlandsvatn (måle-dam, ingen luke) |
| INNTAK | 5 | Inntaks-aggregater (eldre navnekonvensjon) |
| KRST | 1 | Kraftstasjon kommunikasjon |
| NETT | 1 | Linjespenning |

**Anlegg-prefiks er `HONNE_`** (ikke `HONNEFOSS_`).

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Antall dammer | 5 dammer (etter sammen-slåing av Kydlandsvatn + Inntak) |
| Terminal-dam | `honnefoss_inntak` (mottar tags fra både KYDLNDVT- og INNTAK-prefiks) |
| Generator | Én — `honnefoss_g1` |
| INNTAK eldre navnekonvensjon | Eksplisitt mapping i seeder, ikke regex |
| Lekkasje på Nodlandvatn | Other-rolle med StoreSamples=true |
| Mangler FALLHOYDE-tag | Beregn fra `RORGATE_VANN_TRYKK − TURB_VANN_TRYKK` hvis trengs |

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 0 | Bruker bekrefter topology for REVSVT/NODLANDVT + KYDLNDVT/INNTAK | – | **STOPP** |
| 1 | Backfill `core.dams` med 5 dammer | 30 min | – |
| 2 | `HonnefossSignalMapSeeder` + 14 tester | 3 t | – |
| 3 | Drag-drop import + verifiser tag-fordeling | 30 min | **STOPP — rapporter SignalMap-fordeling** |
| 4 | Smoke-test vakt-ROI | 1 t | – |

## Spørre-policy

- Hvis bruker ikke har bekreftet topology: pause
- Hvis seeder mapper > 5 % av tags som "Other" (utover bevisste avvik): rapporter listen for review
- Hvis Drivdal-regresjon brytes etter dam-backfill: STOPP — kan indikere at backfill påvirket eksisterende data

## Verifikasjon

```powershell
# 1. Verifiser dammer
psql -d kraftverkuptime -c "SELECT dam_id, cascade_position, is_turbine_intake FROM core.dams WHERE plant_id='honnefoss' ORDER BY cascade_position;"
# Forventet: 5 rader, én med is_turbine_intake=true

# 2. Verifiser SignalMap-fordeling etter import
psql -d kraftverkuptime -c "
SELECT dam_id, generator_id, COUNT(*) AS tags FROM core.signal_map
WHERE plant_id='honnefoss' GROUP BY dam_id, generator_id;
"
# Forventet: ~113 tags fordelt på 5 dammer + g1 + null (KRST/NETT)

# 3. Smoke-test vakt-ROI
curl.exe "http://localhost:5080/api/v1/plants/honnefoss/vakt-roi?from=2026-04-01&to=2026-05-01"
```

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-HONNEFOSS.md` med:
- Topology-bekreftelse (hva ble valgt for REVSVT/NODLANDVT)
- SignalMap-fordeling per dam og generator
- Tags som ble mappet til "Other" (for review)
- Drivdal-regresjons-resultat
