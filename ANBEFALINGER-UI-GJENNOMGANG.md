# UI/UX-gjennomgang — KraftverkUptime «Uptime»

Gjennomgang av det visuelle grensesnittet, basert på kildekoden i
`src/KraftverkUptime.Web` (16 sider, 10 komponenter, layout og tema).
Først Portefølje-siden i dybden, så de app-overgripende mønstrene — som er
der den største gevinsten ligger — og til slutt en prioritert tiltaksliste.

Metoden er kodebasert. Ett skjermbilde (Portefølje, mørk modus) er brukt
til å kalibrere hvordan koden ser ut i praksis. En live gjennomgang i
nettleser ville lagt til detaljer om faktisk pikselresultat, men mønstrene
under er entydige i koden.

---

## Hovedfunn i én setning

Hver enkelt side fungerer, men appen mangler et **felles designsystem** —
KPI-kort, farger, typografi og sideoverskrifter er løst på 3–4 forskjellige
måter parallelt. Det er derfor helheten føles uferdig, og det er derfor en
kodeagent ikke «fikser» det av seg selv: problemet er ikke i én fil, det er
fraværet av felles byggeklosser på tvers av filene.

---

## Del 1 — Portefølje-siden

Siden er funksjonelt solid: parallell datahenting, isolert feilhåndtering
per anlegg, sorterbare kolonner, tooltip-forklaringer, «mangler data»-varsler,
sticky første kolonne og en SUM-rad. Det skal beholdes. De visuelle problemene:

**13-kolonners tabell, ~4 synlige.** Dette er den klart største svakheten.
Anlegg, Effekt, AF, FOR, Produksjon, Spotoms, CR, Merverdi, Nedetid, Tap,
Reddet vakt, Reddbare, Datakvalitet — mesteparten ligger bak vannrett
scrolling. Anbefaling: kutt til 6–7 kjernekolonner i standardvisningen
(f.eks. Anlegg, Effekt, AF, FOR, Produksjon, Spotoms, Datakvalitet) og flytt
resten til enten en ekspanderbar detaljrad eller anleggets egen detaljside —
raden er allerede klikkbar dit. Eventuelt en kolonnevelger for de som vil ha
alt. Vannrett scrolling i en datatabell bør være unntaket, ikke normalen.

**Innholdsbredden er låst, men tabellen trenger mer plass.** Hele appen
ligger i `MudContainer MaxWidth="Large"` (~1280 px). På en bred driftsskjerm
gir det store tomme marger på sidene — *samtidig* som tabellen må scrolles.
Anbefaling: la denne siden (og andre tabelltunge sider) bruke full bredde.

**Stort tomt felt under tabellen.** Innholdet klumper seg øverst. Plassen
kan brukes til en liten oversiktsgraf — f.eks. produksjon eller «reddet NOK»
per anlegg som stolper — som gir overblikk uten å scrolle en tabell.

**Anleggsvelgeren i toppstripen er irrelevant her.** Portefølje-rapporten
gjelder alle anlegg; anleggs-nedtrekkslisten gjør ingenting. Skjul den på
denne siden (og på Vakt-ROI i porteføljemodus).

**Enkeltdags-periode.** Skjermbildet viser «19.4–19.4.2026» — én dag. En
porteføljerapport for én dag er en rar tilstand. Vurder å hindre det, eller
default til måned/hittil-i-år.

**«Reddet av vakt: – NOK».** Bindestreken betyr manglende data, men ser ut
som «null kroner». Vis manglende data eksplisitt (f.eks. en liten «ingen
data»-merkelapp) så det ikke forveksles med en reell nullverdi.

**KPI-stripen** (fire kort med farget venstrekant) er grei i prinsippet, men
bør bygges av den felles KPI-komponenten — se Del 2.

---

## Del 2 — App-overgripende mønstre

Dette er kjernen. Funnene under gjelder hele appen og forklarer hvorfor
helheten føles inkonsistent.

### 2.1 Fire konkurrerende KPI-kort-mønstre

KPI-kortet — etikett, stort tall, undertekst, farget kant — finnes i fire
varianter samtidig:

1. **Håndlagde inline-kort:** `MudPaper` + `border-left: 4px solid #hex`,
   gjentatt ~48 ganger: Vakt-ROI 16, Anlegg 12, Effektivitet 10, Nedetid 4,
   Portefølje 4, Capture rate 2.
2. **Delt komponent `KpiCard`** (med info-tooltip).
3. **Delt komponent `KpiTrendCard`** (med trend-pil mot forrige periode).
4. **En ubrukt `.dk-stat`-CSS-klasse** i `app.css` med gradient-topplinje og
   gradient-ikon — tilsynelatende et tidligere designforsøk som aldri ble
   tatt i bruk.

Verre: noen sider **blander**. Anlegg og Capture rate bruker både den delte
komponenten og håndlagde kort i samme visning. Resultatet er at KPI-verdien
er `h3` ett sted og `h4` et annet, kantfargene varierer, og info-tooltip
finnes på noen kort men ikke andre.

**Anbefaling:** konsolidér til **én** komponent — `KpiCard` med valgfri
trend og valgfri info-tooltip. Slett `.dk-stat`-CSS-en og erstatt alle ~48
inline-kortene. Dette er den enkeltendringen som gir mest synlig løft.

### 2.2 Parallell fargepalett utenfor temaet

Temaet (`DalaneTheme.cs`) definerer en gjennomtenkt merkevarepalett — blå
`#1F84B7`, teal `#1AA09F`, grønn `#2A8B5C` — med egne lys/mørk-varianter.
Den brukes nesten ikke. I stedet er det **17+ hardkodede hex-verdier** strødd
ut over sidene, ingen av dem tematokens:

- Tre forskjellige «røde»: `#E76F51`, `#E74C3C`, `#D32F2F`.
- Tre forskjellige «grønne»: `#2A9D8F`, `#2ECC71`, `#00A99D`.
- KPI-blå `#1E5F8E` er ikke merkevareblå `#1F84B7`.

**Anbefaling:** definér et lite sett **semantiske fargetokens** ett sted —
positiv, negativ, nøytral, advarsel, pluss kategori-fargene for nedetid — og
bruk kun dem. Da blir fargebruken konsistent, og dark mode løser seg selv
(se 2.3).

### 2.3 Skjør dark mode

`app.css` lapper dark mode med `!important` og — mest bekymringsfullt —
attributt-string-matching: regler som `span[style*="color: #2A9D8F"]`. Det
betyr at fargekodet tekst kun blir lesbar i mørk modus hvis hex-koden er
skrevet *nøyaktig* slik regelen forventer. Skriver noen `#2a9d8f` med små
bokstaver, eller en nabofarge, blir teksten usynlig på mørk bakgrunn. Dette
er et symptom på rotproblemet i 2.2 — og forsvinner når farger flyttes til
tokens som har innebygde lys/mørk-varianter.

### 2.4 Typografi-hierarkiet brukes inkonsistent

`h1` brukes konsekvent til sidetittel — bra. Resten er rotete:

- Seksjonstitler er `h3` noen steder, `h4` andre steder.
- KPI-verdier er `h3` på Nedetid, Vakt-ROI, Effektivitet og deler av Anlegg
  — men `h4` på Portefølje og toppen av Anlegg.
- `h4` brukes samtidig til seksjonstittel, KPI-verdi *og* expansion-panel-
  tittel.
- `h2` brukes kun ett eneste sted i hele appen (Kategorier).

**Anbefaling:** fast skala — `h1` side, `h2` seksjon, og KPI-verdien får sin
egen klasse (den er et datapunkt, ikke en overskrift). Når KPI-kortet
konsolideres (2.1) løses verdi-typografien automatisk.

### 2.5 Ingen felles side-header

Alle 14 hovedsider har en `h1`, men hver bygger headeren på sin egen måte.
Rapporter-siden bruker en egen `dk-hero`-klasse (2,5 rem, vekt 700);
de andre bruker temaets `Typo.h1` (2,2 rem, vekt 600). Noen legger
undertittel i et `MudGrid`, noen i en vanlig `div`, Anlegg-lista har tittel
+ knapp i en flex-rad. Rapporter-siden ser dermed visuelt annerledes ut enn
resten av appen.

**Anbefaling:** én `PageHeader`-komponent (tittel, valgfri undertittel,
valgfritt høyre-stilt handlingsfelt). Erstatt alle håndlagde headere.

### 2.6 Plassutnyttelse

Hele appen er låst til `MaxWidth="Large"`. For skjema- og detaljsider er det
fint. For de tabelltunge rapportsidene (Portefølje, Produksjon) gir det den
uheldige kombinasjonen brede tomme marger + tabell som likevel må scrolles.

**Anbefaling:** la tabelltunge rapportsider bruke full bredde eller `XL`;
behold `Large` for skjema-/detaljsider.

### 2.7 Tre mønstre for progressiv visning

Anlegg og Data-import bruker `MudTabs`; ReportDetail, PlantAdmin og Capture
rate bruker `MudExpansionPanels`; de fleste andre stabler alt vertikalt. Det
er greit at mønsteret varierer etter behov, men det bør være et bevisst valg
— f.eks. faner for likestilte seksjoner, ekspandering for «avansert/sjelden».

### 2.8 Navigasjon og informasjonsarkitektur

- **Effektivitet er en foreldreløs side.** `/effektivitet` er en fullverdig
  analyse-side, men mangler i venstremenyen. Den må enten inn i menyen eller
  fjernes.
- **Overlappende IA.** Capture rate, Effektivitet, Produksjon, Nedetid og
  Vakt-ROI finnes både som egne toppnivå-sider *og* som faner på anleggets
  detaljside. Samme analyse to steder. Verdt å bestemme: er disse primært
  per-anlegg (da hører de hjemme på anleggssiden) eller portefølje-
  oversikter (da hører de hjemme i menyen)? I dag er det begge deler.
- De øvrige detaljsidene (Anlegg-detalj, PlantAdmin, ReportDetail, Behandles,
  Data-status, Upload) nås via lenker — det er greit at de ikke er i menyen.

### 2.9 Mindre, men konkrete feil

- **`.dk-subbar { top: 56px }`** mens temaets `AppbarHeight` er `64px`. Den
  klistrede periodevelgeren er feiljustert med 8 px mot toppbaren.
- **`NorskTall`-tallformateringen er kopilimt** inn i minst fire filer
  (Portefølje, Nedetid, Capture rate, Effektivitet, `KpiTrendCard`). Bør
  være én delt hjelpeklasse.
- Bakgrunnens faste radial-gradient er en smakssak, men gir lite og kan
  forenkles til en flat flate for et roligere uttrykk.

### 2.10 Den globale periodevelgeren er ubrukelig på rapport-visningen

*Verifisert i kjørende app 20.05.2026.*

Periodevelgeren i toppstripen (`AppBarPlantPeriodSelector`) har granularitet
(Måned/Kvartal/År/Hittil i år/Egendefinert) og ←/→-stegning. Den fungerer
fint på analysesidene der en «periode» er et intervall. På rapport-visningen
gjør den derimot mer skade enn nytte. Observert oppførsel:

1. **Du lander rett på en rapport.** Åpner du `/` (Rapporter), redirecter
   koden automatisk til forrige måneds rapport-detalj (`FindLastMonthImport`).
   Liste-visningen ser brukeren i praksis aldri.

2. **←/→-pilene er deaktivert på rapporten.** En rapport-detalj kjører
   velgeren i *Egendefinert*-modus, og i den modusen er ←/→ grået ut
   (`CanStep` er kun sann for Måned/Kvartal/År). Den eneste måten å bla
   mellom rapporter på er rapportens *egne* «Forrige»/«Neste»-knapper — og de
   steger én måned av gangen. Det finnes ingen «hopp til måned».

3. **Egendefinert-modusen henger igjen.** Etter at man har vært innom en
   rapport er ←/→ fortsatt deaktivert når man går til Nedetid, Vakt-ROI osv.
   — granulariteten sitter fast på Egendefinert. Man må manuelt klikke Måned
   for å få pilene tilbake, og *det* hopper perioden til inneværende måned —
   så perioden man så på, går tapt.

4. **Velger og innhold desynkroniseres.** Klikker man Kvartal mens man står
   på en rapport, endrer velgeren seg til f.eks. «Q1 2026» mens rapporten
   fortsatt viser april. Toppstripen viser da en annen periode enn innholdet.

Kort sagt: på rapport-visningen er den globale velgeren enten død (pilene)
eller misvisende (granularitetsknappene), og brukeren tvinges over på
rapportens egne steg-knapper — én måned av gangen. Det er nøyaktig
«tungvint»-opplevelsen.

Anbefaling: gi Rapporter/rapport-detalj sin egen, side-tilpassede velger —
anleggs-dropdown + en «rapportmåned»-dropdown som lister alle tilgjengelige
måneds-rapporter, der valg navigerer rett til rapporten. Ingen granularitet,
ingen stegning, og ett klikk til hvilken som helst måned. `MainLayout` har
allerede `SuppressedPaths` for å skjule den globale velgeren på sider der den
ikke hører hjemme — rapport-sidene hører dit. Sekundært bør Egendefinert-
modus ikke kunne «smitte» og deaktivere stegningen på andre sider. Dette er
samme rot som 2.8: én velger brukt likt på sider med ulike behov.

---

## Del 3 — Korte notater per side

- **Rapporter (`/`)** — eneste side med `dk-hero`; visuelt avvikende. Søk +
  tabell er ryddig. Bør bruke felles PageHeader.
- **Portefølje** — se Del 1.
- **Nedetid** — fire håndlagde KPI-kort, ApexCharts donut + trend, virtuell
  events-tabell. Solid side; trenger felles KPI-kort og tokens.
- **Vakt-ROI** — flest håndlagde KPI-kort (16) og `h3`/`h4` om hverandre på
  verdiene. Størst gevinst av KPI-konsolidering.
- **Capture rate** — bruker `KpiTrendCard` (bra!) men *også* to håndlagde
  kort. «Avansert»-seksjon bak ekspandering er et godt mønster.
- **Produksjon** — bred måneds-tabell (~12 kolonner), samme scroll-problem
  som Portefølje.
- **Effektivitet** — ti håndlagde KPI-kort; ikke i menyen (2.8).
- **Anlegg-liste (`/plants`)** — kortrutenett, ryddig. Et godt mønster.
- **Anlegg-detalj** — fanebasert; blander komponent- og håndlagde KPI-kort.
- **Data-import** — 1919 linjer, klart appens mest komplekse side; egen
  gjennomgang anbefales hvis den skal forbedres.
- **Kategorier / PlantAdmin / ReportDetail / Behandles** — admin/detalj;
  lavere prioritet, men arver de samme KPI-/farge-/typografi-mønstrene.

---

## Del 4 — Prioritert tiltaksliste

Rangert etter effekt mot innsats. De fire øverste gir mesteparten av det
synlige løftet.

| # | Tiltak | Effekt | Innsats |
|---|--------|--------|---------|
| 1 | Konsolidér til **én `KpiCard`-komponent**; fjern ~48 inline-kort og den ubrukte `.dk-stat`-CSS-en (se Vedlegg A) | Høy | Middels |
| 2 | Definér **semantiske fargetokens**, erstatt alle ~17 hardkodede hex-verdier | Høy | Middels |
| 3 | **Portefølje + Produksjon:** full bredde + kutt til 6–7 kjernekolonner | Høy | Lav–middels |
| 4 | **Side-tilpasset velger på Rapporter** — erstatt den globale periodevelgeren med plant + rapportmåned-dropdown | Middels–høy | Lav–middels |
| 5 | Felles **`PageHeader`-komponent** på alle sider | Middels | Lav |
| 6 | Fast **typografiskala** (h1 side, h2 seksjon, egen KPI-verdi-klasse) | Middels | Lav |
| 7 | Rydd **dark mode** — fjern `!important` og `[style*=...]`-hacks (faller stort sett ut av tiltak 2) | Middels | Lav |
| 8 | **Effektivitet inn i menyen**, avklar per-anlegg vs. portefølje-IA | Lav–middels | Lav |
| 9 | Fiks `.dk-subbar`-offset (56 → 64 px), samle `NorskTall` i én hjelpeklasse | Lav | Triviell |

### Slik bruker du dette mot Claude Code

Rekkefølgen er valgt med vilje: tiltak 1, 2, 4 og 5 lager de felles
byggeklossene; deretter blir resten mekanisk «bytt ut X med komponenten».
Gi Claude Code ett tiltak om gangen som en konkret instruks, f.eks.:

> «Lag en `KpiCard`-komponent som dekker både statisk verdi og trend mot
> forrige periode. Erstatt deretter alle inline `MudPaper`-KPI-kort i
> Vakt-ROI, Anlegg, Effektivitet, Nedetid, Portefølje og Capture rate med
> den. Slett `.dk-stat`-blokken i app.css.»

Da har agenten en avgrenset, verifiserbar oppgave — i motsetning til «gjør
UIet penere», som den ikke kan gjøre noe fornuftig med.

---

## Vedlegg A — Spesifikasjon: felles `KpiCard`-komponent

Denne komponenten erstatter `KpiCard` (gammel), `KpiTrendCard`, den ubrukte
`.dk-stat`-CSS-en og alle ~48 inline KPI-kort. Den dekker alle tilstandene
de fire mønstrene i dag løser hver for seg.

### Parametere

| Parameter | Type | Default | Beskrivelse |
|---|---|---|---|
| `Label` | `string` | påkrevd | Etikett øverst i kortet |
| `Value` | `string` | påkrevd | Ferdigformatert hovedverdi, f.eks. «19 618 MWh» |
| `Accent` | `KpiAccent` | `Auto` | Semantisk farge på venstrekanten |
| `SubText` | `string?` | `null` | Liten tekst under verdien (vises kun når ingen trend) |
| `TrendCurrent` | `double?` | `null` | Nåverdi for trendberegning |
| `TrendPrevious` | `double?` | `null` | Forrige periode for trendberegning |
| `HigherIsBetter` | `bool` | `true` | Avgjør om oppgang er grønn (true) eller rød (false) |
| `InfoText` | `string?` | `null` | Forklaring i info-tooltip; ikon vises kun hvis satt |
| `InfoFormula` | `string?` | `null` | Valgfri formel-linje i tooltip (monospace) |
| `State` | `KpiState` | `Normal` | `Normal`, `NoData` eller `Loading` |

```csharp
public enum KpiAccent { Auto, Neutral, Positive, Negative, Warning, Muted }
public enum KpiState  { Normal, NoData, Loading }
```

### Oppførsel

- **Trend:** når `TrendCurrent` og `TrendPrevious` begge er satt og forrige
  ≠ 0, vises pil + fortegns-prosent + «vs forrige». Endring under 0,5 %
  regnes som flat. Farge: oppgang × `HigherIsBetter` → positiv (grønn);
  oppgang × ikke-bedre → negativ (rød); motsatt ved nedgang.
- **Trend kontra `SubText`:** vises trend, ignoreres `SubText` — de deler
  nederste linje.
- **`Accent = Auto`:** følger trendfargen hvis trend finnes, ellers
  `Neutral`. Et eksplisitt `Accent`-valg overstyrer alltid.
- **`State = NoData`:** verdien dempes til «—», en «ingen data»-merkelapp
  vises, og kanten tvinges til `Muted`. Erstatter dagens tvetydige «–».
- **`State = Loading`:** verdifeltet viser en skjelett-plassholder.
- Alle farger hentes fra de semantiske tokenene i tiltak 2.2 — ingen
  hardkodede hex. Dermed virker lys/mørk modus automatisk.

### Token-mapping (defineres ett sted, jf. tiltak 2.2)

`Neutral` = merkevareblå · `Positive` = grønn · `Negative` = korall/rød ·
`Warning` = gul · `Muted` = grå.

### Bruk

```razor
<KpiCard Label="Produksjon"
         Value="@FormatMwh(total)"
         SubText="@($"{antall} anlegg · {mw:F1} MW")"
         InfoText="Total levert energi til Elhub i perioden." />

<KpiCard Label="Spotomsetning"
         Value="@FormatNok(spot)"
         TrendCurrent="spot" TrendPrevious="spotForrige"
         HigherIsBetter="true" />

<KpiCard Label="Reddet av vakt"
         Value="@FormatNok(reddet)"
         State="@(harVaktData ? KpiState.Normal : KpiState.NoData)" />
```

Tre kall, tre tilstander — statisk verdi, verdi med trend, og «ingen data».
Samme komponent dekker alt de ~48 håndlagde kortene gjør i dag.
