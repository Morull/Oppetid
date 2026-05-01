# Spec: FilterState-deling med rapporter

**Dato:** 2026-04-29
**Estimat:** 30-60 min
**Bakgrunn:** I dag husker `FilterState`-tjenesten valg av anlegg + periode på tvers av `/nedetid` og `/vakt-roi`. Men hvis brukeren åpner en rapport via `/reports/{plantId}/{idempotencyKey}`, så er ikke FilterState oppdatert. Brukeren ønsker at **periode + plant fra åpnet rapport** automatisk fyller inn FilterState slik at de andre sidene er klare.

## Mål

Når brukeren navigerer til `/reports/{plantId}/{idempotencyKey}`:
- FilterState.PlantId ← rapportens plantId
- FilterState.FromDate ← rapportens PeriodStartUtc (lokal-dato)
- FilterState.ToDate ← rapportens PeriodEndUtc + 1 dag (eks. periode-grense)

Etterpå: bruker klikker `/nedetid` eller `/vakt-roi` → samme periode og anlegg er forhåndsutfylt og data lastes automatisk.

## Implementasjons-stegene

### Steg 1 — Inject FilterState i ReportDetail.razor

**Fil:** `src/KraftverkUptime.Web/Pages/ReportDetail.razor`

Legg til på toppen:

```razor
@inject FilterState Filter
```

### Steg 2 — Oppdater FilterState etter rapport er lastet

I `OnParametersSetAsync()` (eller etter `_report = await Api.GetReportAsync(...)`), legg til:

```csharp
if (_report is not null)
{
    // Synkroniser FilterState slik at /nedetid og /vakt-roi får samme kontekst
    Filter.PlantId = _report.PlantId;
    Filter.FromDate = _report.PeriodStartUtc.UtcDateTime.ToLocalTime().Date;
    // PeriodEndUtc er siste time i rapporten (inklusiv) — legg til 1 dag for å gjøre
    // til-datoen til exclusive-grense som /nedetid forventer
    Filter.ToDate = _report.PeriodEndUtc.UtcDateTime.ToLocalTime().Date.AddDays(1);
}
```

`Filter.PlantId/FromDate/ToDate`-setterne håndterer cache-invalidering automatisk — så hvis cachen er for et annet filter, blir den slettet og /nedetid + /vakt-roi henter på nytt.

### Steg 3 — Gjør samme i Nedetid.razor og VaktRoi.razor

Disse oppdaterer allerede FilterState i `OnInitializedAsync` via `Filter.SetIfEmpty(...)` som kun setter når feltene er tomme. Men for konsistens: hvis `_report.PlantId` er satt fra route-parameter (`[Parameter] PlantId`), bør den OVERSTYRE FilterState (ikke kun fylle hvis tom).

Endre i `Nedetid.razor` og `VaktRoi.razor`:

```csharp
protected override async Task OnInitializedAsync()
{
    Filter.OnChange += StateHasChanged;
    try
    {
        _plants = await ReportsApi.ListPlantsAsync();

        if (!string.IsNullOrEmpty(PlantId))
        {
            // Route-parameter: overstyr FilterState hvis route gir eksplisitt plant
            Filter.PlantId = PlantId;
        }
        else if (string.IsNullOrEmpty(Filter.PlantId) && _plants.Count > 0)
        {
            Filter.PlantId = _plants[0].Id;
        }

        // Fyll datoer kun hvis ikke satt fra annen side
        Filter.SetIfEmpty(
            plantId: Filter.PlantId,
            fromDate: DateTime.UtcNow.AddYears(-1).Date,
            toDate: DateTime.UtcNow.Date);

        // ... resten som før
    }
}
```

### Steg 4 — Tester

**Fil:** `tests/KraftverkUptime.Web.Tests/FilterStateSyncTests.cs` (ny — test-prosjekt eksisterer ikke ennå, så bare unit-test for ren state-klasse)

Hvis det ikke er test-prosjekt for Web ennå, hopp over og test manuelt:

1. Åpne `/reports/drivdal/{key}` → bekreft at rapporten viser feb-2025
2. Klikk `/nedetid` i sidemenyen → forventet: feb-2025, drivdal forhåndsvalgt + data lastet
3. Klikk `/vakt-roi` → samme
4. Endre periode på /nedetid til mars-2025 → klikk /reports → forventet: cache-invalidering har skjedd, men /reports-rapportene er ikke automatisk filtrert (det er en separat liste-side, OK)

## Verifisering

Manuelt UI-flyt:

1. http://localhost:5180/reports/drivdal/{key} → ser feb-2025-rapport
2. Klikk **Nedetid** i sidemeny → /nedetid-siden åpnes, viser umiddelbart "8 events, 32 t" for Drivdal feb-2025 (instant fra cache eller fra direkte FilterState-bruk)
3. Klikk **Vakt-ROI** → samme periode og anlegg, ROI-tall vises
4. Endre periode på /nedetid → cache invalideres → trykk Hent → nye tall

## Kritiske detaljer

**Tidssone-håndtering:** `PeriodStartUtc` er UTC. Konverter til lokal tid før `.Date`-slicing for å unngå at en periode i Norge "starter dagen før" pga UTC-shift.

**ToDate er eksklusiv:** Bruker forventer at "feb 2026" betyr 1.-28. feb. Settlement har `PeriodEndUtc = 2026-02-28T22:00:00+00:00` (siste time i den lokale dagen). Vi setter `Filter.ToDate = 2026-03-01` så API-en får riktig eksklusiv slutt.

**Annoteringssystemet:** ReportDetail.razor laster også annoteringer for samme periode. Disse trenger ikke endring — de bruker rapportens periode direkte.

## Out-of-scope

- Synk fra `/upload`-siden eller `/plants`-siden (kan legges til senere ved behov)
- Persistere FilterState i localStorage (overlever ikke browser-restart)
- Auto-navigasjon mellom sider basert på sist brukt
