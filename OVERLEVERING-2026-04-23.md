# Overlevering – UI-løft (iterasjon A + B + C)

Dato: 2026-04-23 (kveld)
Utgangspunkt: Steg 1-5 ferdig og verifisert ende-til-ende. Drivdal feb 2025
reproduserer PoC-fasiten bit-for-bit. UI-løftet er nå i gang.

## Hva som er gjort denne sesjonen

### Sanity-fiks før UI-jobb
- Swashbuckle 7.2.0 erstattet med `Microsoft.AspNetCore.OpenApi` (Swashbuckle er
  inkompatibel med .NET 10).
- EF-indeks-filter endret fra `"DeletedAt"` til `"deleted_at"` — `UseSnakeCase`
  slår ikke gjennom i rå SQL.
- Worker-Dockerfile byttet `runtime:10.0` → `aspnet:10.0` (hosted services
  trenger ASP.NET Core-biblioteker).
- `builder.Services.AddKraftverkJobLoop()` lagt i API-prosessen. V1 in-proc
  `ChannelsJobQueue` deler ikke kø på tvers av containere; Worker-containeren
  står idle til distribuert kø kommer i V2.
- `ConfigureHttpJsonOptions` med `JsonStringEnumConverter` så enums
  serialiseres som strenger (UnitState var før 0/1/2 i JSON, ødela Web-DTO).

### Iterasjon A — fundament
- NuGet: MudBlazor 8.4.0 + Blazor-ApexCharts 6.0.0.
- `DalaneTheme.cs` med lys + mørk palett (primær #1E5F8E, sekundær #00A99D,
  tertiær #F39C12).
- `App.razor` med `MudThemeProvider`/`MudPopoverProvider`/`MudDialogProvider`/
  `MudSnackbarProvider` + lys/mørk-toggle.
- `MainLayout.razor` i MudLayout/MudAppBar/MudDrawer-format.
- `NavMenu.razor` som MudNavMenu.
- `app.css` minimalt — `html/body` binder bakgrunn mot
  `var(--mud-palette-background)` slik at tema-toggle dekker hele siden.
- `index.html` inkluderer MudBlazor-assets + Inter-font.

### Iterasjon B — grafer
- `Reports.razor` konvertert til MudTable med MudSelect + MudDatePicker-filtre.
- `ReportDetail.razor` konvertert:
  - 8 KPI-kort med venstre-stripe og semantiske farger.
  - Donut-chart for tilstandsfordeling (state-color-mapped).
  - **3-veis linje-chart**: Plan / Spotbud / Faktisk (Elhub). `SpotbudMwh`-proxy
    lagt til i `ClassifiedHourlyRow` og `ClassifiedHourDto`.
  - Filtrerbar KPI-katalog (MudTable med søk).
  - Virtualisert timetabell inne i MudExpansionPanel.

### Iterasjon C — resterende sider
- `Plants.razor` konvertert til MudCards med badges per plant-type og knapper
  til upload + rapporter.
- `Upload.razor` konvertert: MudFileUpload + MudProgressLinear + query-param
  for forhåndsvalgt plant (`/upload?plant=drivdal`).
- `Index.razor` som dashboard: 4 stats-kort (anlegg, installert MW, klare
  rapporter, behandles) + hurtigvalg-kort.

## Status

Alle filer er skrevet. Ikke build-verifisert i denne sesjonen (sandboxen har
ikke `dotnet`). Forventet å bygge grønt, men MudBlazor-attributt-syntaks
og ApexCharts 6.x-API kan ha subtile endringer som gir kompileringsfeil.

## Neste steg

1. **Rebuild stacken:**
   ```powershell
   cd "C:\Morten\00 Oppetid"
   docker compose down
   docker compose up --build
   ```

2. **Forventede mulige feil og fikser:**
   - `MudFileUpload` `FilesChanged`-signatur i 8.x kan kreve `EventCallback<IBrowserFile>` eksplisitt.
     Fiks: endre til `FilesChanged="@(async (IBrowserFile f) => await OnFilesChanged(f))"`.
   - `AlignItems` på `MudItem` er noe annet enn på `MudGrid`. Hvis analyzer klager,
     fjern attributtet og bruk `Class="d-flex align-center"` i stedet.
   - ApexCharts 6.x kan ha omdøpt `XAxisType.Datetime` til `XAxisType.DateTime`.
     Begge har eksistert historisk.
   - Hvis restore klager på pakke-versjoner (NU1603), bumper du til versjonen
     NuGet foreslår.

3. **Åpne UI:** http://localhost:5180
   - Oversikt (`/`) — dashboard
   - Anlegg (`/plants`) — MudCards
   - Last opp (`/upload`) — MudFileUpload-form
   - Rapporter (`/reports`) — MudTable + filtre
   - Detalj (`/reports/drivdal/{key}`) — KPI-kort + donut + 3-veis + tabeller

## Åpne punkter for senere iterasjoner

1. **Reaktiv chart-tema.** ApexCharts-grafene bytter ikke lys/mørk med
   theme-toggle i dag. Cascade `App.IsDarkMode` til `ReportDetail` og
   sett `ApexChartOptions.Theme.Mode` dynamisk; kall `StateHasChanged`.
2. **Ekte Dalane-farger.** Paletten nå er en plausibel gjetning. Hent faktiske
   HEX fra dalanekraft.no (hover/inspect i nettleser) og oppdater
   `DalaneTheme.Instance.PaletteLight/Dark.Primary/Secondary`.
3. **Auto-refresh på rapport-listen.** I dag må bruker F5-e for å se
   "Behandles" → "Klar". Legg til `System.Threading.Timer` i `Reports.razor`
   som refresh-er hver 5. sekund hvis det finnes pending-rader.
4. **Flere anlegg i seed.** Kun Drivdal er seedet. Legg til 2-3 flere for å
   stress-teste multi-plant-UI.
5. **Steg 6 – Entra ID.** TODO-lapper står i koden. Tre steder:
   - `SettlementsEndpoints.cs` `.AllowAnonymous()` × 4 → `.RequireAuthorization(…)`.
   - `InfrastructureServiceCollectionExtensions.cs` bytte
     `SystemUserContext` → `EntraIdUserContext`.
   - `DbSettlementImportRecorder.ListForPlantAsync/FindByIdempotencyKey`
     må også filtrere på `OwnerOrgId` fra claims.

## Arkitektur-huskeseddel

- Blazor WASM snakker bare med API på `http://localhost:5080`.
  `wwwroot/appsettings.json` holder `ApiBaseAddress`.
- API + Worker er konfigurert i docker-compose. Worker kjører fortsatt men
  er idle (V1 in-proc-kø).
- Rapporter lagres som JSON-blob under `reports/{org}/{plant}/{key}.json` i
  Azurite (dev) eller Azure Blob (prod).
- `IReportsApi` i Web er den eneste HttpClient-wrapperen — all kommunikasjon
  med backend går gjennom den.
- MudBlazor-tema er i `Theming/DalaneTheme.cs`. App.razor toggler
  `IsDarkMode` og alle MudBlazor-komponenter reagerer via CSS-variabler.

## Commit-forslag

```powershell
git add -A
git commit -m "feat(web): UI-løft iterasjon A+B+C (MudBlazor + ApexCharts + Dalane-tema)

Foundation (A):
- MudBlazor 8.4.0 + Blazor-ApexCharts 6.0.0
- DalaneTheme med lys/mørk palett, toggle i AppBar
- MainLayout og NavMenu konvertert til MudLayout
- html/body binder mot MudBlazor CSS-variabler (hel side flipper tema)
- Inter-font, 8px border-radius, Material Icons

Charts (B):
- Reports-liste: MudTable med filter og status-badges
- Report detail: 8 KPI-kort, donut for state counts, 3-veis linjechart
  (Plan / Spotbud / Faktisk), filtrerbar KPI-katalog, virtualisert timetabell
- SpotbudMwh eksponert via proxy på ClassifiedHourlyRow + Web-DTO

Remaining pages (C):
- Plants: MudCards med type-badges og snarveier til upload/rapporter
- Upload: MudFileUpload + MudProgressLinear, støtter ?plant=-query-param
- Index: dashboard med stats-kort og hurtigvalg"
```

## Kommandoer for morgenen

```powershell
# 1. Bekreft git-status (skal være 'clean' etter commit):
git status

# 2. Start stacken:
docker compose up --build

# 3. Seed (hvis du kjørte down -v):
docker compose exec postgres psql -U kraftverk -d kraftverk -c `
  "INSERT INTO core.plants (id, owner_org_id, name, type, installed_capacity_mw, time_zone, created_at) `
   VALUES ('drivdal', 'dev-org', 'Drivdal', 'Regulated', 2.2, 'Europe/Oslo', NOW());"

# 4. Åpne: http://localhost:5180
```
