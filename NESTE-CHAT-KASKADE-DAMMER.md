# Neste sesjon — Kaskade-dammer + Haukland-mapping

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-KASKADE-DAMMER.md`
**Estimat:** 1.5-2 dager
**Forrige sesjon:** `OVERLEVERING-2026-04-29-OVERLOP.md` (vakt-ROI med overløp levert for Drivdal)
**Parallell sesjon:** `NESTE-CHAT-CAPTURE-RATE.md` (CR-implementasjon — uavhengig av denne)

## Mål

Innfør kaskade-modell i datalaget slik at alle 11 anlegg fungerer korrekt med vakt-ROI med overløp:

1. **Ny `Dam`-entity** per anlegg med `IsTurbineIntake`-markør for terminal-dam (siste før turbin)
2. **`SignalMap` får `DamId`-felt** slik at hver tag knyttes til riktig dam
3. **OverflowQueryService oppdateres** til å bare bruke overløp-tag fra terminal-dam — ikke summere på tvers
4. **Haukland SCADA-eksport mappes** med 195 tags fordelt på 4 dammer + generator
5. **Baklengs-kompatibel** for Drivdal og andre én-dam-anlegg via backfill med default-dam

## Nåværende status

- Spec ferdigstilt og lagret som `docs/SPEC-KASKADE-DAMMER.md`
- Haukland SCADA-eksport mottatt og analysert: 195 tags, 4 dammer (Stølsvatn, Gjelevatn, Skårstemmevatn, Stemmevatn), én generator
- Drifts-leders korreksjon mottatt: "Overløpet er kun viktig for siste dam før kraftverket"
- Eksisterende vakt-ROI-implementasjon for Drivdal er konsistent med ny modell (én OVERLOP-tag på inntaket = terminal-dam) — ingen endring i atferd forventet etter migrering

## Bekreftet portefølje-topologi

| Anlegg | Topologi | Status |
|---|---|---|
| Drivdal | 1 dam | Aktiv (vakt-ROI fungerer) |
| Logjen | 1 dam | Avventer SCADA-data |
| Grødemfoss | 1 dam | Avventer SCADA-data |
| Ørsdalen | 1 dam | Avventer SCADA-data |
| Vikeså | 1 dam | Avventer SCADA-data + settlement |
| Stølskraft | 1 dam | Avventer SCADA-data + settlement |
| **Haukland** | **4 dammer (kaskade)** | **SCADA-eksport mottatt 2026-04-30** |
| Lindland | Flere dammer (kaskade) | Avventer SCADA-data |
| Honnefoss | Flere dammer (kaskade) | Avventer SCADA-data |
| Liavatn | Flere dammer (kaskade) | Avventer SCADA-data |
| Øgreyfoss | Flere dammer (kaskade) | Avventer SCADA-data |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Kaskade-modell | `Dam`-entity + `IsTurbineIntake`-markør på siste dam |
| Overløp-aggregering | **Kun terminal-dam teller**, ikke summering på tvers |
| Migrasjons-strategi | Backfill alle 11 eksisterende anlegg med én `<plant>_main`-dam, `IsTurbineIntake = true`. Eksisterende SignalMap-rader oppdateres med `dam_id = '<plant>_main'`. Baklengs-kompatibel. |
| Drivdal regresjon | Vakt-ROI-tall skal være identiske før/etter migrering. Verifiseres i steg 5. |
| Haukland-mapping | Auto-generert via `HauklandSignalMapSeeder` med suffix-baserte regler. 195 tags → 4 dam-grupper + null (generator). |
| Nye SignalRole | `GateFlow`, `GatePosition`, `TotalDamFlow`, `ReservoirVolume` (alle dam-knyttede) |
| Stemmevatn (Haukland) | Terminal-dam, mangler `LUKE1_VF_PV` (vannet går rett til turbin) — bekreftet av bruker |

## Bekreftede SCADA-avvik for Haukland

| Avvik | Håndtering |
|---|---|
| Stemmevatn mangler `LUKE1_VF_PV`/`LUKE1_POS_PV` | Forventet — vannet går rett til turbin G1. Ikke flag som DQ-issue. |
| Lekkasjemåling kun på Stølsvatn | Ignorer — ikke relevant for KPI-er. Tag mappes til `Other` med `StoreSamples = false`. |
| Ingen `FEIL_AL`/`STARTER_AL`/`STOPPER_AL` | Klassifikator faller tilbake på `GEN_P_PV`-fall for ForcedOutage-deteksjon. Annerledes enn Drivdal — verifiser at SCADA-klassifikator håndterer dette robust. |
| `STEMMEVT_KONTROLL_TOT_VF_PV` har enhet "None" | SCADA-konfigfeil. Forutsett m³/s, logg DQ-warning men aksepter import. |

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 1 | `Dam` entity + `core.dams`-tabell + migrering med backfill | 2-3 t | – |
| 2 | `SignalMap.DamId` utvidelse + migrering | 1 t | – |
| 3 | Nye SignalRole-verdier | 30 min | – |
| 4 | `GetTerminalDamAsync` + tester | 1 t | – |
| 5 | `OverflowQueryService` oppdatering + Drivdal-regresjonstest | 1-2 t | **Stopp og verifiser identiske Drivdal-tall** |
| 6 | `HauklandSignalMapSeeder` med auto-mapping + 13 tester | 4 t | – |
| 7 | Drag-drop Haukland SCADA-eksport, verifiser 195 rader fordelt riktig | 1 t | **Stopp og verifiser DamId-fordeling** |
| 8 | Haukland vakt-ROI smoke-test (krever HRV/LRV via admin) | 1 t | – |
| 9 | PlantAdmin-UI utvidelse for dammer | 2-3 t | – |
| 10 | (valgfri) Magasinstand-widget per dam | 3-4 t | Defer til senere sesjon |

## Brukerinput som kreves underveis

| Punkt | Når | Hvordan |
|---|---|---|
| HRV/LRV/Volum for Haukland 4 dammer | Etter steg 8 | Via `/plants/haukland/admin` UI når den er bygget i steg 9. Til steg 10 fungerer testene uten disse verdiene. |
| Tag-suffix-regler for andre kaskade-anlegg | Når deres SCADA-data kommer | Lag tilsvarende seeder per anlegg etter samme mal som Haukland |

## Spørre-policy

- Suffix-mappings for ukjente Haukland-tags: bruk `Other` med `StoreSamples = false`. Logg liste til konsoll for senere review.
- Hvis Drivdal-regresjons-tall avviker etter migrering: **stopp og rapporter** før Haukland-arbeidet starter.
- Hvis Haukland-import feiler med ukjent CSV-struktur: lagre full header til log og rapporter — ikke gjett.

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 5: `curl /api/v1/plants/drivdal/vakt-roi` returnerer **identiske tall** som dokumentert i `OVERLEVERING-2026-04-29-VEIKART.md` (Drivdal feb-2026: 13 events, 10 696 NOK reddet)
2. Etter steg 7: SQL `SELECT dam_id, role, COUNT(*) FROM signal_map WHERE plant_id='haukland' GROUP BY ...` viser 4 dam-grupperinger + null-gruppe, totalt 195 rader
3. Etter steg 8: Haukland vakt-ROI bruker kun `STEMMEVT_KONTROLL_MAG_OVLOP_PV` for overløps-sjekk (verifiser via debug-log eller telt-sample-størrelse)

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-KASKADE.md` med:
- Kommit-tabell per steg
- Test-status (forventet ~25 nye tester)
- Drivdal-regresjons-resultat (skal være identisk)
- Haukland signal-map-fordeling (forventet ~30 dam-tags + ~50 generator-tags + ~115 Other)
- Eventuelle avvik fra spec og rasjonale

Foreslåtte neste prioriteter etter denne:
- Lindland/Honnefoss/Liavatn/Øgreyfoss SCADA-import (når data kommer) med samme mønster
- Magasinstand-widget per dam (steg 10 hvis deferred)
- Vannverdi-modell (alternativkost, krever per-dam-volum-data)
