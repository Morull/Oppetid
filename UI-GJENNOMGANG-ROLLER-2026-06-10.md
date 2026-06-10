# UI-gjennomgang mot brukerroller — Uptime
**Dato:** 2026-06-10
**Spørsmål:** Er appen intuitiv for de tre rollene? Trengs store endringer?
**Avgrensning:** Den visuelle gjennomgangen fra 20. mai (ANBEFALINGER-UI-GJENNOMGANG.md) er i all hovedsak implementert — felles KpiCard, full bredde på tabellsider, 7-kolonners Portefølje med toggle, HjelpeIkon/Begreper-ordliste. Denne gjennomgangen handler om det som gjenstår: **informasjonsarkitektur** — ligger svarene der rollene leter?

---

## Hovedkonklusjon

**Nei, store endringer trengs ikke. Sidene er innholdsmessig sterke — problemet er at svarene ligger på feil sted, i feil rekkefølge, med feil navn.** To strukturelle grep (ny landingsside + menygruppering) og en håndfull små fikser løser det meste. Ingenting skal rives.

---

## Vurdering per rolle

### Driftsleder — «Står noe? Var vakta verdt det?»
**Karakter i dag: svakest dekket av de tre.**

- Åpner appen → auto-sendes til **forrige måneds avregningsrapport for ett anlegg** (Reports.razor:154–160). Svarer ikke på noen av spørsmålene hans.
- «Hvilke anlegg har stått?» finnes ikke som visning: Nedetid-siden er kun per anlegg → 11 anleggsbytter i toppbaren for å sjekke porteføljen. Ingen tverranleggs hendelsesliste eksisterer.
- Minste periodevalg er **måned** — morgensjekk («hva skjedde i natt/siste uke») er praktisk umulig uten egendefinert datovalg hver gang.
- Kolonnene han trenger i Portefølje-tabellen (Nedetid, Tap, Reddet vakt) er **skjult bak «Vis alle»-toggle** som er av by default og ikke huskes.
- **Vakt-ROI-siden er derimot utmerket** for ham: portefølje-aggregat, vaktvindu-simulering, døgnvakt-baseline, lønnsomhetsdom. 1 klikk. Bevar.

### Produksjonsleder — «Maks penger per liter vann»
**Karakter i dag: god per anlegg, fragmentert på tvers.**

- Capture rate-siden er god (1 klikk, trend mot forrige periode), men kun per anlegg.
- **Overløp — tapt vann — er det dårligst plasserte hovedtallet i appen:** tre KPI-kort på rad 3 inne på «Produksjon»-siden, kun i %, timer og MWh — **aldri i NOK**, og ikke i Portefølje-tabellen. For rollen som jakter tapt vann er dette begravd på feil side uten kroneverdi.
- Start/stopp-kostnader krever 3 klikk via Rapporter → måned → scroll (StartStoppKort kun på ReportDetail).
- **Merverdi-begrepskaos:** «Timing-merverdi», «Hydrogrid plan-merverdi», «Realisert timing-verdi», «Timing-gap», «Netto mot plan», «Merverdi» — 5–7 navn i omløp. Hver har god tooltip, men brukeren må selv bygge mentalt kart over hva som er samme tall.

### Daglig leder — «Ett bilde, uten graving»
**Karakter i dag: bildet finnes, men han lander ikke på det.**

- Portefølje-sidens KPI-strip + Økonomi-fanen (med PDF) **er** DL-bildet. Men han lander på en avregningsliste, må vite at «Portefølje» er svaret, og møter «AF», «FOR», «CR», «Vakt-ROI» i meny og tabellhoder. Tooltips finnes, men menyord forklares ikke — og en DL hovrer ikke.
- Reise i dag: 2–3 klikk + oversettelsesarbeid. Burde vært 0 klikk.

---

## Tverrgående funn

1. **Feil landingsside.** `/` auto-redirecter til forrige måneds enkeltanleggsrapport — riktig for avstemming, galt for alle tre rollene.
2. **Flat 10-punkts meny uten gruppering.** Analyse, dokumenter og admin om hverandre. `MudNavGroup` finnes i MudBlazor, ikke i bruk.
3. **Misvisende navn:** «Rapporter» = månedsavregninger; «Produksjon» = plan-etterlevelse + overløp; «Anlegg» = innstillinger, ikke status.
4. **Ingen kort-periode-valg:** Måned/Kvartal/År/HiÅ — mangler «Siste 7 dager»/«I går».
5. **Verifiserte småfeil:** «Tap i NOK» har grønn/positiv aksent på Nedetid (Nedetid.razor:81, tap ser ut som gevinst); «Total reddet» vises dobbelt i Vakt-ROI portefølje-modus (VaktRoi.razor:147 + 179); `/behandles` er foreldreløs (ikke i meny).

## Det som fungerer godt — bevar

Vakt-ROI-simuleringen, Begreper.cs/HjelpeIkon-systemet (forbilledlig sentral ordliste), KpiCard med trend og InfoText, datakvalitets- og tolknings-bannerne, Økonomi-fanen med PDF, konsolidert Data-import, Forrige/Neste på ReportDetail.

---

## Anbefaling

**To store grep:**

1. **Ny landingsside «Oversikt»** — morgensjekk + DL-bilde på `/`. Komponeres av eksisterende byggeklosser: Portefølje-KPI-stripen, import-banner (siste 24 t), ny tverranleggs liste «Siste nedetidshendelser» og per-anlegg statusrad. Krever ett nytt aggregat-endepunkt; resten er gjenbruk.
2. **Menygruppering etter rolle** (rolle-orientert, ikke rolle-låst):
   - *Oversikt:* Oversikt (ny), Portefølje
   - *Drift:* Nedetid, Vakt-ROI
   - *Marked & vann:* Produksjon & plan, Capture rate, Effektivitet
   - *Data & admin:* Månedsrapporter, Anlegg, Data-import, Kategorier

**Små grep (rangert):** hurtigvalg «Siste 7 dager»/«I går» i periodevelgeren; overløp i NOK + kolonne i Portefølje; persistér kolonnevalg i Portefølje (eller gjør Nedetid/Tap/Reddet til default); omdøp menypunkter; StartStoppKort også på Produksjon-siden; fiks aksent- og duplikat-feilene; badge for Behandles på Data-import; kanonisk merverdi-navnesett.

Full implementasjonsdetalj: se **SPEC-UI-ROLLEBASERT-2026-06-10.md** (skrevet for Claude Code).
