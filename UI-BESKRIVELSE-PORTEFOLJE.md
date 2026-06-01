# Beskrivelse av Portefølje-rapport — grensesnitt for ekstern UI-vurdering

Dette dokumentet er skrevet for å limes inn i ChatGPT (eller et annet verktøy)
med en forespørsel om konkrete forbedringsforslag til det visuelle grensesnittet.
Alt under er faktisk hentet fra kildekoden, ikke gjettet.

---

## 1. Kontekst

Applikasjonen heter «Uptime» og er et internt driftsverktøy for Dalane Kraft.
Den samler SCADA- og avregningsdata for ~9–11 småkraftverk og vindpark, og
presenterer produksjon, nedetid, økonomi (spotomsetning, merverdi) og en
«Vakt-ROI»-analyse. Brukeren er én driftsleder som ser på dette på en stor
desktop-skjerm. Språket i hele grensesnittet er norsk.

Skjermbildet det gjelder er «Portefølje-rapport» (`/portefolje`) — en samlet
tabell på tvers av alle anlegg for en valgt periode.

## 2. Teknisk plattform og harde rammer

Forslag må kunne realiseres innenfor disse rammene, ellers er de ikke nyttige:

- **Blazor Server (.NET 10)**, Razor-komponenter (`.razor`).
- **MudBlazor** som komponentbibliotek — alt UI bygges av Mud-komponenter
  (`MudTable`, `MudGrid`, `MudPaper`, `MudAppBar`, `MudDrawer`, `MudChip`,
  `MudAlert`, `MudTooltip`, `MudIcon` med Material-ikoner). Egendefinert CSS
  er mulig, men store avvik fra MudBlazor-mønsteret krever at man enten
  overstyrer komponentene tungt eller bytter dem ut.
- **Egendefinert MudBlazor-tema** (`DalaneTheme`) med både lys og mørk modus.
- Skrift: **Inter** (fallback system-ui / Segoe UI / Roboto).
- Standard hjørneradius 8 px, app-bar-høyde 64 px.
- Innholdet ligger i en `MudContainer MaxWidth="Large"` (maks ~1280 px bred)
  med 24 px padding — på en bred skjerm gir det store tomme marger på sidene.

Forslagsgiver bør si tydelig fra når et forslag krever å gå utenfor MudBlazor.

## 3. Fargepalett (merkevare fra Dalane Kraft-logo)

| Rolle | Lys modus | Mørk modus |
|---|---|---|
| Primary (blå) | `#1F84B7` | `#4DB0DC` |
| Secondary (teal) | `#1AA09F` | `#3FCBC9` |
| Tertiary (grønn) | `#2A8B5C` | `#5BCC8E` |
| Warning | `#E9A235` | `#F0BB55` |
| Error | `#D14B3F` | `#E5685C` |
| Bakgrunn | `#F4F8FB` | `#0A1A2A` |
| Surface (kort) | `#FFFFFF` | `#13263A` |
| App-bar | `#0E2F47` | `#061321` |

Skjermbildet som vurderes er tatt i **mørk modus**.

## 4. Global ramme (vises på alle rapport-sider)

**Topplinje (app-bar):** mørk navy, 64 px høy. Fra venstre: hamburger-ikon
for å åpne/lukke menyen, «D DALANE KRAFT»-logo, teksten «Uptime», en liten
«v1»-chip. Til høyre: knapp for lys/mørk tema og en konto-meny.

**Venstre navigasjonsskinne:** en MudBlazor «mini»-drawer som utvides ved
hover. Menypunkter med ikon + tekst: Rapporter, Portefølje, Nedetid,
Vakt-ROI, Capture rate, Produksjon, Anlegg, Data-Import, Kategorier.

**Anlegg- og periodevelger:** en horisontal stripe rett under topplinjen.
Inneholder en nedtrekksliste for anlegg (viser «Øgreyfoss» i skjermbildet),
en datovisning med fram/tilbake-piler («19.4–19.4.2026»), og fem
hurtigknapper for periode: Måned, Kvartal, År, Hittil i år, Egendefinert.
Merk: på Portefølje-siden gjelder rapporten *alle* anlegg, så anleggs-
nedtrekkslisten har egentlig ingen funksjon her, men vises likevel.

## 5. Selve Portefølje-siden

I rekkefølge ovenfra og ned:

1. **Tittel** «Portefølje-rapport» (h1) + en grå underteksttekst på to
   linjer som forklarer at man kan klikke en rad for detaljer og holde
   musepekeren over kolonneoverskrifter for forklaring.

2. **Valgfritt gult varselbanner** hvis noen anlegg mangler deldata.

3. **KPI-stripe** — fire kort på rad (MudGrid, hvert kort 1/4 bredde på
   desktop, stables på smal skjerm). Hvert kort har en 4 px farget
   venstrekant, en liten grå overskrift, et stort tall (h4) og en liten
   undertekst:
   - Produksjon — blå kant — «19617.9 MWh» + «9 anlegg · 43.0 MW installert»
   - Spotomsetning — teal kant — «19 895 667 NOK» + «Merverdi: −97 340»
   - Nedetidstap — korall kant — «2 164 NOK» + «2 timer · 2 hendelser»
   - Reddet av vakt — lilla kant — «– NOK» + «2 av 2 events reddbare»

4. **Hovedtabell** inne i et kort med overskriften «Per-anleggs detaljer
   (9 anlegg)». Tabellen er en `MudTable` i tett («Dense») modus med
   hover-effekt, klikkbare rader (navigerer til anleggets detaljside) og
   sorterbare kolonner. Den har **13 kolonner**:

   Anlegg · Effekt (MW) · AF · FOR · Produksjon (MWh) · Spotoms (NOK) ·
   CR · Merverdi · Nedetid (t) · Tap (NOK) · Reddet vakt (NOK) ·
   Reddbare · Datakvalitet

   Alle tallkolonner er høyrejustert. Første kolonne (Anlegg) er «sticky» —
   den blir stående når man scroller tabellen vannrett. Anleggsnavnet vises
   i primærfarge. Nederst en uthevet «SUM»-rad med kolonnesummer; kolonner
   der en sum ikke gir mening (AF, FOR, CR) viser «—». Tomme/null-verdier
   vises gjennomgående som «–».

## 6. Observerte svakheter (kandidatområder for forbedring)

Disse er notert fra skjermbildet og koden, og er ment som utgangspunkt —
ikke en fasit:

- **Tabellen har 13 kolonner, men bare ~4 får plass på skjermen.** Resten
  ligger bak vannrett scrolling. Mesteparten av innholdet er skjult ved
  første blikk, og vannrett scrolling i en tabell er tungvint.
- **Mye ubrukt loddrett plass.** Innholdet klumper seg øverst til venstre;
  under tabellen er det et stort tomt, mørkt felt.
- **Innholdsbredden er låst til ~1280 px**, samtidig som tabellen trenger
  *mer* bredde enn det. Resultatet er brede tomme marger ved siden av en
  tabell som likevel må scrolles.
- **Anleggsvelgeren i toppstripen er irrelevant på denne siden** (rapporten
  gjelder alle anlegg), men vises uansett — potensielt forvirrende.
- **Den valgte perioden er én enkelt dag** («19.4–19.4.2026»), noe som er
  en uvanlig tilstand for en porteføljerapport.
- KPI-kortene og tabellteksten virker små i forhold til skjermflaten.
- Fargebruk: fire forskjellige aksentfarger på KPI-kortene (blå, teal,
  korall, lilla) uten at fargene tydelig koder en betydning.

## 7. Hva som bør beholdes

- MudBlazor som plattform og det eksisterende Dalane-temaet med lys/mørk.
- Norsk språk.
- Den sticky første kolonnen, sorterbare kolonner, klikkbare rader,
  tooltip-forklaringene på kolonneoverskrifter og «mangler data»-varslene —
  dette er funksjonalitet som fungerer.
- SUM-raden.

## 8. Forespørsel til ChatGPT

> Dette er et internt driftsverktøy bygget i Blazor Server med MudBlazor.
> Gi meg konkrete forslag til hvordan det visuelle grensesnittet for denne
> «Portefølje-rapport»-siden kan forbedres. Jeg er særlig interessert i:
> hvordan håndtere en tabell med 13 kolonner uten tung vannrett scrolling;
> hvordan utnytte skjermflaten bedre; og hvordan gjøre informasjonshierarkiet
> tydeligere. Hold forslagene realiserbare i MudBlazor, og si tydelig fra
> dersom et forslag krever å gå utenfor komponentbiblioteket. Prioriter
> forslagene etter forventet effekt mot innsats.
