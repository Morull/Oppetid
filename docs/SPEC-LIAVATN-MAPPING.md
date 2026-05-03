# Spec: Liavatn-kraftverk SCADA-mapping

**Status:** Foreløpig — venter på dedikert SCADA-eksport for Liavatn-kraftverket (2026-05-03)
**Estimat:** 3-4 timer (når CSV er tilgjengelig)
**Avhengighet:** Kaskade-dammer-implementasjon ferdig

## Bakgrunn

Liavatn-kraftverket er et **separat anlegg** oppstrøms Honnefoss. SCADA-skjermbilde 2026-05-03 viser fire dammer i serie + én generator (G1). Vannet fra Liavatn-kraftverket renner videre ned til Honnefoss-anlegget.

**Viktig navnekollisjon:** "Liavatn" eksisterer som BÅDE et kraftverk OG et magasin i Honnefoss-anlegget. Disse er forskjellige fysiske enheter:

- **Liavatn-kraftverket** (anlegg-id `liavatnkraft`) — eget anlegg med egen turbin, egne dammer
- **Liavatn-magasinet i Honnefoss** (`honnefoss_liavatn_magasin`) — del av Honnefoss-anlegget

## Topology fra SCADA-skjermbilde

```
Revsvatn (-126.86 cm, 80.4%)
       │
       ▼
Nodlandsvatn (-166.8 cm, 69.7%, 2.61 m³/s)
       │
       ▼
Stokkurhølen (-5.0 cm)            ← lite tunnel-magasin
       │
       ▼
       G1 (216 kW, 27 kVAr, 2.5 m³/s, 10.24 m fallhøyde)
       │
       ▼
Liavatn (-183 cm, 67.5%, 3.05 m³/s)   ← terminal, mater videre ned til Honnefoss
```

Topology-merknader:
- G1 sitter mellom Stokkurhølen og Liavatn — Liavatn er **utløpsmagasinet** etter turbinen, ikke inntaket
- Stokkurhølen er liten (kun -5.0 cm vises, ingen fyllgrad — sannsynlig tunnel-mottaks-basseng)
- Fallhøyde 10.24 m er lavt — relativt lavt-fall-anlegg (217 kW × 8760 = ~1.9 GWh/år hvis kontinuerlig drift)
- Modus i bildet: Vannstandsregulering — anlegget regulerer vannstand, ikke effekt

## Foreslått dam-mapping (krever bekreftelse + SCADA-eksport for full tag-liste)

| dam_id | name | cascade_position | is_turbine_intake | is_regulated |
|---|---|---|---|---|
| `liavatnkraft_revsvatn` | Revsvatn | 1 | false | true |
| `liavatnkraft_nodlandsvatn` | Nodlandsvatn | 2 | false | true |
| `liavatnkraft_stokkurhølen` | Stokkurhølen | 3 (turbin-inntak) | **true** | true |
| `liavatnkraft_liavatn` | Liavatn (utløpsmagasin) | 4 (etter turbin) | false | true |

**Spesielt design-valg:** Stokkurhølen er `IsTurbineIntake = true` siden vannet går FRA Stokkurhølen TIL turbinen. Liavatn (utløp) er IKKE turbin-inntak — den er nedstrøms turbinen og fungerer som demper/utløp som så slipper vannet videre til Honnefoss.

**Konsekvens for vakt-ROI:** overløp på Stokkurhølen er det som koster penger (vannet renner forbi turbinen). Overløp på Liavatn (utløpsmagasin) er forventet — det er der vannet skal hen etter produksjon.

## Cross-plant vannflyt: Liavatn → Honnefoss

Liavatn-utløp (Liavatn-magasinet i Liavatn-kraftverket) er ekvivalent med Liavatn-magasinet oppstrøms i Honnefoss-anlegget. Dette betyr at:

- Driftsbeslutninger i Liavatn-kraftverket påvirker tilsig til Honnefoss
- Overløp ved Liavatn-utløp er problem for Honnefoss (mer vann enn de kan håndtere)
- Vakt-ROI for Honnefoss bør i prinsippet vurdere oppstrøms-bidrag fra Liavatn-kraftverkets drift

For v1: behandles som separate anlegg uten cross-plant-modellering. Cross-plant vannbalanse-modul er en fremtidig spec hvis det viser seg å være verdifullt.

## SCADA-import-strategi

To muligheter:

### Alternativ A: Egen Liavatn-SCADA-eksport

Be drifts-leder generere dedikert eksport fra SCADA-systemet for Liavatn-kraftverket alene. Tag-prefiks blir sannsynligvis `LIAVATN_` eller `LIAVATNKRAFT_`. Dette er den reneste tilnærmingen.

### Alternativ B: Splitt fra Honnefoss-eksporten

Honnefoss-eksporten inneholder allerede REVSVT- og NODLANDVT-tags. Hvis disse er hele datasettet for Liavatn-kraftverket (sjekk om STOKKURHOLEN- og generator-tags også finnes der), kan vi implementere en **multi-plant-importør** som splitter eksporten basert på prefiks-til-anlegg-mapping.

**Anbefaling:** spør drifts-leder om en dedikert eksport. Mer pålitelig og lettere å vedlikeholde. Multi-plant-importør kan komme senere som forbedring.

## Brukerinput som kreves før implementasjon

| Punkt | Status |
|---|---|
| Dedikert Liavatn SCADA-eksport (CSV) | ⏳ Venter — be drifts-leder generere |
| Tag-prefiks for Liavatn-kraftverket | ⏳ Bekreft (LIAVATN_, LIAVATNKRAFT_, eller annet) |
| G1 installert kapasitet | Kreves — bildet viser 216 kW operativt, men maks-kapasitet kan være høyere |
| HRV/LRV per dam | Kreves — fra konsesjonsdokumenter |
| Bekreftelse av topology | Stokkurhølen er turbin-inntak, Liavatn er utløpsmagasin? |

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 0 | Bruker leverer SCADA-eksport for Liavatn-kraftverket | utenfor scope |
| 1 | Backfill `core.dams` med 4 dammer + `core.plants` med Liavatn-kraftverket | 30 min |
| 2 | `LiavatnSignalMapSeeder` med tag-suffiks-regler | 2 t |
| 3 | Drag-drop import + verifiser fordeling | 30 min |
| 4 | Smoke-test vakt-ROI med Stokkurhølen som terminal-dam | 1 t |

**Estimat totalt:** 3-4 timer etter at dedikert SCADA-eksport er tilgjengelig.

## Referanser

- SCADA-skjerm 2026-05-03: viser 4 dammer + G1, 216 kW, 10.24 m fallhøyde
- Honnefoss-spec: `docs/SPEC-HONNEFOSS-MAPPING.md` — REVSVT/NODLANDVT som vises i Honnefoss-eksport tilhører Liavatn
- Bygger på kaskade-dammer-implementasjon
