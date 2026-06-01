# Fagvurdering — start/stopp-budsjett, vannverdi-modell, P50/P90

**Dato:** 2026-05-22
**Skrevet for:** Drifts-leder Dalane Kraft, som beslutningsgrunnlag før instrukser eventuelt gis til Code.
**Status:** Litteraturgjennomgang + scope-vurdering. Ingen kode foreslått ennå.

---

## 1. Start/stopp-budsjett per anlegg — fagvurdering

### Hva forskningen sier

**Historisk drift:** Francis-turbiner ble historisk konstruert for «få dusin» start/stopp-sykler i året (Mandag-start / Fredag-stopp-mønsteret) ([Nature Communications 2025](https://www.nature.com/articles/s41467-025-58229-z)). Som drifts-leder vet du dette — det er dimensjoneringsfilosofien fra 70- og 80-tallet.

**Dagens drift:** Med fornybar-intermittens kan moderne turbiner kjøre opp mot **500 sykler per år**, en størrelsesorden over designforutsetningen ([Springer 2023 — Start/stop Cost Evaluation of a Francis Turbine Runner](https://link.springer.com/chapter/10.1007/978-3-031-25448-2_25)).

**Skademekanisme — bekreftet i flere uavhengige studier:**
- *Low-cycle fatigue* fra selve oppstartsprosessen (lastendring 0 → nominell på minutter)
- *High-cycle fatigue* fra rotor-stator-interaksjon under drift
- Kombinasjonen gir sprekkdannelse på **løpehjuls-bladene** som dominerende failure-modus ([ScienceDirect 2021 — Damage due to start-stop cycles of turbine runners under high-cycle fatigue](https://www.sciencedirect.com/science/article/abs/pii/S0142112321003169); [ScienceDirect — Failure analysis of runner blades in a Francis hydraulic turbine](https://www.sciencedirect.com/science/article/abs/pii/S1350630715301205))

**Kostnad per syklus:** Litteraturen rapporterer **$200–$250 per syklus** (ca. 2 200–2 700 NOK) som bredt anvendt estimat for slitasje-/vedlikeholds-kost, men dette er en grov gjennomsnittsverdi som varierer kraftig med:
- Turbinstørrelse og fallhøyde
- Alder og design-spesifikasjoner
- Driftsmønster (kald vs varm start, hastighet på rampe)

To metodikker for å regne mer presist ([Springer 2023, samme kilde som over](https://link.springer.com/chapter/10.1007/978-3-031-25448-2_25)):
1. **Modifisert vedlikeholds-/erstatnings-tilnærming:** beregner kost fra forventet inspeksjons- og reparasjonsfrekvens, fordelt over forventet syklus-antall.
2. **Reliability-basert modell:** bruker fatigue-modellen og redusert pålitelighet/levetid til å regne kost.

### Hva som er anerkjent i norsk kontekst

**SINTEF/NTNU er de norske referansene:**
- **HydroFlex** — EU Horizon 2020-prosjekt (2018-2022, 5,7 mEUR), NTNU-koordinert. Mål: redusere fatigue-skade ved transient drift gjennom variabel hastighet ([NTNU HydroFlex](https://www.ntnu.edu/web/norwegian-hydropower-center/hydroflex)).
- **FME HydroCen** — Forskningssenter for miljøvennlig energi, fokuserer på neste-generasjons turbinkonstruksjon for fleksibel drift ([NTNU HydroCen 2.5](https://www.ntnu.edu/hydrocen/flexible-hydropower-unit)).
- **Waterpower Laboratory ved NTNU** — eksperimentell base for tøyningsmålinger på modell-turbiner ([NTNU Waterpower Lab](https://www.ntnu.edu/ept/laboratories/waterpower)).

**Industri-praksis (Statkraft, Vattenfall):** Bruker SHOP/ProdRisk-modeller (SINTEF) som inkluderer **eksplisitt start-up-kostnad** som beslutningsvariabel ved planlegging ([SINTEF SHOP-bloggen](https://blog.sintef.com/energy/shop-hydropower-scheduling-creates-value/)). Det er industri-konsensus om at start/stopp-kost skal med i optimeringen — diskusjonen er om størrelsen og hvordan den modelleres.

### Hva det betyr for Dalane Kraft

**Pålitelig:** Ja, start/stopp-slitasje på Francis-turbiner er en *empirisk veletablert* skademekanisme. Det er ikke spekulativt.

**Et samlet «syklus-budsjett» per år er imidlertid ikke standardisert i en norm.** Det er ikke som lager-levetid (L10) i ISO. Tallene i litteraturen er forsknings-estimater eller OEM-spesifikke anbefalinger, ikke en bransje-norm. Hvis dere skal sette et konkret «budsjett» bør det enten:
- (a) Hentes fra OEM-dokumentasjonen for hver turbin (Voith, Rainpower, Andritz osv. har dette) — mest pålitelig.
- (b) Estimeres reliability-basert som $\text{syklus-budsjett} = \text{forventet løpehjul-levetid} / \text{forventet syklus-bidrag til skade}$.

**Anbefaling for appen:**
1. **Steg 1:** Logg start/stopp-sykler per anlegg per år (krever ingen nye sensorer — dataene finnes allerede i 15-min-intervallene).
2. **Steg 2:** Vis det som en trend mot tidligere år. Selv uten et eksakt budsjett er en plutselig dobling av sykler verdt å varsle om.
3. **Steg 3:** Når dere har OEM-tall fra hver turbinleverandør, sett budsjettet og vis «brukt 45 av 80» med fargekoder.

Steg 1-2 er en ren KPI som kan bygges på dagens data. Steg 3 krever drifts-leders egen kunnskap om OEM-anbefaling per turbin og kan rulles ut når dere har tallene.

**Konkret KPI-design som er forskningsmessig forsvarbar:**

| KPI | Datakilde | Verdi |
|---|---|---|
| Antall starter ÅTD per anlegg | 15-min-intervaller (allerede beregnet som Start/stopp-overganger) | Faktisk telling |
| Glidende 12-mnd snitt | Samme | Baseline |
| Trend (% vs samme periode i fjor) | Samme | Tidlig varsel |
| **OEM-budsjett (når satt)** | Anleggs-admin | Per turbin |
| **Brukt-prosent vs budsjett** | Beregnet | Grønn/gul/rød ved 60/80 % |

Implementasjons-estimat når dere bestemmer dere: **0,5-1 dag** for steg 1-2 (KPI-kort på Effektivitet-fanen). Steg 3 er triviell utvidelse når OEM-tallene er innhentet.

---

## 2. Vannverdi-modell og bedre disponering — fagvurdering

### Hvor avansert er dette egentlig

Det finnes tre realistiske ambisjonsnivåer, hver med veldig forskjellig kompleksitet:

**Nivå 1 — Etterbetraktning («hva ville vi fått»):**
For hver historisk time, beregn snittpris × MWh ute av vannet vs hva samme MWh ville gitt om man ventet 24/48/168 timer. Krever **kun** spotpris-historikk og faktisk produksjons-tidsstempel — dette har dere allerede. Gir ikke planlegging fremover, men avdekker mønstre («vi taper 8 % på å kjøre på morgenmaks i stedet for kveldsmaks»).

**Estimat:** 2-3 dager. Bygger på eksisterende data.

**Nivå 2 — Sammenligning mot Hydrogrid-planen («er planen god nok»):**
Logg Hydrogrid sine plan-tall mot faktisk realisert. Beregn «merverdi tapt ved å avvike fra plan» kontra «merverdi tapt ved å følge en dårlig plan». Krever ingen ny modellering — bare to tidsserier å sammenligne. Dette er nært beslektet med Tapsregnskap-arbeidet som allerede er anbefalt.

**Estimat:** Bygd inn som del av Tapsregnskap punkt 1 (Netto mot plan). Ingen separat investering.

**Nivå 3 — Ekte vannverdi-modell («bedre plan enn Hydrogrid»):**
Her snakker vi om stokastisk optimering med SDDP (Stochastic Dual Dynamic Programming) — samme metodikk som **SINTEF ProdRisk** ([SINTEF ProdRisk-overview](https://www.sintef.no/en/software/prodrisk/)). ProdRisk brukes av de største nordiske kraftprodusentene og er standarden for langsiktig planlegging.

Kort om hva som kreves matematisk:
- Tilstand: magasinvolum, sesonglig tilsigsprognose, prisprognose
- Beslutning per tidssteg: produser nå eller magasiner
- Stokastisk: tilsig og pris er usikre fremover
- Verdifunksjon: marginalverdi av vann i magasinet i hver tilstand

Dette er et **MSc-nivå optimeringsprosjekt** når man skal bygge fra bunn. SINTEF har dokumentert metodikken i åpne publikasjoner ([SINTEF — A stochastic dynamic programming model for hydropower scheduling with state-dependent maximum discharge constraints, 2023](https://www.sintef.no/en/publications/publication/2034721/); [SINTEF — Snow Storage Information in Hydropower Scheduling, ProdRisk-applikasjon, 2024](https://www.researchgate.net/publication/385905926_Snow_Storage_Information_in_Hydropower_Scheduling_With_application_in_the_SDDP-based_ProdRisk_model)).

Realistiske valg på Nivå 3:
- **A) Bruk ProdRisk direkte.** Lisens fra SINTEF, krever dataintegrasjon og opplæring. Industrially proven, men ikke gratis. Sannsynlig overkill for 11 små anlegg under 10 MW hver.
- **B) Bygg en forenklet modell.** Single-reservoir, deterministisk pris-prognose, stokastisk tilsig. Implementér med Python (`gurobipy`/`pyomo`) eller .NET-tilsvarende. Tar 2-4 uker for en kompetent ingeniør. Gir 70-80 % av gevinsten på 20 % av kostnaden.
- **C) Vent.** Hvis Hydrogrid leverer planer som er innenfor 5 % av optimum, er ROI på egen modell marginalt.

### Dataene dere har vs trenger

| Data | Status | Kommentar |
|---|---|---|
| Spotpris-historikk per time | ✓ Har | Hentet fra settlement |
| Faktisk produksjon per anlegg per time (Elhub) | ✓ Har | |
| Magasinfyllingsgrad over tid | ✓ Har via SCADA (`*_MAGASIN_FYLLGRAD_PV` på Øgreyfoss) | Sjekk om logges trendbasert, ikke bare aktuell verdi |
| Tilsig per anlegg | ? Delvis | Vannføring inn er ikke alltid målt direkte; ofte avledet fra fyllgrads-endring + produksjon |
| Vannverdi-anslag (NOK/m³) | ✗ Mangler | Selve outputen fra modellen |
| Tilsigs-prognose | ✗ Mangler | Krever NVE-/met-data eller egen prognose |
| Sesongmønster for tilsig (historisk) | ✓ Kan utledes | Hvis dere har 3+ år med magasin- og produksjons-data |
| Spotpris-prognose fremover | ? Delvis | Hydrogrid har plan; dere kan også kjøpe forwards eller bruke EPEX-prognoser |
| Snøreserve-data | Avhengig av nedslagsfelt | NVE Snokart kan gi noe; lokale snømålere bedre |

**Konklusjon på data:** For Nivå 1 har dere alt. For Nivå 2 har dere alt minus eksplisitt logging av Hydrogrid-plantall mot faktisk. For Nivå 3 mangler dere tilsigs-prognose og kvalitets-data på snø/nedslagsfelt for noen av anleggene.

### Min anbefaling

**Begynn med Nivå 1.** Det krever ingen ny investering og avdekker hvor mye dere faktisk taper på timing. Tar 2-3 dager å bygge.

**Vurder deretter om dere ser timing-tap som er stort nok til å rettferdiggjøre Nivå 3-B.** Hvis Nivå 1 viser at dere typisk taper 2-3 % på timing er Nivå 3 ikke verdt det. Hvis det viser 10 %+ er det en gevinst på flere millioner i året, og en forenklet modell er forsvarlig.

**Nivå 3-A (ProdRisk-lisens) er sannsynligvis overkill** for porteføljen deres — den er bygget for store, multi-reservoir-systemer der den marginale modelleringsverdien er stor nok til å forsvare investeringen.

---

## 3. P50/P90 — produksjonsutbytte mot sannsynlighet

### Hva det er — kort

P50 = median forventet produksjon (50 % sjanse for å overstige). P90 = konservativt anslag — det nivået faktisk produksjon overstiger 90 % av årene ([DNV — Terminology explained: P10, P50 and P90](https://www.dnv.com/article/terminology-explained-p10-p50-and-p90-202611/); [Renewables Valuation Institute — How to Model P50, P75, P90, P99](https://courses.renewablesvaluationinstitute.com/pages/academy/how-to-model-p50-p75-and-p90-energy-yield)).

Brukes typisk i:
- **Finansieringsvurderinger** — bankene vil se hvor stor sjanse det er for å nå inntektsforutsetningene. Lav-P90 dekker rentebetjening.
- **Forsikrings-vurdering** — hvor stor er nedside-risikoen i et dårlig år.
- **Investerings-beslutninger** — hvor robust er businesscasen.

### Hvor mye historikk trengs

| Estimat-kvalitet | Datagrunnlag | Hva dere har |
|---|---|---|
| Crude | 1-2 år historikk + variasjonsestimat | ? — sannsynligvis nok hvis 2025 er fullført |
| Akseptabel | 3-5 år | Trolig manuelt registrert hvis dere har det |
| Solid | 10+ år | Krever lang historie eller normalisering mot hydrologiske referanseserier |

For norsk vannkraft er hydrologiske data tilgjengelige fra NVE 50+ år tilbake. Selv om appen kun har 4-5 måneders direktedata, kan P50/P90 fortsatt bygges hvis dere normaliserer mot tilsigs-/nedbørstatistikk for hvert nedslagsfelt.

### Implementasjonsestimat

**Backward-looking (basert på eksisterende data):**
- Hent månedlig produksjon per anlegg over de månedene som finnes
- Anta normalfordeling eller bruk historisk variasjon
- Beregn P10/P50/P90 per anlegg per måned
- Vis som «forventet produksjon X MWh; P90 Y MWh» på Anlegg-fanen eller som tilleggsinfo på normal årsproduksjon-feltet
- **Estimat:** 1-2 dager hvis dere godtar at usikkerheten er stor med kort historikk.

**Forward-looking probabilistisk (mer ambisiøst):**
- Kobler P50/P90 til hydrologisk prognose
- Krever multi-år historie + tilsigs-modellering
- **Estimat:** 1-2 ukers prosjekt + kompetanse på hydrologi.

### Min anbefaling

Bygg backward-looking først som en enkel utvidelse av eksisterende normal årsproduksjon-feltet — vis P50 og P90 i tillegg til middelverdien. Dette tar 1-2 dager og gir umiddelbart en verdi (særlig for finansieringssamtaler og styre-rapportering).

Forward-looking er et eget prosjekt og bør vente til dere har 2+ år komplett data i systemet. Da gir den vesentlig bedre verdi.

---

## Samlet anbefaling om rekkefølge

| # | Område | Innsats | Verdi | Rekkefølge |
|---|---|---|---|---|
| 1 | Start/stopp-budsjett, steg 1-2 (KPI uten OEM-tall) | 0,5-1 dag | Tidlig varsel om driftsendringer | **Først — billigst, hurtigst** |
| 2 | P50/P90 backward-looking | 1-2 dager | Bedre finansieringsgrunnlag, rapport | Andre — enkel utvidelse |
| 3 | Vannverdi Nivå 1 (etterbetraktning timing-tap) | 2-3 dager | Avdekker hvor mye dere faktisk taper på timing — beslutningsgrunnlag for Nivå 2/3 | Tredje |
| 4 | Tapsregnskap-arbeid | 1-2 uker | Stor (allerede i kø) | Som planlagt |
| 5 | Vannverdi Nivå 3-B (forenklet stokastisk modell) | 2-4 uker | Avhenger av punkt 3-resultater | Bare hvis Nivå 1 viser >5 % timing-tap |

Punkt 1-3 utgjør ca **én ukes arbeid** og legger grunnlaget for å vurdere de større investeringene (punkt 4-5). Lav risiko, lav kost, god utbytteinformasjon.

---

## Begrensninger og forbehold

- Jeg er ikke vannkraft-ingeniør. Tallene over start/stopp-kost (~200-250 USD per syklus) er fra forsknings-litteraturen og bør verifiseres mot deres faktiske OEM-dokumentasjon før de brukes som beslutningsgrunnlag i krone-tall.
- Vannverdi-modellering er et eget fagfelt med 50+ års norsk historie. Hvis dere ender med å bygge Nivå 3 anbefales sterkt å konsultere SINTEF eller en konsulent med ProdRisk-erfaring i designfasen.
- P50/P90-metodikken antar typisk normalfordeling. For små anlegg med klimatisk variasjon kan halene være tyngre enn normalt — verifiser fordelingen før dere stoler på P90-tallet.

## Kilder

**Start/stopp:**
- [Springer 2023 — Start/stop Cost Evaluation of a Francis Turbine Runner Based on Reliability](https://link.springer.com/chapter/10.1007/978-3-031-25448-2_25)
- [ScienceDirect 2021 — Damage due to start-stop cycles of turbine runners under high-cycle fatigue](https://www.sciencedirect.com/science/article/abs/pii/S0142112321003169)
- [Nature Communications 2025 — Fatigue damage reduction in hydropower startups with machine learning](https://www.nature.com/articles/s41467-025-58229-z)
- [ScienceDirect — Failure analysis of runner blades in a Francis hydraulic turbine](https://www.sciencedirect.com/science/article/abs/pii/S1350630715301205)
- [ScienceDirect — Fatigue life estimation of Francis turbines based on experimental strain measurements](https://www.sciencedirect.com/science/article/abs/pii/S1364032118307974)
- [NTNU HydroFlex — Increasing the value of Hydropower through increased Flexibility](https://www.ntnu.edu/web/norwegian-hydropower-center/hydroflex)
- [NTNU HydroCen 2.5 — Flexible hydropower unit](https://www.ntnu.edu/hydrocen/flexible-hydropower-unit)
- [NTNU Waterpower Laboratory](https://www.ntnu.edu/ept/laboratories/waterpower)

**Vannverdi:**
- [SINTEF ProdRisk](https://www.sintef.no/en/software/prodrisk/)
- [SINTEF — A stochastic dynamic programming model for hydropower scheduling with state-dependent maximum discharge constraints (2023)](https://www.sintef.no/en/publications/publication/2034721/)
- [SINTEF Blog — SHOP: How hydropower scheduling creates value now and in the future](https://blog.sintef.com/energy/shop-hydropower-scheduling-creates-value/)
- [Snow Storage Information in Hydropower Scheduling, ProdRisk-applikasjon (2024)](https://www.researchgate.net/publication/385905926_Snow_Storage_Information_in_Hydropower_Scheduling_With_application_in_the_SDDP-based_ProdRisk_model)

**P50/P90:**
- [DNV — Terminology explained: P10, P50 and P90](https://www.dnv.com/article/terminology-explained-p10-p50-and-p90-202611/)
- [Renewables Valuation Institute — How to Model P50, P75, P90, P99 Energy Yields](https://courses.renewablesvaluationinstitute.com/pages/academy/how-to-model-p50-p75-and-p90-energy-yield)
- [Renewable Space — P90/P75/P50: Interpreting exceedance probabilities](https://renewablespace.wordpress.com/2018/07/31/p90-p75-p50-what-do-these-exceedance-probabilities-mean/)
