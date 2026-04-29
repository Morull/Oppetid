# Spec: Vakt-ROI med overløps-justering

**Status:** Klar til implementasjon (2026-04-29)
**Estimat:** 1-2 timer
**Forrige sesjon:** Prioritet 1 i `OVERLEVERING-2026-04-28-SCADA.md` levert. /nedetid og /vakt-roi i drift med Drivdal feb-2025-data.

## Bakgrunn

Vakt-ROI v1 antar at all produksjon som ikke skjedde under nedetid er tapt. Det er feil for regulerte vannkraftverk: hvis det var overløp i magasinet samtidig, ville vannet uansett rent forbi. Da redder ikke vakt-tjenesten noe — vannet er tapt uavhengig av om turbinen kjørte.

Drifts-leder Dalane Kraft påpekte dette 2026-04-29: *"Tar Vakt-ROI hensyn til overløp i perioden? I utgangspunktet taper vi ikke penger om vi har plass i magasinet."*

## Beslutning

**Enkel regel:** Vakt-ROI gjelder **bare** for timer der det var overløp i magasinet. Hvis ingen overløp → vannet er trygt magasinert, kan brukes senere → vakten redder ingenting.

| Scenario under nedetid | Vakt-ROI per time |
|---|---|
| Overløp (magasin fullt, vann renner forbi) | Positiv — vakten ville ha startet turbinen og fanget opp vannet før det rant over |
| Ingen overløp (magasin har plass) | Null — vannet er bare utsatt, ikke tapt |

Dette er konservativt og lett å forsvare for drifts-leder. Senere modeller kan utvides med vannverdi/magasinstand for å fange "kunne kjørt nå til høy spot vs senere lav spot"-scenarioet, men det er utenfor v1.

## Dataflyt

For hver nedetids-event som er innenfor vakt-vinduet og reddbar:

1. Beregn counterfactual-perioden = [event.EndUtc, counterfactualEnd] — det er disse timene vakten "sparte" oss for
2. Hent SCADA-samples for `SignalRole.OverflowFlow` for samme plant i samme periode
3. For hver time i counterfactual-perioden: var overløps-vannføring > terskel (f.eks. 0.001 m³/s for å filtrere støy)?
4. **Reddbare timer = antall timer i counterfactual som HADDE overløp**
5. `reddet_mwh = reddbare_timer × effektMw × kapasitetsfaktor`
6. `reddet_nok = reddet_mwh × snittSpot`

Hvis SCADA-data mangler for hele eller deler av counterfactual-perioden: konservativt anta INGEN overløp → `reddbare_timer = 0` for de manglende delene. Flagg eventet med `OverflowDataMissing = true` for transparens.

Konsekvens av denne tilnærmingen: ROI-tallet blir aldri overestimert. Det er bedre å undertelle redningen enn å love drifts-leder noe som ikke er ekte.

## Endringer i kodebasen

### 1. Ny SignalRole

**Fil:** `src/KraftverkUptime.Core/Domain/SignalMap.cs`

Legg til ny enum-verdi:

```csharp
/// <summary>
/// Overløps-vannføring (m³/s). Verdi > 0 betyr at vann renner forbi turbinen
/// uten å produsere kraft — produksjon som ikke skjer mens denne er aktiv
/// kunne uansett ikke vært utnyttet.
/// </summary>
OverflowFlow,
```

### 2. Oppdater Drivdal-seederen

**Fil:** `src/KraftverkUptime.Infrastructure/Scada/DrivdalSignalMapSeeder.cs` (eller hvor seederen ligger — finn med `Grep "DRIVDAL_INNTAK_NIVA_OVERLOP"`)

Endre rolle for `DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV` fra `Other` til `OverflowFlow`.

Database-migrering:

```sql
UPDATE core.signal_map
SET role = 'OverflowFlow'
WHERE plant_id = 'drivdal' AND signal_id = 'DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV';
```

### 3. Ny query-tjeneste for overløpssjekk

**Fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/IOverflowQueryService.cs`

```csharp
public interface IOverflowQueryService
{
    /// <summary>
    /// Returnerer settet av timer i [fromUtc, toUtc) der overløp ble registrert
    /// (sample-verdi > 0). Returnerer tom HashSet hvis ingen OverflowFlow-tag
    /// finnes for plantet, eller hvis ingen samples er importert i perioden.
    /// </summary>
    Task<IReadOnlySet<DateTimeOffset>> GetOverflowHoursAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>True hvis plantet har en konfigurert OverflowFlow-tag.</summary>
    Task<bool> HasOverflowTagAsync(string plantId, CancellationToken ct);
}
```

Implementasjon i `OverflowQueryService.cs`:
- Slå opp signal_id for SignalRole.OverflowFlow via `ISignalMapRepository`
- Hvis ikke funnet → returner tom HashSet
- Hent samples fra `IScadaSampleRepository` for [from, to)
- Filtrer på `value > 0.001` (terskel mot floating-point støy)
- Returner timene som DateTimeOffset (rundet til hele timer)

### 4. Utvid VaktRoiCalculator

**Fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/VaktRoiCalculator.cs`

Endre signaturen til `Calculate(...)` til å ta `IReadOnlySet<DateTimeOffset> overflowHours` som ekstra parameter (eller injiser `IOverflowQueryService` i ctor — da må Calculate bli async).

Pseudokode for ny logikk per event:

```csharp
ekstra_timer = (counterfactualEnd - event.EndUtc).TotalHours;

// Tell hvor mange av counterfactual-timene som hadde overløp.
// Vakt-ROI gjelder kun for disse — ingen overløp = vann trygt magasinert.
overflowTimerInPeriod = enumerateHours(event.EndUtc, counterfactualEnd)
    .Count(t => overflowHours.Contains(t));

reddbare_timer = overflowTimerInPeriod;
reddet_mwh = reddbare_timer * effektMw * kapasitetsfaktor;
reddet_nok = reddet_mwh * snittSpot;
```

Legg til to nye felt på `VaktRoiResultat`:

```csharp
/// <summary>
/// Antall timer i counterfactual-perioden der det var overløp i magasinet.
/// Det er disse timene vakt-tjenesten faktisk reddet produksjon for —
/// ellers ville vannet rent forbi turbinen.
/// </summary>
public required int OverflowTimerInCounterfactual { get; init; }

/// <summary>True hvis SCADA-data manglet for hele eller deler av counterfactual-perioden.</summary>
public required bool OverflowDataMissing { get; init; }
```

Oppdater `Forklaring`-feltet, eks:
- Med overløp: *"Vakt løste på 1.5 t. Counterfactual = 14.5 t. Av disse hadde 6 t overløp i magasinet → 6 t reddet (≈ 5 100 NOK)."*
- Uten overløp: *"Vakt løste på 1.5 t. Counterfactual = 14.5 t, men ingen overløp i perioden — vannet ville vært magasinert. Ingen ROI."*
- Mangler data: *"Vakt løste på 1.5 t. SCADA mangler overløps-data for counterfactual-perioden — kan ikke beregne ROI."*

### 5. Utvid wire-DTO og UI

**Fil:** `src/KraftverkUptime.Api/Contracts/NedetidContracts.cs`

Legg til de samme to feltene på `VaktRoiEventDto`.

**Fil:** `src/KraftverkUptime.Web/Pages/VaktRoi.razor`

I event-tabellen, legg til ny kolonne "Overløp" som viser:
- "X t" hvis OverflowTimerInCounterfactual > 0
- "Mangler data" hvis OverflowDataMissing
- "–" ellers

Oppdater "Forklaring"-feltet i VaktRoiCalculator slik at det nevner overløp når aktuelt, f.eks.:
*"Vakt løste på 1.5 t. Counterfactual = 14.5 t ekstra, men 6 t hadde overløp → 8.5 t reddet."*

### 6. Tester

**Fil:** `tests/KraftverkUptime.Infrastructure.Tests/Nedetid/VaktRoiCalculatorTests.cs`

Legg til:

- `Trip_Uten_Overlop_Gir_Null_ROI` — tom overflow-set → reddet_nok = 0 fordi vannet er magasinert
- `Trip_Med_Overlop_Halve_Counterfactual_Gir_Halvparten_ROI` — 6 t overløp av 14.5 t → reddet = 6 t × kapasitet × spot
- `Trip_Med_Overlop_Hele_Perioden_Gir_Full_ROI` — overflow i alle timene i counterfactual → reddet_timer = ekstra_timer
- `Mangler_Overlop_Data_Konservativ_Antagelse` — service melder data missing → reddet_nok = 0 og flagg satt

Ny test-fil:

**Fil:** `tests/KraftverkUptime.Infrastructure.Tests/Nedetid/OverflowQueryServiceTests.cs`

Bruk in-memory EF context. Verifiser:
- Returns tom HashSet når plant har ikke OverflowFlow-tag
- Returnerer riktige timer når samples > 0
- Filtrerer ut samples lik 0 og under terskel

## Verifisering

Etter implementasjon, kjør:

```powershell
docker compose up -d --build api
curl.exe "http://localhost:5080/api/v1/plants/drivdal/vakt-roi?from=2025-02-01T00:00:00Z&to=2025-03-01T00:00:00Z" | ConvertFrom-Json | Select-Object totalReddetNok, antallReddbareInnenforVakt
```

For Drivdal feb-2025 forventes **vesentlig lavere** `totalReddetNok` enn de 163 326 NOK fra v1 — sannsynligvis nær null, siden Drivdal kjørte bare 13.9 % av timene (lite vann i magasinet, neppe overløp). Det er det ærlige tallet: i en tørr måned redder vakt-tjenesten lite produksjon fordi vannet uansett er trygt magasinert.

Tallet vil bli mer interessant for vår-/sommermåneder med høy vannføring og hyppigere overløp.

For å manuelt sjekke: spør Postgres etter overløpstimer i feb-2025:

```sql
SELECT COUNT(*) FROM core.sample_facts
WHERE asset_id = 'drivdal'
  AND signal_id = 'DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV'
  AND time_utc >= '2025-02-01' AND time_utc < '2025-03-01'
  AND value > 0.001;
```

## Senere steg (utenfor denne specen)

- Bruk `ReservoirFillFactor > 0.95` som proxy når `OverflowFlow` mangler
- Bruk `UpstreamLevel >= HRV` som tredje fallback
- Vannverdi-modell for "magasin har plass men lav spotpris"-scenario

Disse er ikke del av denne specen.

## Out-of-scope

- Endringer i `/nedetid`-siden (tap_nok der er fortsatt brutto, ikke vakt-relatert)
- UI for å konfigurere HRV per anlegg
- Vannverdi-beregning
