# Spec: MVP-hardening — sikkerhet, fasit, datakvalitet, plant-type

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 2-3 dager (alle fire deler)
**Bakgrunn:** Statisk kodegjennomgang 2026-04-30 + brukervalidering. Avsluttet med fire konkrete forbedringer som høyner appens pålitelighet uten å vente på SaaS-arkitektur.

## Bakgrunn og scope

En statisk kodegjennomgang foreslo et stort knippe SaaS-arkitektur-endringer. Etter brukervurdering er fire av punktene plukket ut som **reelle og verdt å fikse for ditt drifts-verktøy** (resten er over-engineering eller misforståelser). Specen samler alle fire i én levering siden de er små hver for seg og henger sammen som "MVP-hardening".

| Tiltak | Hva det gir | Estimat |
|---|---|---|
| **A. AllowAnonymous-audit + fiks** | Beskytter API mot at noen kan endre KPI-data, importere falske filer, eller slette annoteringer. Kritisk hvis appen er eksponert utenfor VPN. | 4-6 t |
| **B. Drivdal-regresjonstest reaktivering** | Forhindrer regresjon i KPI-tall ved fremtidige endringer. Den er allerede skrevet og bare disabled med `Skip` — krever fasit-regenerering. | 2-3 t |
| **C. Datakvalitets-widget i UI** | DqState eksisterer i datalaget men er usynlig i UI. Gjør det synlig per anlegg per periode. | 3-4 t |
| **D. PlantType aktiv i klassifikator** | Run-of-river og magasin-anlegg skal ikke behandles likt. PlantType eksisterer men brukes ikke i klassifikator-heuristikken. | 4-6 t |

## Tiltak A: AllowAnonymous-audit + fiks

### A1. Audit hvilke endepunkter er åpne

Før koding: kjør konkret tellingsrapport for å se hva som er ubeskyttet.

```powershell
cd C:\Morten\00 Oppetid
Get-ChildItem -Path src\KraftverkUptime.Api\Endpoints -Filter *.cs |
    ForEach-Object {
        $file = $_.FullName
        $allow = (Select-String -Path $file -Pattern 'AllowAnonymous' -AllMatches).Matches.Count
        $require = (Select-String -Path $file -Pattern 'RequireAuthorization' -AllMatches).Matches.Count
        [PSCustomObject]@{ File = $_.Name; Allow = $allow; Require = $require }
    } | Format-Table -AutoSize
```

Output skal være en tabell. Hvis `Allow > 0` på endpoint-filer som inneholder write-operasjoner (`POST`, `PUT`, `DELETE`, `PATCH`), er det kritisk å fikse.

### A2. Klassifisering av endepunkter

For hvert endpoint, tildel ett av tre nivåer:

| Nivå | Krav | Eksempler |
|---|---|---|
| **PUBLIC** | Ingen autentisering | `/health`, `/health/ready` |
| **AUTHENTICATED** | Innlogget bruker (alle roller) | Alle GET-endepunkter for KPI-er, rapporter, statusvisning |
| **ADMIN** | Innlogget + admin-rolle | POST/PUT/DELETE — import, annoteringer, plant-config, reset, sync-now |

Tabell over alle eksisterende endpoint-filer og foreslått nivå:

| Fil | GET-endepunkter | Write-endepunkter |
|---|---|---|
| `HealthEndpoints.cs` | PUBLIC | – |
| `PlantsEndpoints.cs` | AUTHENTICATED | ADMIN (PUT) |
| `NedetidEndpoints.cs` | AUTHENTICATED | – |
| `EffektivitetEndpoints.cs` | AUTHENTICATED | – |
| `PortfolioEndpoints.cs` | AUTHENTICATED | – |
| `ProduksjonEndpoints.cs` | AUTHENTICATED | – |
| `CaptureRateEndpoints.cs` (når levert) | AUTHENTICATED | – |
| `DamsEndpoints.cs` (når levert) | AUTHENTICATED | ADMIN |
| `AnnotationsEndpoints.cs` | AUTHENTICATED | ADMIN |
| `SettlementsEndpoints.cs` | AUTHENTICATED | ADMIN (POST upload) |
| `MultiPlantSettlementsEndpoints.cs` | AUTHENTICATED | ADMIN (POST upload) |
| `MultiPlantOperlogEndpoints.cs` | AUTHENTICATED | ADMIN (POST upload) |
| `ScadaEndpoints.cs` | AUTHENTICATED | ADMIN (POST upload) |
| `AdminEndpoints.cs` | ADMIN | ADMIN |

### A3. Authentication-strategi

Den enkleste pragmatiske løsningen for én operatør (Dalane Kraft):

**Alternativ 1 (anbefalt for v1): Microsoft Entra ID med to roller**

```csharp
// Program.cs
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
    options.AddPolicy("Admin", p => p
        .RequireAuthenticatedUser()
        .RequireRole("KraftverkUptime.Admin"));
    options.FallbackPolicy = options.GetPolicy("Authenticated");  // default: krever auth
});
```

Roller settes opp i Azure AD-tenanten under app-registrering. Dalane-ansatte legges i `KraftverkUptime.Admin`-gruppen.

**Alternativ 2 (kun hvis Entra ikke er tilgjengelig): API-key med rolle**

```csharp
// Settings: API-keys for service-til-service + en bruker-token-tabell
```

Mer arbeid, mindre standard. Anbefaler ikke med mindre Entra ID er blokkert.

### A4. Implementasjon

For hvert endpoint:

```csharp
// FØR
app.MapGet("/api/v1/plants/{plantId}/nedetid", ...).AllowAnonymous();

// ETTER (read-endepunkt)
app.MapGet("/api/v1/plants/{plantId}/nedetid", ...)
   .RequireAuthorization("Authenticated");

// ETTER (write-endepunkt)
app.MapPost("/api/v1/plants/{plantId}/admin", ...)
   .RequireAuthorization("Admin");
```

Bruk `FallbackPolicy = "Authenticated"` slik at endepunkter uten eksplisitt policy automatisk krever auth — gir defense-in-depth mot at nye endepunkter legges til uten gjennomtanke.

### A5. Audit-logg for write-operasjoner

Eksisterende `IAuditLogger` brukes — utvid alle ADMIN-endepunkter til å logge hvilken bruker som gjorde hva:

```csharp
await auditLogger.LogAsync(new AuditEntry
{
    UserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown",
    Action = "settlement.upload",
    PlantId = plantId,
    Detail = $"file={fileName}, size={fileSize}",
    TimestampUtc = DateTimeOffset.UtcNow
});
```

## Tiltak B: Drivdal-regresjonstest reaktivering

### B1. Status i dag

Fil: `tests/KraftverkUptime.EndToEnd.Tests/DrivdalRegressionTests.cs` — eksisterer men har `[Skip("Fasit må regenereres mot ny KPI-katalog")]`.

Den var fra Phase A før dagens KPI-katalog ble forenklet (CapacityFactor, OutputFactor, EAF fjernet bevisst).

### B2. Strategi

**Regenerer fasit fra siste kjente gode kjøring** (drift-leder har sett feb-2026-tallene og bekreftet at de stemmer). Fasit-tabellen bygges fra:

1. **KPI-tall** for Drivdal feb-2026 fra `UptimeReport`-blob etter siste vellykket kjøring
2. **Nedetids-events** og deres `reddet_nok` etter overløps-justering — verdier fra `OVERLEVERING-2026-04-29-VEIKART.md`: 13 events, 10 696 NOK reddet
3. **Capture rate** og `dagCr` ≈ 1.086 (fra Excel-fasit)
4. **Produksjons-KPI-er** fra Produksjon-modul: PlanTreff 65,8 %, AndelProdIToppKvartil 14,7 %, HydrogridMerverdi −21 697 NOK

### B3. Test-design

```csharp
[Fact(DisplayName = "Drivdal feb-2026 fasit — alle KPI-er innenfor toleranse")]
public async Task Drivdal_Feb2026_RegressionFasit()
{
    // Arrange
    var report = await _reportLoader.LoadAsync("drivdal", new DateRange("2026-02-01", "2026-03-01"));

    // Drift KPI-er
    Assert.Equal(672, report.PeriodHours);  // 28 dager × 24 timer
    Assert.Equal(0.96, report.AvailabilityFactor, precision: 2);  // ±1 %
    Assert.InRange(report.ServiceHours, 580, 620);
    Assert.InRange(report.ForcedOutageHours, 5, 25);

    // Marked
    Assert.InRange(report.BidDelivery, 0.95, 1.0);

    // Nedetid (etter overløps-justering)
    Assert.Equal(13, downtimeEvents.Count);
    Assert.InRange(totalReddetNok, 10000, 11500);  // ±5 %

    // Capture rate
    Assert.Equal(1.086, captureRateResult.DagCr, precision: 2);  // ±0.5 %

    // Produksjon
    Assert.Equal(0.658, produksjonResult.PlanTreffProsent, precision: 2);
    Assert.Equal(0.147, produksjonResult.AndelProdIToppKvartil, precision: 2);
    Assert.InRange(produksjonResult.HydrogridMerverdiNok, -23000, -20000);
}
```

Toleranse-rasjonale: tallene er rundet i overleveringer og kan variere ±2 % uten at det indikerer regresjon. Stramme tester gir falske rød lys; for løse fanger ikke reell drift.

### B4. Fasit-data-strategi

Fasit lagres i ett av to formater:

**Alternativ A: Hardkodet i test-kode** (som over). Enkelt, transparent, krever kode-endring ved ny fasit.

**Alternativ B: Test-data-fil** (`tests/Fasit/drivdal-feb-2026.json`). Versjons-håndteres separat fra koden, kan oppdateres uten omkompilering.

**Anbefaling: Alternativ A for v1.** Hardkodet er enkelere, og fasit endres sjelden — ved ekte endringer i beregnings-logikk skal testen tvinge bevisst review og kommit, ikke en redigering av en JSON-fil.

### B5. Utvidelse til flere måneder/anlegg

Etter at feb-2026 passerer, utvid med:

- Drivdal jan-2026 (sjekk DST-overgang ikke er der, men ny måned med annen produksjons-profil)
- Drivdal helt år 2025 (lengre periode for at årsbaserte KPI-er kan testes)
- Lindland feb-2026 (annet anlegg, samme periode — kontroll mot at logikken er anlegg-uavhengig)

## Tiltak C: Datakvalitets-widget i UI

### C1. Hva DqState er i dag

`SettlementHourlyRow.DqState` er en `DataQualityState`-enum med verdier som `Good`, `Warning`, `Bad`, `Missing`. Settes av `DataQualityReportBuilder` i settlement-importen. Brukes internt i klassifikator, men eksponeres ikke i Web-laget.

### C2. Beslutning

Eksponer DqState som dedikert widget på flere nivåer:

1. **Per anlegg per periode:** "Datakvalitet 98 %" på portefølje-kort
2. **Detaljert breakdown** på anleggs-side: timer-fordeling per state
3. **Filter-knapp på rapporter:** "Skjul timer med dårlig datakvalitet"

### C3. Backend-utvidelse

**Fil:** `src/KraftverkUptime.Modules.Reporting/DataQuality/IDataQualityQueryService.cs` NY

```csharp
public interface IDataQualityQueryService
{
    Task<DataQualitySummary> GetSummaryAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

public sealed record DataQualitySummary(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalHours,
    int GoodHours,
    int WarningHours,
    int BadHours,
    int MissingHours,
    int ManuallyValidatedHours,
    double GoodPct,                    // GoodHours / TotalHours
    IReadOnlyList<DataQualityIssue> TopIssues);  // f.eks. "DST-overgang 2026-03-30 mangler 1 time"

public sealed record DataQualityIssue(
    DateTimeOffset TimeUtc,
    DataQualityState State,
    string Reason);
```

### C4. UI-eksponering

**Fil:** `src/KraftverkUptime.Web/Pages/Portefolje.razor`

Ny kolonne "Datakvalitet" på portefølje-tabellen:

```razor
<MudTd Style="text-align:right">
    <MudTooltip Text="@($"{ctx.GoodHours}/{ctx.TotalHours} timer god kvalitet. {ctx.WarningHours} advarsel, {ctx.BadHours} dårlig.")">
        <span style="@(GetDqColor(ctx.GoodPct))">
            @((ctx.GoodPct * 100).ToString("F1")) %
        </span>
    </MudTooltip>
</MudTd>
```

Fargekoding: ≥99 % grønn, 95-99 % gul, < 95 % rød.

**Fil:** `src/KraftverkUptime.Web/Pages/Anlegg.razor` (eksisterende anleggs-side)

Nytt kort: "Datakvalitet for valgt periode":

```
Total timer: 672
✓ God:        654 (97,3 %)
⚠ Advarsel:    12 (1,8 %)
✕ Dårlig:       6 (0,9 %)
○ Mangler:      0 (0,0 %)

Top-issues:
- 2026-02-15 03:00–04:00: SUMMARY_HOURLY_MISMATCH
- 2026-02-22 14:00:        SpotprisNokMwh = null
```

### C5. Filter-knapp på rapporter

På Nedetid- og Produksjon-sidene: ny toggle "Skjul timer med dårlig datakvalitet". Når aktiv, ekskluderes timer med `DqState >= Bad` fra alle aggregeringer. Tooltip forklarer hvor mange timer som ble ekskludert.

## Tiltak D: PlantType aktiv i klassifikator

### D1. Status i dag

`PlantType`-enum eksisterer i `Core.Domain.PlantType`:
- `RegulatedHydro` (magasin)
- `RunOfRiver`
- `Mixed`
- `PumpedStorage`

Lagret i `core.plants.plant_type`. Men `SettlementClassifier` bruker den ikke — alle anlegg får samme heuristikk.

### D2. Heuristikk-forskjeller per type

| Tilstand | RegulatedHydro | RunOfRiver | Mixed | PumpedStorage |
|---|---|---|---|---|
| 0 produksjon, 0 spotbud | `ReserveShutdown` (markedsoptimering) | `ResourceUnavailable` (sannsynlig vannmangel) | `ReserveShutdown` med Warning | `ReserveShutdown` (drift-valg) |
| Negativ produksjon (energiimport) | `InformationUnavailable` (datafeil) | `InformationUnavailable` (datafeil) | `InformationUnavailable` (datafeil) | `InService` (pumping er normal drift) |
| Lav produksjon (< 10 % installert) under høy spot | `ForcedDerating` | `ResourceUnavailable` (lavt tilsig) | Heuristikk-mix | `ForcedDerating` |
| 0 produksjon, høy spot, magasin > 80 % | Mistanke om `ForcedOutage` (skulle kjørt) | Ikke relevant | Mistanke om `ForcedOutage` | Mistanke om `ForcedOutage` |

### D3. Implementasjon

**Fil:** `src/KraftverkUptime.Modules.Classification/Classification/SettlementClassifier.cs`

Utvid metoden `Classify(SettlementHourlyRow row, PlantClassificationConfig config)` til å forgrene på `config.PlantType`:

```csharp
public ClassifiedHourlyRow Classify(SettlementHourlyRow row, PlantClassificationConfig config)
{
    // Eksisterende kvalitetssjekk
    if (row.DqState != DataQualityState.Good) return Classified(row, UnitState.InformationUnavailable);

    // Ny forgrening per type
    return config.PlantType switch
    {
        PlantType.RunOfRiver => ClassifyRunOfRiver(row, config),
        PlantType.PumpedStorage => ClassifyPumpedStorage(row, config),
        PlantType.RegulatedHydro or PlantType.Mixed or _ => ClassifyRegulated(row, config)
    };
}

private ClassifiedHourlyRow ClassifyRunOfRiver(SettlementHourlyRow row, PlantClassificationConfig config)
{
    // 0 produksjon + 0 bud → trolig vannmangel for run-of-river,
    // ikke "ReserveShutdown" som for regulert.
    if (IsZero(row.MwhElhub) && IsZero(row.SpotbudMwh))
    {
        return Classified(row, UnitState.ResourceUnavailable, 
            "Run-of-river uten produksjon og bud — sannsynlig lavt tilsig");
    }
    
    // Resten følger felles logikk
    return ClassifyRegulated(row, config);
}

private ClassifiedHourlyRow ClassifyPumpedStorage(SettlementHourlyRow row, PlantClassificationConfig config)
{
    // Negativ Elhub er IKKE datafeil for pumpekraft — det er pumping
    if (row.MwhElhub.HasValue && row.MwhElhub.Value < 0)
    {
        return Classified(row, UnitState.InService, 
            "Pumping (negativ MWh) — normal drift for pumpekraft");
    }
    return ClassifyRegulated(row, config);
}
```

### D4. Konfig per anlegg

`PlantClassificationConfig` har allerede `PlantType`. Ingen ny migrering nødvendig — bare backfill manglende verdier:

```sql
-- Sjekk og backfill
UPDATE core.plants SET plant_type = 'RegulatedHydro' WHERE plant_type IS NULL AND plant_id IN ('drivdal', 'lindland');
UPDATE core.plants SET plant_type = 'RunOfRiver' WHERE plant_type IS NULL AND plant_id IN ('vikesa', 'stolskraft', 'grodemfoss');
-- Resten — vurder per anlegg, default RegulatedHydro hvis usikker
```

**Brukerinput kreves:** bekreft plant-type per anlegg. Forslag basert på samtale-kontekst:

| Anlegg | Forslått type | Bekreft |
|---|---|---|
| Drivdal | RegulatedHydro | ✓/✗ |
| Lindland | RegulatedHydro | ✓/✗ |
| Haukland | RegulatedHydro (kaskade) | ✓/✗ |
| Honnefoss | RegulatedHydro (kaskade) | ✓/✗ |
| Liavatn | RegulatedHydro (kaskade) | ✓/✗ |
| Øgreyfoss | RegulatedHydro (kaskade) | ✓/✗ |
| Logjen | RegulatedHydro (lite magasin) | ✓/✗ |
| Grødemfoss | RunOfRiver eller Mixed | ✓/✗ |
| Ørsdalen | RegulatedHydro | ✓/✗ |
| Vikeså | RunOfRiver | ✓/✗ |
| Stølskraft | RunOfRiver | ✓/✗ |

### D5. Tester

`SettlementClassifierTests` utvides med per-type-scenarier:

```csharp
[Fact]
public void RunOfRiver_NoProductionNoBid_ClassifiesAsResourceUnavailable() { }

[Fact]
public void RegulatedHydro_NoProductionNoBid_ClassifiesAsReserveShutdown() { }

[Fact]
public void PumpedStorage_NegativeElhub_ClassifiesAsInService() { }

[Fact]
public void RegulatedHydro_NegativeElhub_ClassifiesAsInformationUnavailable() { }
```

Minst 8 nye tester totalt — to per type × to scenarier.

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 12 nye tester passerer
2. **Tiltak A:** Audit-rapport viser at ingen write-endepunkter er `AllowAnonymous`. Health-endepunkter forblir åpne. Rev-tester verifiserer at `POST /api/v1/plants/.../admin` returnerer 401 uten token.
3. **Tiltak B:** `DrivdalRegressionTests` har ingen `Skip`, kjører grønt for feb-2026-fasit. Hvis et KPI-tall avviker > 5 % fra fasit, faller testen.
4. **Tiltak C:** Portefølje-tabell viser datakvalitet-kolonne. Anleggs-side viser breakdown. Filter-knapp på Nedetid og Produksjon ekskluderer dårlige timer når aktivert.
5. **Tiltak D:** Vikeså og andre run-of-river-anlegg får `ResourceUnavailable` på 0/0-timer, ikke `ReserveShutdown`. Verifiseres via klassifikator-test og smoke-test mot live data.

### Test-dekning

6. Tiltak A: 4 nye tester (auth-policy fungerer)
7. Tiltak B: 1 ny test (regresjons-fasit), reaktivert eksisterende test
8. Tiltak C: 5 nye tester (DataQualityQueryService) + 2 UI-tester (widget rendrer)
9. Tiltak D: 8 nye klassifikator-tester (per-type-heuristikk)

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat | Stoppunkt |
|---|---|---|---|
| 1 | **A1-A2:** Audit-rapport for AllowAnonymous + endpoint-klassifisering | 1 t | **Stopp og gi bruker rapport — bekreft hvilke endepunkter er admin-only** |
| 2 | **A3:** Entra ID-oppsett (krever Azure-kontoadgang) | 2-3 t | – |
| 3 | **A4-A5:** Endre AllowAnonymous til RequireAuthorization + audit-logg på write-endepunkter | 2 t | – |
| 4 | **B:** Reaktiver Drivdal-regresjonstest med fasit + utvid med flere måneder | 2-3 t | – |
| 5 | **C:** DataQualityQueryService + portefølje-kolonne + anleggs-kort | 3-4 t | – |
| 6 | **D:** PlantType-forgrening i klassifikator + tester + plant-type-backfill | 4-6 t | **Stopp og rapporter klassifikator-endring per anlegg — drifts-leder skal bekrefte at run-of-river-anleggene faktisk klassifiseres riktig** |

**Estimat totalt:** 2-3 dager.

## Antakelser

1. **Entra ID er tilgjengelig** for Dalane Kraft — hvis ikke, må alternativ 2 (API-key) brukes.
2. **Eksisterende `IAuditLogger`** fungerer og skriver til `core.audit_log`. Hvis ikke implementert: tiltak A5 tilføyes spec.
3. **DqState fylles inn pålitelig** av `DataQualityReportBuilder` i settlement-importen. Hvis det er hull, må builderen forbedres parallelt.
4. **PlantType-konfigurering kan oppdateres via PlantAdmin-UI** (bygd i steg 1 av veikartet). Hvis ikke: legg til drop-down-kontroll på samme side.

## Ut-av-scope for v1

- Distribuert jobbkø (over-engineering for én operatør)
- Hierarki Eier → Vassdrag → Aggregat (over-engineering)
- Sub-hour event-håndtering (settlement er per definisjon timesvis)
- Tre separate tilgjengelighets-KPI-er (én er nok når den dokumenteres godt)
- Multi-tenant — ingen plan om SaaS-utvidelse

## Verifikasjon

```powershell
# 1. Audit AllowAnonymous etter endring
Get-ChildItem -Path src\KraftverkUptime.Api\Endpoints -Filter *.cs |
    ForEach-Object {
        $allow = (Select-String -Path $_.FullName -Pattern 'AllowAnonymous').Matches.Count
        if ($allow -gt 0 -and $_.Name -ne 'HealthEndpoints.cs') {
            Write-Warning "$($_.Name) har $allow AllowAnonymous-treff — sjekk manuelt"
        }
    }
# Forventet: ingen warnings

# 2. Kjør Drivdal-regresjonstest
dotnet test tests\KraftverkUptime.EndToEnd.Tests --filter "FullyQualifiedName~DrivdalRegression"
# Forventet: passerer

# 3. Datakvalitet-API
curl.exe "http://localhost:5080/api/v1/plants/drivdal/data-quality?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json
# Forventet: { "totalHours": 672, "goodPct": ~0.98, ... }

# 4. PlantType-klassifikator-effekt
# Test for run-of-river: importer en periode med 0/0-timer for vikesa
curl.exe "http://localhost:5080/api/v1/plants/vikesa/nedetid?from=2026-02-01&to=2026-03-01" | ConvertFrom-Json
# Forventet: 0/0-timer klassifisert som ResourceUnavailable, ikke ReserveShutdown

# 5. UI-test
# http://localhost:5180/portefolje  → datakvalitet-kolonne synlig
# http://localhost:5180/anlegg/drivdal  → datakvalitet-kort synlig
# http://localhost:5180/  → må logge inn (hvis Entra aktivert)
```

## Referanser

- Statisk kodegjennomgang 2026-04-30 (Cowork-samtale)
- `src/KraftverkUptime.Modules.Classification/Kpi/UptimeKpiCalculator.cs` linje 21-32 — KPI-fjerning som er bevisst
- `tests/KraftverkUptime.EndToEnd.Tests/DrivdalRegressionTests.cs` — eksisterende skipped test
- `OVERLEVERING-2026-04-29-VEIKART.md` — Drivdal feb-2026-tall som fasit
