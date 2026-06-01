# NESTE CHAT — Oppfølging Økonomi-fane: tre konkrete funn

**Dato:** 2026-05-22
**Bygger på:** `NESTE-CHAT-OKONOMI-FANE-PDF.md` (samme dag, første leveranse) — Økonomi-fanen er i hovedsak bygget og fungerer. Disse tre punktene må fikses før fanen kan stoles på i produksjon.

Skrives etter live-test 2026-05-22 ettermiddag. Alle tre er bekreftet med faktiske tall fra kjørende app + API.

---

## Funn 1 — Spotomsetning er feilaggregert for «Hittil i år» og «Egendefinert» (kritisk)

API-en returnerer omtrent april-verdien for hele ÅTD-perioden i stedet for summen over måneder.

### Bevis (hentet 2026-05-22 ettermiddag fra kjørende `/api/v1/economy`)

Spotomsetning per måned, alle 11 anlegg, april 2026 og bakover:

| Periode | Kall | Spotomsetning (NOK) |
|---|---|---|
| Januar 2026 | `kind=Month, from=01-01, to=02-01` | 19 644 120 |
| Februar 2026 | `kind=Month, from=02-01, to=03-01` | 27 579 297 |
| Mars 2026 | `kind=Month, from=03-01, to=04-01` | 22 733 769 |
| April 2026 | `kind=Month, from=04-01, to=05-01` | 20 404 688 |
| Mai (1–29) | `kind=Month, from=05-01, to=05-29` | 8 003 323 |
| **Sum av månedene** | | **98 365 197** |
| **«Hittil i år»** | `kind=YearToDate, from=01-01, to=05-29` | **21 086 823** |
| **«Egendefinert» (samme spenn)** | `kind=Custom, from=01-01, to=05-29` | **21 086 823** |

ÅTD/Custom underrapporterer med ~77 millioner NOK. Tallet de returnerer ligger 3 % over april alene — ikke en avrundingsfeil.

Per-anlegg-listen `EconomyReportDto.perPlant` for ÅTD viser samme bilde: 9 av 11 anlegg returnerer eksakt sine april-verdier (Haukland 2 266 525, Øgreyfoss 7 202 941, Lindland 4 888 005 osv. — identiske med april-tallene på Portefølje-Sammendrag).

### Rotårsak (sannsynlig)

`EconomyReportQueryService` ser ut til å bare aggregere settlement-importene for `kind=Month` og evt. `Quarter`/`Year`, men når `YearToDate` eller `Custom` brukes faller den tilbake til siste import-måned. Sjekk om SQL-en filtrerer på `import_month = …` i stedet for `period_start BETWEEN from AND to`. Samme mønster må gjelde for Capture rate/Merverdi/Oppgjør på samme periode (verifiser disse også — Oppgjør viste 21 549 204 for ÅTD, som ser tilsvarende lavt ut sammenliknet med en sum av månedene).

### Akseptkriterier

- [ ] `kind=YearToDate` og `kind=Custom` returnerer korrekt sum over hele periode-spennet, ikke siste måned.
- [ ] For ÅTD (Jan 1 – Mai 29, 2026): Spotomsetning ≈ 98 mill NOK. Avvik fra «sum av månedene» < 1 %.
- [ ] Per-anlegg-listen `perPlant` reflekterer sum-perioden — ikke siste måned.
- [ ] Ny enhetstest: `EconomyReportQueryService.GetAsync(YearToDate, …)` mot et seedet datasett med tre måneders settlement-data — sum-resultatet matcher 3×månedsbeløp.
- [ ] Portefølje-Sammendrag-fanen viser samme tall (de henter trolig fra samme tjeneste — bekreft).

---

## Funn 2 — SUM-raden i Portefølje-Sammendrag-tabellen bryter skaleringen (UX)

Drifts-leder rapporterer at SUM-raden «ødelegger skaleringen av tabellen» — verifisert i screenshot 2026-05-22 (Hittil i år, 13 synlige kolonner). SUM-cellene glir bort fra kolonnene de skal summere. Eksempel: «20608,2» legger seg ikke under «Produksjon (MWh)»-kolonnen, og «21 086 823» legger seg ikke under «Spotoms (NOK)».

Dette ble allerede beskrevet i `NESTE-CHAT-VAKTROI-OG-UI-FIKS.md` Del C, men gjentas her med mer presis instruks fordi det ble enda mer synlig etter at flere kolonner ble lagt til.

### Rotårsak

`Portefolje.razor` rendrer SUM-raden som en separat `<tfoot><tr>` med fri `<td>`-layout. `<tfoot>`-cellene tar ikke `width` fra MudDataGrid-kolonnenes definisjon — de flekker fritt for å fylle rad-bredden. På smal viewport fungerer det tilfeldigvis; på 13-kolonners ultrawide brytes alignmenten.

### Endring

Bytt mønster fra fri tfoot-rad til **per-kolonne `Footer`-binding** i MudDataGrid:

```razor
<MudDataGrid Items="@_rows" Hover="true">
  <Columns>
    <PropertyColumn Property="@(r => r.Anlegg)"            Title="Anlegg"
                    HeaderClass="text-start" CellClass="text-start" FooterClass="text-start">
      <FooterTemplate>SUM</FooterTemplate>
    </PropertyColumn>
    <PropertyColumn Property="@(r => r.EffektMw)"          Title="Effekt (MW)"
                    HeaderClass="text-end" CellClass="text-end" FooterClass="text-end">
      <FooterTemplate>@_sum.EffektMw.ToString("N1", _nb)</FooterTemplate>
    </PropertyColumn>
    <PropertyColumn Property="@(r => r.SpotomsNok)"        Title="Spotoms (NOK)"
                    HeaderClass="text-end" CellClass="text-end" FooterClass="text-end">
      <FooterTemplate>@_sum.SpotomsNok.ToString("N0", _nb)</FooterTemplate>
    </PropertyColumn>
    @* … resten av kolonnene tilsvarende … *@
  </Columns>
</MudDataGrid>
```

Da arver hver `FooterTemplate`-celle kolonnens bredde og justering. Resultatet: SUM-tallene stables eksakt under tallene de summerer.

I tillegg, slik som spesifisert i `FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING.md` Del 1: **alle numeriske kolonner skal høyrejusteres** (`text-end` på Header, Cell, Footer). Tekstkolonnen Anlegg er den eneste som beholder venstrejustering.

### Akseptkriterier

- [ ] Ingen `<tfoot>`-rad i `Portefolje.razor`. Alle kolonner med sum bruker `FooterTemplate` inne i `PropertyColumn`.
- [ ] Numeriske kolonner høyrejustert i header, body og footer.
- [ ] Anlegg-kolonnen venstrejustert.
- [ ] SUM-tallene er visuelt vertikalt på linje med kolonnens data, både med 7 og 13 synlige kolonner.
- [ ] Ingen horisontal scroll-trigger fra at SUM-raden er bredere enn body-radene.

---

## Funn 3 — HTTP 429 Too Many Requests ved periode-bytte (ytelse)

Drifts-leder ser feilmeldingen «net_http_message_not_success_statuscode_reason, 429, Too Many Requests» med jevne mellomrom på Økonomi-fanen.

### Bevis

Instrumentert `window.fetch` på Portefølje-siden, klikket «Hittil i år», telt kall i 8 sekunder:

| Endepunkt | Antall kall |
|---|---|
| `/api/v1/portfolio/kaia-cost` | 1 |
| `/api/v1/plants/{anlegg}/vakt-roi` | 11 |
| `/api/v1/plants/{anlegg}/nedetid` | 11 (delvis truncated) |
| `/api/v1/economy` | 1 |
| **Sum i burst** | **~24 parallelle kall på <1 sek** |

API-ens rate-limiter (sannsynlig fixed-window per IP) treffes ved den tettheten. 429 returneres for noen av kallene, og UI viser feilmeldingen.

### Rotårsak

Når brukeren er på Økonomi-underfanen, lastes **Sammendrag-underfanen i parallell** — dens komponent-tre rendrer i bakgrunnen og initierer alle per-anleggs-kallene. Det er ikke ekspander-/`@if`-gatet på aktiv-fane.

### Endring (anbefalt, fjerner rotårsaken)

I `Portefolje.razor` skal hver `MudTabPanel` sitt innhold kun initialisere når fanen er aktiv. To akseptable mønstre:

**A. `@if (@_activeTab == X)` rundt fane-innhold:**

```razor
<MudTabs ActivePanelIndexChanged="@OnTabChanged">
  <MudTabPanel Text="Sammendrag">
    @if (_activeTab == 0) { <PortefoljeSammendrag From="@_from" To="@_to" /> }
  </MudTabPanel>
  <MudTabPanel Text="Økonomi">
    @if (_activeTab == 1) { <OkonomiFane From="@_from" To="@_to" /> }
  </MudTabPanel>
</MudTabs>
```

**B. MudBlazor `KeepPanelsAlive="false"`** på `MudTabs`, så ikke-aktiv fane-innhold tas ut av DOM-en.

Resultat: ved Økonomi-fanen sendes kun `/api/v1/economy` (+ evt. `/economy/pdf` på klikk). Det fjerner 22+ unødvendige kall per periode-bytte.

### Sekundær endring (forsiktighet)

Selv med fane-isolasjon kan brukeren bytte mellom Sammendrag og Økonomi raskt. Legg inn en lett debouncing/coalescing i `EconomyApi`-klienten (HttpClient-wrapper): hvis et nytt kall starter mens et med samme parametere er i flight, returner samme `Task<EconomyReportDto>` — ikke send to. Standardmønster, 5 linjer.

### Akseptkriterier

- [ ] På Økonomi-fanen sendes maks **2** kall per periode-bytte: `/api/v1/economy` + evt. `/economy/pdf`.
- [ ] Ingen `/vakt-roi`, `/nedetid`, `/kaia-cost`-kall mens Økonomi-fanen er aktiv.
- [ ] 429-feilmeldingen forsvinner i normal bruk (verifiser ved å bytte periode 10 ganger på rad).
- [ ] Bytting tilbake til Sammendrag-fanen laster den friskt — uten å holde igjen Økonomi-statusen.

---

---

## Funn 4 — PDF-knappen navigerer til feil host (frontend)

«Last ned PDF»-knappen er bygget, men ved klikk navigerer Blazor til:

```
http://localhost:5180/api/v1/economy/pdf?plants=…&kind=YearToDate
```

Port **5180** er web-appen — den har ikke det endepunktet og svarer «Siden ble ikke funnet». Riktig host er **5080** (API-et).

### Rotårsak

Sannsynligvis kalles `NavigationManager.NavigateTo("/api/v1/economy/pdf?…")` med relativ URL, slik at Blazor prefikser med web-appens base. Skal i stedet prefikse med `ApiBaseAddress` fra `wwwroot/appsettings.json` — samme som `HttpClient.BaseAddress` allerede er konfigurert med i `Program.cs`.

### Endring

I `OkonomiFane.razor` (eller `EconomyApi.cs`):

```csharp
var apiBase = Configuration["ApiBaseAddress"]!.TrimEnd('/');
var url = $"{apiBase}/api/v1/economy/pdf?{queryString}";
Navigation.NavigateTo(url, forceLoad: true);
```

Eller, ryddigere: lag en `EconomyApi.BuildPdfUrl(plants, from, to, kind)` som bruker `_http.BaseAddress` (allerede satt fra appsettings) og bygger absolutt URL. Bruk denne både til `<a href="...">` og til `NavigateTo`.

Åpne i ny fane (`target="_blank"`) er anbefalt mønster for nedlasting, så hovedfanen ikke flytter seg.

### Akseptkriterier

- [ ] Klikk på «Last ned PDF» går til `http://localhost:5080/...` (eller den `ApiBaseAddress` som er satt for miljøet), ikke til web-appen.
- [ ] Hovedfanen forblir på Portefølje; nedlastingen åpner i ny fane eller starter direkte i nettleserens nedlastningskø.

---

## Funn 5 — PDF-generering krasjer på SkiaSharp i Linux-containeren (backend)

Hentet `/api/v1/economy/pdf` direkte mot port 5080 (forbi frontend-bugen):

```
HTTP 500
{
  "title": "Internal server error",
  "detail": "The type initializer for 'SkiaSharp.SKImageInfo' threw an exception."
}
```

Klassisk SkiaSharp-i-Docker-feil: SkiaSharp trenger native runtime-biblioteker (libfontconfig, libfreetype, libICU) som ikke er med i `mcr.microsoft.com/dotnet/aspnet:8.0`-base-imaget. Type-initialiseringen feiler fordi `libSkiaSharp.so` ikke finner det den trenger.

### Endring — to alternativer

**Alternativ A (anbefalt) — bytt NuGet-pakken:**

Bytt fra `SkiaSharp.NativeAssets.Linux` til **`SkiaSharp.NativeAssets.Linux.NoDependencies`** i `KraftverkUptime.Api.csproj`. Den bundler de native bibliotekene rett inn, så ingen apt-get-tilpasninger er nødvendige. Mest reproduserbar.

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="2.88.*" />
```

**Alternativ B — installer native deps i Dockerfile:**

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 libfreetype6 \
    && rm -rf /var/lib/apt/lists/*
```

Fungerer, men kobler image-byggingen til debian-pakker og er litt mer skjørt over tid.

### Fonter (vurder også)

Hvis QuestPDF/SkiaSharp bruker Inter-fonten fra spec-en (`EconomyPdfTheme.FontFamily = "Inter"`), må font-fila kopieres inn i imaget eller registreres via `QuestPDF.FontManager.RegisterFont(...)`. Ellers vil PDF-en falle tilbake til systemfont eller feile ved tekst-rendering. Sjekk om Code allerede har gjort dette; hvis ikke: legg `assets/Inter.ttf` i container-imaget og registrer ved app-oppstart.

### Akseptkriterier

- [ ] `GET /api/v1/economy/pdf?plants=all&from=…&to=…&kind=Month` returnerer HTTP 200 med `Content-Type: application/pdf` og en gyldig PDF i body.
- [ ] PDF-en åpner i en standard PDF-leser og viser forside + KPI-sammendrag + per-anlegg + trend (per spec).
- [ ] Container-imaget bygger uten varsler om manglende native libs.
- [ ] Hvis Inter-fonten brukes: PDF-en rendrer den korrekt (ingen «glyph missing»-bokser).

---

## Prioritet og rekkefølge

1. **Funn 1 (Spotomsetning-aggregering)** — ren regnefeil i et tall som vises som hovedtall. Topp prioritet. (NB: Spotomsetning ÅTD ble bekreftet fikset live 2026-05-22 ettermiddag — verifiser at Capture rate, Merverdi, Oppgjør osv. på samme periode-typer også er korrekte.)
2. **Funn 4 (PDF-host)** — én linje, fjerner «Siden ble ikke funnet»-feilen umiddelbart.
3. **Funn 5 (SkiaSharp i Docker)** — uten denne returnerer PDF-endepunktet 500. Bytt NuGet-pakke, rebuild, ferdig.
4. **Funn 3 (429-burst)** — fjerner brukerirritasjon, liten endring.
5. **Funn 2 (SUM-i-kolonner)** — UX-fiks, kan committes samtidig med høyrejustering-jobben fra `FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING.md`.

## Merknad

Funn 4 og 5 må fikses **sammen** — hver for seg fjerner ikke feilen. Funn 4 alene viser fortsatt 500 fra API-et; Funn 5 alene fjerner SkiaSharp-krasjet, men knappen treffer ikke endepunktet. Verifiser ende-til-ende først etter at begge er deployet.
