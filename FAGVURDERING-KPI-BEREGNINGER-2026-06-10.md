# Fagvurdering: KPI-beregninger mot bransjestandard og regelverk
**Dato:** 2026-06-10
**Omfang:** Høy verdi-gruppen — tilgjengelighet (AF/FOR/EAF), capture rate/merverdi, ubalanse/RK, virkningsgrad. Formler trukket ut av koden og validert mot IEEE 762/NERC GADS, markedspraksis (value factor-litteratur), nordisk balanseringsregelverk (NBM/eSett) og IEC 60041/62006. De mest konsekvensrike påstandene er verifisert manuelt mot koden.
**Ikke dekket her:** start/stopp og vannverdi/P50-P90 (se FAGVURDERING-START-STOPP-VANNVERDI-P50P90.md), Vakt-ROI-modellens øvrige forutsetninger, overløpsestimatet.

**Dommer:** SAMSVARER · AVVIKER BEVISST (dokumentert valg) · AVVIKER UBEVISST (avvik uten begrunnelse — hovedfunnene)

---

## Hovedkonklusjon

Kjernen i økonomi-tallene er solid: **times-CR er lærebok-korrekt** og **Ubalansekost_NOK (KAIA-nettet) er énpris-korrekt**. Men tre KPI-familier har avvik som betyr noe:

1. **AF er ikke IEEE-AF** — den er matematisk identisk med 1−FOR og usammenlignbar med bransjetall.
2. **Ubalansepremien bygger på toprismodellen som ble avviklet i 2021** — Vakt-ROI-ubalansekomponenten kan være 5–20× for høy.
3. **Virkningsgrad-tallene har falsk presisjon** — η leses rått fra SCADA (manualen påstår ρgQH-beregning), fallhøyde er uobservert, og −2 pp-terskelen ligger innenfor støyen fra magasinvariasjon.

---

## 1. Tilgjengelighet (AF, FOR, EAF) — mot IEEE 762 / NERC GADS

**Kode:** `Modules.Classification/Kpi/UptimeKpiCalculator.cs`, klassifisering i `SettlementClassifier.cs`

| KPI | Koden | Standard | Dom |
|---|---|---|---|
| FOR | FOH/(FOH+SH) | FOH/(FOH+SH) | Formel SAMSVARER; input AVVIKER UBEVISST |
| AF | SH/(SH+FOH) | AH/PH (RS = tilgjengelig, kalendertimer i nevner) | **AVVIKER UBEVISST** |
| EAF | (PH−FOH−RU−POH−MOH−EFDH)/PH, EFDH=0,5×derating-timer | (AH−EUDH−EPDH−ESEDH)/PH, MW-veid EFDH | Delvis bevisst (0,5-proxy dokumentert), delvis ubevisst (RU-fradrag, IU-håndtering) |

**Hovedfunn (verifisert):**
- **AF ≡ 1−FOR eksakt** — begge bruker samme to tall (linje 77–79 vs 86–88). Kommentaren på linje 84–85 («Skiller seg fra (1−AF) ved at den ikke teller med Reserve/Planned/Maintenance») er feil om sin egen kode. De to KPI-ene er redundante.
- IEEE-AF regner ReserveShutdown som *tilgjengelig* og bruker kalenderperiode. Eksempel: SH=300, FOH=20, RS=400 → koden 93,8 %, IEEE 97,2 %. Med stor planlagt revisjon snur fortegnet (koden 95,2 % vs IEEE 55,6 %). **Tallene er ikke sammenlignbare med NVE/Energi Norge/GADS-statistikk**, og avviket er udokumentert — README hevder tvert imot IEEE-samsvar.
- **FOH-proxyen er markedsbasert** (Elhub=0 + spotbud>0): havari uten bud forsvinner fra FOR (underrapportering), uannotert planlagt stans med bud inflaterer FOR. Retningen avhenger av annoteringsdisiplin.
- **EAF:** RU (vannmangel) trekkes fra telleren uten begrunnelse — elvekraft i tørrår får kunstig lav EAF. IU-timer (datahull) telles som *tilgjengelige* — i strid med både IEC 61400-26 og kodens egen UnitState-doc («skal ALDRI tolkes som nedetid»).
- **Aggregeringsbug:** `EconomyReportQueryService.cs:267/274` — null-AF blir 0 og vektes inn med fulle PeriodHours. Én måned uten beregningsgrunnlag drar portefølje-AF fra ~0,98 mot ~0,65.
- **Vindparken:** ingen IEC 61400-26-implementasjon finnes; vind uten produksjon/bud blir «ReserveShutdown» — meningsløst.
- **Tre motstridende AF-definisjoner i dokumentasjonen:** koden (SH/(SH+FOH)), manualen ((InService+PlannedOutage)/TotalHours — feil også mot IEEE) og Begreper.cs (IEEE-aktig tidsandel).

**Kilder:** [NERC GADS DRI Appendix F](https://www.nerc.com/pa/RAPA/gads/DataReportingInstructions/Appendix_F_Equations_2023_DRI.pdf) · [IEEE 762-2006](https://standards.ieee.org/ieee/762/3639/) · [IEC 61400-26-1:2019](https://webstore.iec.ch/en/publication/62548) · [DNV GL: Availability Terms for Wind](https://www.ourenergypolicy.org/wp-content/uploads/2017/08/Definitions-of-availability-terms-for-the-wind-industry-white-paper-09-08-2017.pdf)

---

## 2. Capture rate og merverdi — mot markedspraksis (value factor)

**Kode:** `Modules.Reporting/CaptureRate/CaptureRateCalculator.cs`, `CaptureRateQueryService.cs`

| Beregning | Dom |
|---|---|
| Times-CR: Σ(MWh×spot)/ΣMWh ÷ aritmetisk snittspot | **SAMSVARER** — identisk med value factor-standarden (Hirth 2013, Pexapark/KYOS/Volue-praksis). Direkte sammenlignbar med Hydrogrid/megler-tall. |
| Timing-merverdi NOK | **SAMSVARER** — standard dekomponering, fortegnsinvariant testet. |
| Realisert vs spot (utførelses-gap) | **SAMSVARER** — riktig holdt adskilt fra CR. |
| Dag-CR med P5/P95-filter | **AVVIKER BEVISST** — dokumentert Excel-arv (SPEC-CAPTURE-RATE.md). Ikke markedspraksis; kaster ~10 % av dagene og demper nettopp signalet fra dagene der fleksibilitet har størst verdi. Må aldri rapporteres eksternt som «capture rate». |
| Negative priser i dag-CR | **AVVIKER UBEVISST** — dager med negativ snittspot kastes stille (l. 196, 264). Marginal i dag, voksende problem i NO2. |
| Månedsserie | **AVVIKER UBEVISST** — grupperes på UTC-måned, ikke Europe/Oslo (samme feilklasse som ProduksjonAnalyse, kjent fra KODEGJENNOMGANG). |
| `SpotomsetningNok.HasValue`-krav i CR-teller (l. 129) | **AVVIKER UBEVISST** — et rent timing-mål skal ikke kreve omsetningskolonnen; hull som korrelerer med pris skjevvrir CR. |
| **Dagserie-endepunktet** (`GetDailySeriesAsync` l. 78–104) | **AVVIKER UBEVISST — reell bug.** Bruker gammelt grunnlag (NokDay/MwhDay) mens dag-CR-KPI-en bruker ElhubSpotValueDay. Scatter/histogram i UI stemmer ikke med KPI-kortet — samme feilklasse som Øgreyfoss-caset (~9 pp) som CR-oppryddingen skulle fjerne. Del 2 av oppryddings-specen ble ikke fullført her. |

**Kilder:** [Hirth 2013 — Market Value of Variable Renewables](https://neon.energy/Hirth-2013-Market-Value-Renewables-Solar-Wind-Power-Variability-Price.pdf) · [KYOS Capture Rate Index](https://power.kyos.com/capture-rates) · [Pexapark PPA Glossary](https://pexapark.com/blog/glossary-energy-terms-ppa-explanation/) · [Statkraft Annual Report 2024](https://www.statkraft.com/globalassets/0/.com/6-investor-relations/reports-and-presentations/2024/q4/statkraft-as---annual-report-2024.pdf)

---

## 3. Ubalanse/RK — mot NBM/eSett (énprismodellen)

**Kode:** `UptimeKpiCalculator.cs` (l. 137–221), `NedetidQueryService.GetAvgImbalancePremiumAsync` (l. 211–253), `ProduksjonAnalyseCalculator.cs` (l. 64–73), `VaktRoiCalculator.cs` (l. 281–283, 427–430)

**Regelverkskontekst:** Énprismodell siden nov. 2021 (én ubalansepris per periode, samme uansett retning, én netto posisjon per BRP). 15-min ISP med ekte 15-min ubalansepris i Norge fra 19.3.2025; day-ahead på 15-min MTU fra 1.10.2025. Gamle regulerkraftmarkedet erstattet av mFRR EAM 4.3.2025.

| Beregning | Dom |
|---|---|
| `Ubalansekost_NOK` (KAIA-feltet «Tap/gevinst ubalanse» + eSett-gebyrer) | **SAMSVARER** — eksakt riktig under énpris. Appens beste ubalanse-tall; bør være fasit de andre avstemmes mot. |
| eSett-gebyrer som rene tillegg | **SAMSVARER** (volumgebyr 0,26 EUR/MWh NO, ubalansegebyr 1,15 EUR/MWh). |
| `RkSalgVsSpot`/`RkKjopVsSpot` | **AVVIKER UBEVISST** — (a) fremstilles som «RK-trading»; under énpris/mFRR EAM finnes ikke det gamle RK-markedet, og BRP-en handler ikke — dette er passivt oppgjør. (b) Kjent dobbelttelling: timer med begge retninger (normalt etter 15-min ISP — fire kvarterer kan ha ulik retning) bruker hele AbsUbalansevolum i begge grener. |
| `GetAvgImbalancePremiumAsync`: snitt av kun positive (RK−spot) | **AVVIKER — toprislogikk, feilaktig begrunnet som «konservativ».** Verifisert: `if (diff <= 0) continue;` (l. 245). Under énpris er forventet kostnad E[ubalansepris−spot] over *alle* perioder — ofte nær null eller negativ i NO2. Å snitte bare den positive halen **overestimerer premien, lett faktor 3–10**. SPEC-VAKT-ROI-UBALANSE.md begrunner med nedregulerings-resonnement fra toprismodellen. |
| Vakt-ROI `ReddetUbalanse_NOK` | **AVVIKER UBEVISST** — arver oppblåst premie OG multipliserer med plan-MWh for hele counterfactual-vinduet (opptil ~60 t). Reell eksponering stopper ved neste day-ahead gate closure (12:00 D-1) — uten vakt nullstilles budene for neste døgn. **Samlet kan komponenten være 5–20× for høy.** |
| ProduksjonAnalyse ubalansekost: max(0, RK−spot)×underleveranse | **AVVIKER BEVISST** (flagget i ANBEFALING-TAPSREGNSKAP), men med foreldet premiss: under énpris trengs ingen egen nedregulerings-kolonne — riktig formel er signert (ubalansepris−spot)×(Elhub−forpliktelse) med eksisterende RkPris-kolonne. Klippingen gjør «Netto mot plan» systematisk for pessimistisk. |

**15-min-konsekvens:** NOK-summene fra KAIA er fortsatt eksakte etter timeaggregering; alle *avledede* pris×volum-resonnementer på timesnivå er nå proxyer (RkPris og Spotpris vektes med ulike vekter i HourlyAggregator — kan gi falsk «premie» selv når ubalansepris=spot per kvarter).

**Kilder:** [NBM Single price model](https://nordicbalancingmodel.net/roadmap-and-projects/single-price-model/) · [eSett Handbook](https://www.esett.com/handbook/) · [eSett: 15-min ISP Norge/Sverige](https://www.esett.com/news/15-min-imbalance-settlement-period-in-norway-and-sweden/) · [Statnett: mFRR EAM go-live](https://www.statnett.no/en/for-stakeholders-in-the-power-industry/news-for-the-power-industry/confirmation-of-mfrr-eam-go-live-march-4th-2025/) · [eSett: volumgebyr 2025](https://www.esett.com/news/statnett-to-increase-the-brp-volume-fee-from-1-1-2025/)

---

## 4. Virkningsgrad/effektivitet — mot IEC 60041/62006 og indeksmetodikk

**Kode:** `Modules.Reporting/Effektivitet/EffektivitetQueryService.cs`, `EffektivitetEpisodeService.cs`

**Hovedkarakter: AVVIKER BEVISST på metodevalg (forsvarlig), med flere UBEVISSTE avvik i presentasjon.**

**Det som er riktig tenkt:** Appen gjør ikke akseptansetest-η, men *relativ* tilstandsovervåking — Δη mot anleggets eget 200 kW-bin-snitt fra samme instrument. Det er metodisk beslektet med indeksmetoder (Winter-Kennedy) og riktig ambisjonsnivå for SCADA-data: systematiske Q-feil (typisk 2–5 %) kansellerer langt på vei når punkt og baseline deler kilde. Genuine/Transition-skillet og tapsformelen E×(η_ref−η)/η er korrekte.

**Funn:**
- **η beregnes ikke — den leses rått fra SCADA-taggen** (`SignalRole.TurbineEfficiency`). Verifisert: ingen ρgQH-beregning finnes i src/. **Manualen (index.html:653) oppgir likevel «η = P_el/(ρ×g×Q×H)»** — beskriver en beregning som bare finnes som spec (ANALYSE-VIRKNINGSGRAD.md: egen-η med H_netto, K·Q²-falltap, kryssjekk — ingenting implementert). AVVIKER UBEVISST i dokumentasjonen.
- **Fallhøyde er uobservert:** η(P)-bins blander perioder med ulik H. ±1 m inntaksvariasjon på 50 m fall ≈ ±2 % tilgjengelig energi — **på størrelse med hele −2 pp-terskelen**. En «underytende episode» kan være lavt magasin, ikke turbintilstand.
- **Sirkulær baseline:** beregnes fra samme periode som analyseres — gradvis degradering senker baselinen og blir usynlig. Ingen frossen referanseperiode.
- **Sweet spot på 3 samples (45 min data)** er for svakt for driftsanbefaling; tap-mot-sweet-spot i NOK ignorerer at sweet-spot-drift ofte er umulig (vanntilgang, minstevannføring).
- **NOK-tap per episode uten usikkerhetsbånd** = falsk presisjon (±50–100 % nær terskelen). Tapstopplisten er robust nok for *prioritering*; beløpene er det ikke.
- **Tekniske bugs:** hourly-fallback gir 4× for lavt episode-MWh (hardkodet 0,25 t/intervall, ingen oppløsningsguard); effektbånd-aggregat hardkoder −2,0 mens episodene bruker justerbar terskel.
- Norconsult-leverandørkurven for Lindland (Virkningsgrader/) brukes ikke som referanse av koden.

**Kilder:** [IGHEM: IEC 60041 kap. 14](https://ighem.org/Paper2010/TSD04.pdf) · [IEC 62006:2010](https://webstore.ansi.org/standards/iec/iec62006ed2010) · [TU Graz: termodynamisk metode](https://www.hfm.tugraz.at/en/research-engineering/on-site-measurement-acc-iec-60041-iec-62006/thermodynamic-efficiency-measurement.html) · [Winter-Kennedy: Problems and Challenges](https://www.researchgate.net/publication/307173383_Winter-Kennedy_method_in_hydraulic_discharge_measurement_Problems_and_Challenges) · [MDPI Energies 13:1310](https://www.mdpi.com/1996-1073/13/6/1310)

---

## Samlet prioritering

| # | Tiltak | Konsekvens i dag |
|---|--------|------------------|
| 1 | **Ubalansepremie: erstatt max(0, RK−spot) med signert forventningsverdi** + begrens Vakt-ROI-ubalanse til neste gate closure | Vakt-ROI-komponent 5–20× for høy — påvirker vakt-beslutninger |
| 2 | **AF: innfør IEEE-definisjonen (AH/PH) eller døp om til «Leveringsgrad i forpliktede timer»** + fjern redundansen mot FOR | Usammenlignbar med bransjetall; villedende kommentar |
| 3 | **Fiks null→0-bugen i portefølje-AF-aggregeringen** | Én datafattig måned rasere portefølje-AF |
| 4 | **Fiks `GetDailySeriesAsync` til ElhubSpotValueDay-grunnlag** | UI-grafer motsier KPI-kort (~9 pp i kjent case) |
| 5 | **EAF: fjern RU fra teller, ekskluder IU fra begge ledd** (eller dokumenter som egen variant) | Tørrår gir kunstig lav EAF; datahull blåser den opp |
| 6 | **Effektivitet: oppløsningsguard (hourly→ingen episoder), frys baseline-referanseperiode, hev sweet spot-minimum, merk NOK som estimat** | Falsk presisjon i tapstall |
| 7 | **Rett dokumentasjonen:** manualens η-formel og AF-definisjon, README-ens IEEE-påstand, Begreper.cs | Tre motstridende AF-definisjoner i omløp |
| 8 | Signert ubalansekost i ProduksjonAnalyse (énpris-formel) | «Netto mot plan» for pessimistisk |
| 9 | Dag-CR: dokumenter negativ-pris-policy; sperr P5/P95-tall fra ekstern rapportering | Voksende relevans |
| 10 | Vindpark: egen IEC 61400-26-beregningsvei | Vind-tilgjengelighet er meningsløs i dag |
| 11 | Vurder kvartersoppløsning for ubalansefelter gjennom pipelinen | Alle pris×volum-proxyer på timesnivå |

**Det som står seg godt:** times-CR (lærebok), Timing-merverdi, Realisert vs spot-skillet, Ubalansekost_NOK via KAIA-feltet, eSett-gebyrhåndtering, FOR-formelen, Genuine/Transition-skillet og tapsformelen i effektivitet, og det relative Δη-metodevalget i seg selv.

**Gjenstående områder (ikke dekket):** Vakt-ROI-modellens øvrige forutsetninger (counterfactual-vindu, reddbarhet), overløpsestimatet, start/stopp-kostnadenes parametre mot EPRI-litteratur (delvis dekket i FAGVURDERING-START-STOPP-VANNVERDI-P50P90.md). Si fra hvis du vil ha disse også.
