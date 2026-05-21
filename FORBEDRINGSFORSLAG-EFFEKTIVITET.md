# Forbedringsforslag — Effektivitet-siden

**Dato:** 2026-05-21
**Bakgrunn:** Live gjennomgang av `/effektivitet` + feilbanneret. Skrevet for å gis videre til Claude Code.
**Brukermål (Morten):** Se om kraftverkene kjøres effektivt, sammenligne anlegg, og analysere kjøring mot Hydrogrid (produksjonsplan, vannstand) og SCADA. Fokus skal ligge på **gjentagende** og **langvarige** underytelses-intervaller — det er der det er mest å hente.

---

## Del 1 — Feilbanneret «En uventet feil har oppstått»

**Hva det er:** Banneret nederst er Blazors innebygde `#blazor-error-ui`. Det er ikke en feil i seg selv — det er sikkerhetsnettet som slår inn når en komponent kaster en uhåndtert exception.

**Hvorfor det føles som det er der hele tiden:** Verifisert i kjørende app. Når banneret først er trigget, blir det stående ved *menynavigasjon* (SPA — ingen sidelasting). Testet: Effektivitet utløser banneret → klikk «Nedetid» i menyen → banneret står fortsatt, selv om Nedetid er feilfri. Det forsvinner kun ved full nettleseroppdatering (F5).

**Hva som trigger det i dag:** To uavhengige feil:
1. **Effektivitet** — `NullReferenceException` i ApexCharts (se Del 2).
2. **Rapport-detalj** — `nb-NO`-kultur-krasj (funn #1 i `TESTRAPPORT-2026-05-21.md`).

**Anbefaling:** Fjern de to underliggende feilene, så trigges aldri banneret. I tillegg bør banneret tømmes ved ruteendring (skjul `#blazor-error-ui` i en `LocationChanged`-handler) slik at en enkeltfeil ikke «smitter» til alle sider — men det er sekundært. Rotfiksen er å fjerne exception-ene.

---

## Del 2 — Feil funnet på Effektivitet-siden

| # | Feil | Effekt |
|---|------|--------|
| A | `NullReferenceException` i `ApexCharts.ApexPointSeries<EffektivitetBin>.OnInitialized()` (og `.Dispose()`) | Trigger feilbanneret hver gang siden lastes |
| B | η(P)-kurvens x-akse rendrer **5966 etiketter** (én per datapunkt) | Uleselig grå stripe nederst i grafen; x-aksen er ubrukelig |

Begge er ApexCharts-konfigurasjonsfeil. **B** skyldes at x-aksen behandles som en kategori-akse — den bør være `type: numeric` med ~8–12 hakk fra 0 til maks effekt. **A** er trolig at en serie bindes til en `null`-samling før data er hentet; pakk serie-komponenten i `@if (data is not null)` eller initialiser til tom liste.

> **⚠️ Verifiseringsnotat til Claude Code (2026-05-21 11:52):**
> Feil A og B er **re-testet live etter at appen ble stoppet og startet på nytt** — begge er **fortsatt til stede**. Konsollen kaster fremdeles `NullReferenceException` i `ApexCharts.ApexPointSeries<EffektivitetBin>.OnInitialized()` / `.Dispose()`, og feilbanneret vises.
>
> En ren stopp/start av appen retter ikke dette — det er en kodefeil. Claude Code må:
> 1. Bekrefte at fiksen faktisk ligger i kildekoden (`Effektivitet.razor` eller der `ApexPointSeries<EffektivitetBin>` brukes) — ikke bare anta at den er gjort.
> 2. Bygge på nytt med `docker compose build` (ikke bare `docker compose up`), siden Web kjøres som Blazor WASM i container.
> 3. Verifisere i nettleser-konsollen at exception-en er borte og at `#blazor-error-ui` ikke vises.

---

## Del 3 — Det viktigste: fra η(P)-kurve til tapsanalyse

Din egen vurdering: η(P)-kurven gir lite verdi. Det stemmer — **som visuelt blikkfang**. Men som *beregningsgrunnlag* er den helt sentral. Skillet er nøkkelen til redesignet:

- η(P)-kurven/effekt-bins = **baseline-modellen** (forventet virkningsgrad ved en gitt effekt). Behold beregningen.
- Det siden mangler er **handlings-visningen**: hvor taper vi virkningsgrad, hvor lenge, og hvor ofte.

### 3.1 Riktig referanse: per effekt-bånd, ikke globalt snitt

Virkningsgrad avhenger sterkt av effekt — et intervall på 600 kW har naturlig lavere η enn 1900 kW. Måler man «2 % under snittet» mot det *globale* snittet (88,8 %), drukner man i lav-effekt-intervaller som er «lave» av ren fysikk.

**Riktig mål på underytelse:** `Δη = faktisk η − forventet η for effekt-bin'en`. Da fanger man et 600 kW-intervall som kjører 76 % når 600–800 kW-bin'en snittet 79,1 % — et reelt, fiksbart tap — uten falske treff. Effekt-bins-tabellen beregner allerede bin-snittene; de er den ferdige baselinen.

### 3.2 Ny hovedvisning: «Underytelse / tapstoppliste»

Erstatt η(P)-scatteret som førsteinntrykk med en handlingsorientert visning:

**Steg 1 — Flagg avvik.** For hvert Genuine 15-min-intervall: `Δη = η − baseline(effekt)`. Marker intervallet som underytende når `Δη ≤ −2,0 pp` (terskelen bør være justerbar i UI).

**Steg 2 — Grupper til episoder.** Slå sammen sammenhengende underytende intervaller til *episoder*. Tillat gjerne 1 normalt intervall mellom (justerbart) så ikke alt splittes opp. Hver episode får:

| Felt | Beskrivelse |
|------|-------------|
| Start / slutt / varighet | Når og hvor lenge |
| Antall intervaller | 15-min-blokker |
| Snitt Δη | Hvor langt under forventet (pp) |
| Effektområde | min–maks kW |
| Tapt energi | ≈ Σ produksjon × (η_forventet − η_faktisk) / η_faktisk |
| Tapt verdi | tapt energi × spotpris i intervallet (NOK) |

**Steg 3 — Ranger etter tapt verdi.** Øverst = mest å hente. Det er svaret på «hvor jobber jeg først».

### 3.3 Gjentagende mønstre

Dette er kjernen i det du etterspør. Etter at episodene er funnet, grupper dem for å finne gjentagelse:

- **Per effekt-bånd:** «Underytelse i 600–800 kW: 14 episoder, totalt 38 timer, ~X NOK tapt.» Et anlegg som gjentatte ganger kjører dårlig i ett bånd peker ofte på et fast driftspunkt-problem (f.eks. ugunstig ledeapparat-åpning, eller at man bør kjøre på/av i stedet for å ligge i et dårlig bånd).
- **Per tidspunkt:** histogram over time-på-døgnet / ukedag. Avdekker om underytelsen følger et mønster (nattkjøring, lavlast-perioder).
- **Sortér gjentagende grupper etter total varighet og total tapt verdi** — lange og hyppige øverst, slik du selv vil ha det.

Forslag til oversiktstabell:

| Effekt-bånd | Antall episoder | Total varighet | Snitt Δη | Tapt energi | Tapt verdi | Typisk tidspunkt |
|-------------|-----------------|----------------|----------|-------------|------------|------------------|
| 600–800 kW | 14 | 38 t | −4,1 pp | … MWh | … NOK | natt 00–05 |

### 3.4 η(P)-kurven beholdes — som diagnose, ikke blikkfang

Behold grafen, men mindre og sekundær: fikset numerisk x-akse, og fremhev de underytende punktene i rødt så man ser *hvor på kurven* tapet ligger. Da blir den et støtteverktøy for episodene over, ikke en vegg av prikker.

---

## Del 4 — Sammenligne anlegg + kobling mot Hydrogrid og SCADA

### 4.1 Anleggssammenligning

I dag er siden strengt ett-anlegg-om-gangen (nedtrekksliste). Det mangler et **portefølje-blikk på effektivitet**: alle anlegg side om side med Snitt η, sweet-spot, spesifikt vannforbruk og **total tapt verdi**. Da ser du hvilket anlegg som har mest å hente, før du borer ned i ett. Dette kan være en egen «oversikt»-fane øverst på Effektivitet-siden, eller en kolonne i Portefølje-tabellen som lenker hit.

### 4.2 Drill-down for årsaksanalyse (Hydrogrid + SCADA)

Når du klikker en episode, bør detaljvisningen samle alt du trenger for å forstå *hvorfor*, for nøyaktig det tidsvinduet:

- **Hydrogrid:** faktisk produksjon vs. produksjonsplan (logikken finnes allerede på Produksjon-siden — gjenbruk den).
- **Vannstand / magasin:** nivå i vinduet — lav fallhøyde forklarer ofte lav η.
- **SCADA:** lenke/innebygd visning av de relevante signalene for vinduet — turbin-virkningsgrad, vannføring, generator-effekt, og ledeapparat-åpning hvis tilgjengelig.

Poenget: et effektivitetstap har en årsak — feil driftspunkt mot plan, lav vannstand, eller en utstyrsfeil. Drill-down skal gjøre årsaken synlig uten at du må hoppe mellom fire sider.

---

## Del 5 — Mindre UX-fikser

- **Dev-referanse i brukerteksten:** undertittelen viser «(Spec NESTE-CHAT-EFFEKTIVITET-15MIN)». Intern dokumentreferanse — fjern fra sluttbruker-tekst.
- **Engelsk sjargong:** «Genuine» og «Transition» midt i en norsk app. Bytt til f.eks. «Normal drift» og «Start/stopp».
- **KPI-kortene** blander effektivitet (Snitt η, sweet-spot, spesifikt vannforbruk) og økonomi (Capture rate, Merverdi vs spot). Grupper dem visuelt, eller skill med en liten seksjonstittel.
- **Filter-interaksjonen virker bra:** klikk på en effekt-bin-rad filtrerer intervall-tabellen og viser en «Filter»-chip. Behold mønsteret — bruk det også på den nye tapstopplisten.

---

## Del 6 — Foreslått byggerekkefølge for Claude Code

1. **Fiks feil A** (ApexCharts `NullReferenceException`) — fjerner feilbanneret. Liten, isolert. **Bekreftet fortsatt aktiv 2026-05-21 11:52 etter omstart — se verifiseringsnotatet i Del 2. Sjekk at fiksen faktisk ligger i koden og at appen er bygget på nytt.**
2. **Fiks feil B** (x-aksen → numerisk) — gjør η(P)-grafen lesbar. Liten. **Bekreftet fortsatt aktiv 2026-05-21 11:52.**
3. **Bygg avviks- og episode-beregningen** (Del 3.1–3.2) som en tjeneste — ren logikk, kan enhetstestes.
4. **Ny hovedvisning «Underytelse / tapstoppliste»** (Del 3.2–3.3) — bruker tjenesten fra punkt 3.
5. **Drill-down med Hydrogrid + SCADA** (Del 4.2).
6. **Anleggssammenligning** (Del 4.1).
7. **UX-fiksene** (Del 5) — kan tas når som helst, trivielle.

Punkt 1–2 og 7 er små. Punkt 3 er den viktigste investeringen — når episode-beregningen finnes, er resten i hovedsak å presentere den.
