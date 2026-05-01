# Spec: Rist-feilkategori — vedlikeholdsbehov vs mekanisk feil

**Dato:** 2026-04-29
**Estimat:** 3-4 t
**Bakgrunn:** Liavatn har 55 turbin-trip-events i Q1 2026. 73 % (40 av 55) skjer innen 60 min etter en rist-falltap-alarm (`*_INNTAK_RIST_FALLTAP_HH_AL`). Disse er ikke "ekte" turbin-feil — de er konsekvenser av tett inntaksrist (løvfall, is, fremmedlegemer). Klassifiseringen bør skille disse for å gi drifts-leder presis statistikk.

## Mål

1. **Ny kategori:** `DowntimeEventCategory.TettInntaksrist` — separat fra `TripFeil`
2. **Auto-deteksjon:** Trip-events med rist-alarm i samme tidsvindu klassifiseres automatisk
3. **Vakt-ROI-konsekvens:** Tett-rist-events teller fortsatt som reddbare, men med annerledes forklaring
4. **Drifts-leder-rapport:** /nedetid-siden viser separat søyle for rist-events i kategori-fordelingen

## Detekt-regelen

**Per trip-event** (`*_TURB_FEIL_AL` eller `*_HAVARI` i operlog):

1. Søk etter `*_INNTAK_RIST_FALLTAP_*_AL` for samme anlegg innen ±60 minutter rundt trip-tidspunktet
2. Hvis match → kategoriser som `TettInntaksrist` med cause-code `operlog:rist-falltap`
3. Ellers → kategoriser som vanlig `TripFeil`

Grensen 60 min er empirisk fra Liavatn-data (73 % match). Konfigurerbart via `Modules.Reporting.Nedetid.RistDetectionOptions` for finjustering.

## Hvorfor egen kategori

| Aspekt | TripFeil (klassisk) | TettInntaksrist |
|---|---|---|
| Årsak | Mekanisk/elektrisk turbin-feil | Vedlikeholdsbehov (løv, is) |
| Rettetid med vakt | 1-2 t (reset/diagnose) | 0.5-1 t (rens av rist) |
| Forebygging | Vedlikeholds-program | Sesong-rens, rist-design |
| Vedlikeholds-prioritet | Høy (kan være alvorlig) | Lav-medium (rutine) |
| Forsikrings-relevant | Ja | Nei |
| KPI-rapport | "Mekanisk tilstand" | "Driftsforhold" |

## Implementasjons-stegene

### Steg 1 — Utvid DowntimeEventCategory

**Fil:** `src/KraftverkUptime.Core/Domain/DowntimeEvent.cs`

```csharp
public enum DowntimeEventCategory
{
    TripFeil,
    TettInntaksrist,    // NY
    EksternForstyrrelse,
    PlanlagtVedlikehold,
    Markedstopp,
    Ressursmangel,
    DataMangel,
    UkjentNedetid,
}
```

### Steg 2 — Korrelert event-aggregering

**Fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/DowntimeEventAggregator.cs`

Endre `MapCategory(state, causeCode)` til å akseptere en valgfri parameter:

```csharp
public static DowntimeEventCategory MapCategory(
    UnitState state,
    string? causeCode,
    bool harRistAlarmIPeriode = false)
{
    if (harRistAlarmIPeriode &&
        (state == UnitState.ForcedOutage || state == UnitState.ForcedDerating))
    {
        return DowntimeEventCategory.TettInntaksrist;
    }

    // ... eksisterende logikk
}
```

I `Aggregate(...)`-metoden, før kategorisering:
1. Samle alle rist-alarmer fra operlog-events for plantet i perioden
2. For hvert downtime-event: sjekk om noen rist-alarm matcher (innen 60 min)
3. Pass flagget til `MapCategory`

### Steg 3 — Rist-deteksjons-tjeneste

**Ny fil:** `src/KraftverkUptime.Modules.Reporting/Nedetid/IRistAlarmDetector.cs`

```csharp
public interface IRistAlarmDetector
{
    /// <summary>
    /// Returnerer settet av tidspunkter der en rist-falltap-alarm var aktiv
    /// for plantet i perioden. Brukes av aggregator til å overstyre
    /// kategoriseringen av trip-events.
    /// </summary>
    Task<IReadOnlySet<DateTimeOffset>> GetRistAlarmTimesAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
```

**Implementasjon:** søk i `IClassifiedEventRepository` etter events der CauseCode/SourcesJson nevner "rist" eller "RIST_FALLTAP" — eller utvid `OperlogCsvParser` til å skille rist-alarmer ut som egen sub-kategori under import.

**Tag-mønster** for deteksjon:
- `*_INNTAK_RIST_FALLTAP_HH_AL` (Høy-høy = kritisk)
- `*_INNTAK_RIST_FALLTAP_H_AL` (Høy = advarsel)
- `*_RIST_*` med `alarmType="alarm"`

### Steg 4 — UI-tilpasninger

**Fil:** `src/KraftverkUptime.Web/Pages/Nedetid.razor`

I `DisplayKategori`:
```csharp
"TettInntaksrist" => "Tett inntaksrist",
```

I `KategoriColor`:
```csharp
"TettInntaksrist" => MudBlazor.Color.Tertiary, // brun/oransje for å skille fra rød trip
```

I `KategoriHex`:
```csharp
"TettInntaksrist" => "#A0522D", // saddle brown
```

I event-tabellen: vis ikon ved siden av "Tett inntaksrist"-events: `Icons.Material.Filled.Park` (løv-ikon) eller `Icons.Material.Filled.AcUnit` (is-ikon).

### Steg 5 — Vakt-ROI-tilpasning

`TettInntaksrist` skal fortsatt være reddbar (vakten kan rense risten manuelt eller restarte). Legg til i `VaktRoiCalculator.ReddbareKategorier`:

```csharp
private static readonly HashSet<DowntimeEventCategory> ReddbareKategorier = new()
{
    DowntimeEventCategory.TripFeil,
    DowntimeEventCategory.TettInntaksrist,    // NY
    DowntimeEventCategory.EksternForstyrrelse,
    DowntimeEventCategory.UkjentNedetid,
};
```

I `Forklaring`-strengen for rist-events:
> "Tett inntaksrist løst av vakt på 0.8 t. Counterfactual = 14.5 t. Reddet 6 t (overløp-perioden)."

### Steg 6 — Tester

**Ny fil:** `tests/KraftverkUptime.Infrastructure.Tests/Nedetid/RistDetectionTests.cs`

- `Trip_Med_Rist_Alarm_Innen_60Min_Klassifiseres_Som_TettInntaksrist`
- `Trip_Uten_Rist_Alarm_Klassifiseres_Som_TripFeil`
- `Trip_Med_Rist_Alarm_75Min_Far_Klassifiseres_Som_TripFeil` (utenfor vinduet)
- `Liavatn_Q1_2026_Verifiserer_40_Av_55_Som_TettInntaksrist`

Bruk syntetiske operlog-events for de fleste, men inkluder en regresjonstest mot ekte Liavatn-data hvis den er tilgjengelig i `tests/fixtures/`.

### Steg 7 — Datakvalitet-flagg

På anlegg-siden eller drift-rapport, vis advarsel hvis:
- > 50 % av trips i perioden er TettInntaksrist
- Anbefal vedlikeholdsplan: "Liavatn har høy frekvens av rist-events i januar — vurder rist-rens-program eller automatisk renser"

## Verifisering

```powershell
# Etter implementasjon, sjekk Liavatn jan-mars 2026:
curl.exe "http://localhost:5080/api/v1/plants/liavatn/nedetid?from=2026-01-01T00:00:00Z&to=2026-04-01T00:00:00Z" `
    | ConvertFrom-Json `
    | Select-Object -ExpandProperty kategoriSummaries
```

Forventet output:
```
Kategori: TettInntaksrist  Antall: ~40  TotalTimer: ...
Kategori: TripFeil         Antall: ~15  TotalTimer: ...
```

(Mot dagens: 55 i `TripFeil` for Liavatn.)

## Datadrevet konfigurasjon (out-of-scope for v1, men hold i mente)

Senere kan tidsvinduet (60 min) tunes per anlegg basert på faktisk korrelasjon. Lagres i `plant_configurations` med nøkkel `RistDetectionWindowMinutes`. Default 60. For Drivdal/Øgreyfoss kan det være kortere (rist sitter nært turbin) — for større magasiner med lang inntakstunnel kan det være lengre.

## Out-of-scope

- Auto-trigger varsling til vakt når rist-alarm passerer terskel (kommer som egen feature)
- Rist-rens-vedlikeholdsplanlegging (manuell vurdering inntil videre)
- Differensiering H vs HH-alarm-typer (begge regnes som rist-event nå)

## Begrunnelse

Drifts-leder ser i dag "Liavatn — 55 trip" og må forklare alle som mekaniske feil. Med ny kategori kan han si: "40 var tett rist (rutine), 15 var ekte feil — vi anbefaler rist-rens-kontrakt for vinteren". Det skiller forsikringsspørsmål fra vedlikeholdsbudsjett, og gir mer presis tilstand-vurdering av anlegget.
