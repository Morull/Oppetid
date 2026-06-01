# NESTE CHAT — Økonomi-fane under Portefølje + PDF-rapport (QuestPDF)

**Dato:** 2026-05-22
**Estimat:** 6–8 dager (.NET + Blazor + ny PDF-pipeline)
**Avhengighet:** Bygger på eksisterende KAIA-kostnad, Capture rate, Settlement og Vakt-ROI-tjenester — alle tall finnes allerede, dette er aggregering + presentasjon + eksport.

---

## Mål

Drifts-leder skal kunne se økonomisk status for valgt periode på (a) ett anlegg, (b) et utvalg anlegg, eller (c) hele porteføljen. KPI-ene vises som oversiktlige kort med trend-piler mot forrige tilsvarende periode, og hele bildet kan eksporteres som en lekker PDF med samme designspråk som appen.

## Beslutninger (avklart 2026-05-22)

| Spørsmål | Beslutning |
|---|---|
| Plassering | **Under Portefølje, som ny underfane «Økonomi»** (Portefølje får to faner: Sammendrag = dagens visning, Økonomi = ny). |
| Hovedresultat-tall | **Oppgjør** — slik det vises på Rapport-detalj i dag. |
| PDF-stack | **QuestPDF** (.NET, MIT). Skal matche appens designspråk (mørk bakgrunn, samme aksent-farger). |

> **Forutsetning:** Oppgjør-bruddet flagget i `FORBEDRINGSFORSLAG-KPI-KORT-INTEGRITET.md` bør være ryddet før Oppgjør gjøres til hovedtall — enten ved at Oppgjørs synlige komponenter avstemmes, eller ved at Oppgjør står som eget kort i en separat «Resultat»-seksjon. Behold det skillet i Økonomi-fanen.

---

## Del 1 — Portefølje får underfaner

`Web/Pages/Portefolje.razor` refaktoreres til en container med `MudTabs`:

```razor
<MudTabs>
    <MudTabPanel Text="Sammendrag">
        @* Dagens innhold: 4 toppkort + per-anleggs-tabell *@
    </MudTabPanel>
    <MudTabPanel Text="Økonomi">
        <OkonomiFane PeriodFrom="@_from" PeriodTo="@_to" />
    </MudTabPanel>
</MudTabs>
```

Periode-velgeren forblir i topp-baren og deles av begge faner. Anleggs-velgeren (Del 2) ligger **inne i Økonomi-fanen** — Sammendrag er fortsatt «alle anlegg».

---

## Del 2 — Anleggs-velger (tre moduser i én komponent)

Ny komponent `Web/Pages/Components/AnleggVelger.razor`. Tre faste valgmoduser:

| Modus | UI | Resultat |
|---|---|---|
| Ett anlegg | `MudSelect` med 11 alternativer | `selectedPlants = [plantId]` |
| Utvalg | `MudSelect` med `MultiSelection=true`, viser chips | `selectedPlants = [a, b, c]` |
| Alle | `MudButton` «Alle (11)» som forhåndsvelger alle | `selectedPlants = alle 11` |

Tilstand bobles til foreldre-komponenten via `EventCallback<List<string>>`. Persister valget i `sessionStorage` slik at det overlever sidebytte men nullstilles ved ny fane/login.

---

## Del 3 — KPI-kort med trend-piler

### Gruppe-struktur (tre tydelige seksjoner)

Hver seksjon har en seksjonstittel slik at `FORBEDRINGSFORSLAG-KPI-KORT-INTEGRITET.md`-regelen oppfylles automatisk.

**Inntekter**

| KPI | Aggregering over anlegg | Opp = |
|---|---|---|
| Spotomsetning (NOK) | sum | grønn |
| Capture rate (forholdstall) | MWh-vektet snitt | grønn |
| Merverdi vs spot (NOK) | sum | grønn |

**Kostnader**

| KPI | Aggregering | Opp = |
|---|---|---|
| Ubalansekost (NOK) | sum | rød |
| KAIA-kostnad (NOK) | sum | rød |
| Vakt-kost-andel (NOK) | sum av (samlet portefølje-vaktkost × GWh-andel for hvert valgt anlegg) | rød |

**Resultat / drift**

| KPI | Aggregering | Opp = |
|---|---|---|
| Oppgjør (NOK) | sum | grønn |
| Nedetidstap (NOK) | sum | rød |
| Reddet av vakt (NOK) | sum | grønn |

### Kort-anatomi

```
┌──────────────────────────────────┐
│  Spotomsetning                   │
│  20 404 688 NOK                  │
│  ▲ +12,4 %  vs forrige måned     │
└──────────────────────────────────┘
```

- **Etikett** (overline).
- **Hovedtall** (h4), norsk formatering (mellomrom som tusenskille, komma som desimal, samme `NorskTall`-hjelper som resten av appen).
- **Trend-linje**: pil (▲/▼) + prosent + etikett for sammenligningsperiode. Farge bestemmes per KPI (tabellen over) — ikke av tegn alene. F.eks. en oppgang i Ubalansekost er rød ▲.
- Hvis forrige periode er 0 eller mangler data: vis «– (ingen sammenligning)» i grått, ingen pil.

### «Forrige periode» — definisjon

- Måned: foregående kalendermåned (April → Mars).
- Kvartal: foregående kvartal (Q2 2026 → Q1 2026).
- År: foregående år (2026 → 2025).
- Hittil i år: samme dato-spenn i fjor (1.1–22.5.2026 → 1.1–22.5.2025).
- Egendefinert: samme lengde umiddelbart før valgt periode.

Implementer som en ren funksjon `PreviousPeriod(from, to, periodKind) -> (prevFrom, prevTo)` i `Modules.Reporting`. Skal være enhetstestet.

---

## Del 4 — Backend

### DTO og kontrakt

`Modules.Reporting/Economy/EconomyReportDto.cs`:

```csharp
public sealed record EconomyReportDto(
    string[] PlantIds,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset PrevFrom,
    DateTimeOffset PrevTo,
    EconomyKpiGroupDto Inntekter,
    EconomyKpiGroupDto Kostnader,
    EconomyKpiGroupDto Resultat,
    PerPlantEconomyDto[] PerPlant);

public sealed record EconomyKpiDto(
    string Key,             // f.eks. "spotomsetning"
    string Label,           // f.eks. "Spotomsetning"
    double Verdi,
    string Enhet,           // "NOK", "ratio", "MWh"
    double? VerdiForrige,
    double? EndringProsent,
    string GoodDirection);  // "up" eller "down"

public sealed record EconomyKpiGroupDto(string Title, EconomyKpiDto[] Kpis);
```

### Service

`Infrastructure/Reporting/EconomyReportQueryService.cs` implementerer `IEconomyReportQueryService` og samler tallene fra eksisterende tjenester:

- `Spotomsetning, Oppgjør, Ubalansekost` — `SettlementQueryService` (eller tilsvarende).
- `KAIA-kostnad` — `IKaiaCostQueryService` (finnes).
- `Capture rate, Merverdi` — `CaptureRateQueryService`.
- `Vakt-kost-andel` — fra `PortfolioVaktRoiQueryService` (bruker GWh-andelen som allerede vises på Vakt-ROI per anlegg).
- `Nedetidstap` — `NedetidQueryService`.
- `Reddet av vakt` — `PortfolioVaktRoiQueryService.totalReddetNok` summert over valgte anlegg.

Tjenesten kjører **to passeringer** — én for valgt periode, én for `PreviousPeriod(…)` — og bygger DTO-en. Parallelliser kall over anlegg (samme mønster som dagens portefølje-fetch).

### API-endepunkt

`Api/Endpoints/EconomyEndpoints.cs`:

```
GET  /api/v1/economy?plants=haukland,vikesa&from=…&to=…
GET  /api/v1/economy/pdf?plants=…&from=…&to=…       → application/pdf
```

`plants` kan også være `all` (= 11 anlegg). Tomt sett → 400.

---

## Del 5 — Frontend

### Filer

- `Web/Pages/Components/OkonomiFane.razor` — hovedvisningen.
- `Web/Pages/Components/AnleggVelger.razor` — gjenbrukbar velger (Del 2).
- `Web/Pages/Components/KpiKort.razor` — én komponent som tar `EconomyKpiDto` og rendrer kort + trend-pil.
- `Web/Services/EconomyApi.cs` — HttpClient-wrapper.

### Layout

```
[Anleggs-velger: ⦿ Ett anlegg ⦾ Utvalg ⦾ Alle]   [Last ned PDF]
                                                     
Inntekter
[Spotomsetning]  [Capture rate]  [Merverdi vs spot]

Kostnader
[Ubalansekost]   [KAIA-kostnad]  [Vakt-kost]

Resultat / drift
[Oppgjør]        [Nedetidstap]   [Reddet av vakt]

[Per-anlegg-tabell, vises kun hvis flere anlegg valgt]
```

Per-anlegg-tabell (kun ved multi-anleggs-valg): én rad per anlegg, kolonner = de viktigste KPI-ene (Oppgjør, Spotomsetning, Ubalansekost, KAIA-kostnad, Capture rate). Sorterbar — gjenbruk mønsteret fra `FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING.md`.

---

## Del 6 — PDF-eksport med QuestPDF

### Pakke + DI

NuGet: `QuestPDF` (siste stable, MIT for non-commercial — kommersielle organisasjoner trenger Community License-bekreftelse, gratis under terskelen). Registrer settings i `Program.cs`:

```csharp
QuestPDF.Settings.License = LicenseType.Community;
```

### Sider

| Side | Innhold |
|---|---|
| Forside | Dalane Kraft-logo, tittel «Økonomi-rapport», periode, valgte anlegg (eller «Hele porteføljen (11 anlegg)»), generert dato/tidspunkt. |
| Sammendrag | De 9 KPI-kortene fra UI-en, gruppert som Inntekter / Kostnader / Resultat. Trend-piler og prosent inkludert. |
| Måneds-trend | Bardiagram (12 mnd) for Spotomsetning og Oppgjør, side om side. |
| Per anlegg (kun ved multi) | Tabell, én rad per anlegg, samme kolonner som UI-ens per-anlegg-tabell. |
| Vedlegg | Nedetid-kategori-fordeling + KAIA-breakdown (Megler vs Fast) per anlegg. |

### Designspråk (mørk tema, matcher appen)

Definer en `EconomyPdfTheme.cs` med konstanter som matcher appens CSS-variabler:

```csharp
public static class EconomyPdfTheme
{
    public static Color Background  = Color.FromHex("#0F1620");  // ≈ appens dk-bg
    public static Color Surface     = Color.FromHex("#1A2330");  // kort-flate
    public static Color TextPrimary = Color.FromHex("#E6EDF5");
    public static Color TextMuted   = Color.FromHex("#8DA0B5");
    public static Color AccentGood  = Color.FromHex("#3DDC97");
    public static Color AccentBad   = Color.FromHex("#F45B69");
    public static string FontFamily = "Inter";   // eller systemfont som ligner appens
}
```

Hent faktiske hex-verdier fra `dk-theme.css` slik at PDF-en speiler skjermbildet. Embed Inter-fonten i container-imaget hvis den brukes.

### Graf-tegning

QuestPDF har ikke innebygd chart-engine, men kan vise bilder. To realistiske veier:

1. **SkiaSharp** for å tegne bardiagrammene som PNG som så embedes — enkleste og fungerer i Docker. Anbefalt.
2. **Embed SVG** — QuestPDF støtter SVG via `Svg(string)`. Hvis dere har ApexCharts allerede kan SVG-eksport derfra brukes, men det kobler PDF-en til frontend.

Anbefaler SkiaSharp-rute — én helper-klasse `EconomyChartRenderer.cs` som tar en serie og returnerer `byte[] png`.

### Filnavn og levering

```
Okonomi_<scope>_<periodelabel>.pdf
```

- `scope`: `Haukland` / `Utvalg-3anlegg` / `Hele-portefolje`.
- `periodelabel`: f.eks. `2026-04` for måned, `2026-Q2` for kvartal, `2026-01-01_2026-05-22` for egendefinert.

Endpoint returnerer `application/pdf` med `Content-Disposition: attachment; filename=…`. Frontend laster ned via en `<a>`-link mot endepunktet i ny fane.

---

## Del 7 — Tester

- `PreviousPeriodCalculator`-tester: dekk Måned, Kvartal, År, Hittil i år, Egendefinert.
- `EconomyReportQueryService`-tester med stubbede underliggende tjenester: bekreft at NOK-tall summeres, at Capture rate MWh-vektes, at trend-prosent regnes riktig (inkludert div-by-zero-håndtering).
- Snapshot-test for PDF-en: generer en kjent rapport, verifiser at totalt antall sider og posisjon av nøkkelelementer (forside-tittel, KPI-summary) er som forventet. (QuestPDF har `Document.GeneratePdfAndShow` for utvikling, og en test-mode for verifisering.)
- Aksept-test: 5 anlegg × april 2026 produserer et faktisk PDF-dokument på disk i CI som kan inspiseres manuelt.

---

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `PreviousPeriod` ren-funksjon + tester | 0,5 dag |
| 2 | `EconomyReportDto` + `IEconomyReportQueryService` + implementasjon | 1,5 dag |
| 3 | API-endepunkt `/economy` (uten PDF) + Web-klient | 0,5 dag |
| 4 | `AnleggVelger`-komponent | 0,5 dag |
| 5 | `KpiKort`-komponent + Økonomi-fane-layout + Portefølje-underfane-refaktor | 1,5 dag |
| 6 | `EconomyChartRenderer` (SkiaSharp) + tester | 0,5 dag |
| 7 | `EconomyPdfBuilder` (QuestPDF) — forside, sammendrag, trend, per-anlegg, vedlegg | 1,5 dag |
| 8 | `/economy/pdf`-endepunkt + nedlastings-knapp | 0,5 dag |
| 9 | Polering, ikon-konvensjon, responsiv-test, akseptkjøring | 0,5 dag |
| **Sum** | | **~7 dager** |

Punkt 1–5 gir Økonomi-fanen alene (uten PDF) som kan demoes til drifts-leder. Punkt 6–8 legger PDF oppå.

---

## Akseptkriterier

- [ ] Portefølje-siden har to underfaner: «Sammendrag» (uendret) og «Økonomi» (ny).
- [ ] Anleggs-velgeren støtter ett anlegg, utvalg av flere, og alle 11.
- [ ] 9 KPI-kort vises gruppert i tre seksjoner med tydelige titler. Hvert kort viser verdi, enhet, trend-pil og prosent vs forrige periode.
- [ ] Trend-pilens farge følger KPI-spesifikk «god retning», ikke bare tegn på endringen.
- [ ] «Forrige periode» er korrekt definert for Måned, Kvartal, År, Hittil i år, Egendefinert (enhetstestet).
- [ ] NOK-tall summeres på tvers av valgte anlegg; Capture rate MWh-vektes.
- [ ] PDF kan lastes ned med riktig filnavn-mønster.
- [ ] PDF-rapporten har forside, KPI-sammendrag, måneds-trend-bardiagram, per-anlegg-tabell (ved multi) og vedlegg.
- [ ] PDF-en bruker samme aksent-farger og typografi som appen (verifiseres visuelt mot et skjermbilde).
- [ ] Periode-velgeren i topp-baren styrer både UI-en og PDF-en.
- [ ] Sortering virker på per-anlegg-tabellen.
- [ ] Tomt anleggs-utvalg → UI viser «Velg minst ett anlegg»; API svarer 400.

---

## Merknader

- **KPI-integritets-avhengighet:** Oppgjør står som hovedresultat, men Oppgjør avstemmer ikke med synlige nabokort på Rapport-detalj i dag (gap på 55 273 NOK funnet 2026-05-22). Hvis dette ikke ryddes før Økonomi-fanen rulles ut, dupliseres feilen. Anbefaling: fiks Oppgjør-bruddet (vis komponentene, eller skill kortet visuelt) først, eller la Økonomi-fanens Oppgjør-kort vise samme komponenter i undertittelen — slik KAIA-kostnad-kortet gjør i dag.
- **Vakt-kost-andel:** Forutsetter at porteføljeverdien er persistert mot DB (sjekkpunkt fra `NESTE-CHAT-VAKTROI-OG-UI-FIKS.md` Del B). Hvis ikke, gjør det først — Økonomi-rapporten må kunne reprodusere samme tall ved gjenåpning.
- **Senere utvidelser** (ikke i denne instruksen): tidsserier per KPI som drill-down, sammenligning mot «normalår», eksport til Excel. Bygg fundamentet først.
