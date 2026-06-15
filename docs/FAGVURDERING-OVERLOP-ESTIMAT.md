# Fagvurdering: Overløpsestimat — er det godt nok til å publisere kroner?

**Dato:** 2026-06-12
**Bakgrunn:** FAGVURDERING-KPI-BEREGNINGER-2026-06-10.md lot overløpsestimatet stå udekket. SPEC-UI-ROLLEBASERT 3.2 («Overløp i NOK») ble utsatt til estimatet var fagvurdert. Dette er den vurderingen.
**Dommer:** SAMSVARER · AVVIKER BEVISST (dokumentert) · AVVIKER UBEVISST (svakhet uten begrunnelse)

---

## Hovedfunn: det er TO ulike «overløp», ikke ett

Dette er den viktigste avklaringen, og den endrer hva 3.2 skal bygge på.

| Begrep | Hva | Hvor brukt | Datakilde |
|---|---|---|---|
| **Counterfactual-overløp** | «Ville magasinet gått fullt hvis turbinen sto HELE counterfactual-vinduet?» (turbin AV) | Vakt-ROI (overflow-v2, `InflowOverflowEstimator`) | Tilsigsmodell (volum-derivat + lookback) |
| **Observert overløp** | «Hvor mye rant faktisk forbi MENS turbinen kjørte?» (turbin PÅ) = ekte, tapt produksjon | Produksjon-siden (timer/%) — og det 3.2 egentlig trenger | SCADA OverflowFlow-tag |

**Konsekvens:** 3.2 «Overløp i NOK» handler om *faktisk tapt produksjon i perioden* — altså **observert** overløp, ikke counterfactual-tilsigsmodellen. Å bygge 3.2 på `InflowOverflowEstimator` ville vært feil begrep. De to må vurderes hver for seg.

---

## Del 1 — Counterfactual-modellen (`InflowOverflowEstimator`), for Vakt-ROI

**Kode:** `Modules.Reporting/Nedetid/InflowOverflowEstimator.cs`

**Modell (massebalanse):**
```
tilsig_t       ≈ ΔVolum/Δt + TotalDamFlow + TurbineFlow      (alt ut + endring i lager = inn)
netto_inn      = snitt_tilsig − snitt_TotalDamFlow            (turbin AV i counterfactual)
ledig_kap      = (1 − fyllgrad) × maks_volum
tid_til_fullt  = ledig_kap / (netto_inn × 3600)
overløpstimer  = max(0, vindu_h − tid_til_fullt)
```

| Vurdering | Dom |
|---|---|
| Massebalanse-utledningen (inn = Δlager + ut) | **SAMSVARER** — fysisk korrekt. |
| Turbin AV i counterfactual | **SAMSVARER** — riktig premiss (vakten reddet nettopp turbindriften). |
| Edge-guards (< 2 volum-samples, maks-vol/fyllgrad mangler → DataAvailable=false; netto ≤ 0 → 0) | **SAMSVARER** — fabrikkerer ikke tall uten grunnlag. |
| Konstant tilsig = 24 t lookback-snitt over opptil ~58 t vindu | **AVVIKER BEVISST** — dokumentert i spec. Svakt over lange vinduer; i flom/snøsmelting underestimerer lookback stigende tilsig → **konservativt i riktig retning** for et «vakt reddet»-tall. |
| Konstant dam-luke (TotalDamFlow holdes på lookback-snitt) | **AVVIKER UBEVISST** — i en reell lang stans ville operatør trolig justert luker (flomavledning). Modellen antar uendret. |
| Volum-derivat fra ReservoirVolume-tag | **AVVIKER UBEVISST** — magasin-volum (nivå→volum-kurve) er grovt/støyende. ΔVolum over 1 t × 3600 forsterker støy til m³/s. Snitt over 24 t demper, men par-differansene er ikke robuste mot uteliggere. |
| Fyllgrad = siste enkelt-sample før vindu | **AVVIKER UBEVISST** — ingen glatting; en sensor-glitch akkurat der treffer ledig_kap direkte. |

**Dom Del 1:** Modellen er **forsvarlig som beslutningsstøtte når den er merket «~»** (slik den er i Vakt-ROI-dialogen). Den er konservativ i riktig retning og fabrikkerer ikke tall. Den er **ikke** kalibrert/validert mot faktiske overløpshendelser, så den egner seg ikke som autoritativt tall alene.

---

## Del 2 — Observert overløpstap (det 3.2 faktisk trenger)

**Hva 3.2 krever:** ekte tapt produksjon = vann som rant forbi turbinen mens den kjørte, verdsatt i kroner:
```
overløpstap_NOK = Σ_t [ OverflowFlow_t (m³/s) × 3600 × EnergyEquiv (kWh/m³) / 1000 × spot_t (NOK/MWh) ]
```

| Komponent | Status / vurdering |
|---|---|
| `OverflowFlow`-tag (m³/s) per time | Finnes for anlegg med magasin-telemetri, men **patchy** — flere anlegg mangler scada-fine (Ørsdalen/Stølskraft), og dagens `OverflowQueryService` returnerer **kun overløps-TIMER (boolsk sett), ikke flow-volum**. 3.2 trenger flow-verdiene, ikke bare timene. |
| `EnergyEquivalentKwhPerM3` (kWh/m³) | Ligger på `PlantDto` per anlegg — men er en **statisk konfig-verdi** (avhenger av fallhøyde × virkningsgrad). Hvis feil/umålt for et anlegg, blir NOK-tallet feil. |
| Spotpris per time | **SAMSVARER** — pålitelig (samme kilde som CR/nedetidstap). |

**Dom Del 2:** Den *direkte* observerte beregningen er **mer validerbar enn tilsigsmodellen** (måler faktisk spill, ikke et hypotetisk), MEN:
- krever at man leser **flow-verdier** (ny query-utvidelse, ikke bare timer),
- avhenger av `EnergyEquivalentKwhPerM3` som ikke er verifisert per anlegg,
- mangler data for de samme anleggene som ellers har scada-hull.

---

## Anbefaling for 3.2

**Kan bygges trygt nå — med disse forbeholdene:**

1. **Bruk observert overløp** (`OverflowFlow` × energi-ekvivalent × spot), IKKE counterfactual-estimatet.
2. **Bygg query-utvidelsen** som henter flow-volum (ikke bare overløpstimer) — ny metode i overløps-query-tjenesten + enhetstester på m³→MWh→NOK-konverteringen.
3. **Vis bare for anlegg med `OverflowDataTilgjengelig = true` OG `EnergyEquivalentKwhPerM3` satt.** Andre anlegg: vis «–» / «mangler data», ikke 0.
4. **Merk tallet «~»** med tooltip: «Estimat: observert overløp × energi-ekvivalent × spot. Avhenger av anleggets kWh/m³-konfig og SCADA-dekning.»
5. **Verifiser `EnergyEquivalentKwhPerM3`** for de 2–3 anleggene 3.2 først vises for, mot fallhøyde × turbinkurve, før tallet vises i Portefølje-kolonnen (der det inviterer til sammenligning).

**Dette gjenstår før kroner kan publiseres som autoritativt (ikke «~»):** kalibrering av minst ett anlegg mot kjent faktisk spill-volum (f.eks. en flomperiode med målt overløp), og bekreftelse av energi-ekvivalentene.

## Samlet dom

- **Counterfactual-modellen (Vakt-ROI):** god nok som «~»-merket beslutningsstøtte — som i dag. Ikke autoritativ alene.
- **3.2 observert overløpstap:** **kan bygges** som et tydelig «~»-merket estimat for anlegg med flow-data + energi-ekvivalent, forutsatt query-utvidelse + per-anlegg-verifisering av kWh/m³. Ikke som et upresisert headline-pengetall ennå.
