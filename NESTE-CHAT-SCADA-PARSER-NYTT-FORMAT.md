# NESTE CHAT (Code) — SCADA-parser må støtte nytt eksportformat

**Dato:** 2026-06-01
**Problem:** Nyere SCADA-eksport (eks. `export-44-tags-avg-hour-20260601-090232_MASTER.csv`, hele mai, 11 anlegg) gir **HTTP 400 «Ugyldig CSV»** ved import → `InvalidDataException` fra `ScadaMasterCsvParser`. Stack: `HotFolderWatcher.RouteAndImportAsync` → `EnsureSuccessStatusCode`.

## Rotårsak — to formatendringer i ny eksport

| | Gammel (importerte) | Ny (400) |
|---|---|---|
| Header kol. 0 | `DateTime` | `DateTime (UTC)` |
| Tidsstempel | `2025-12-31 23:00:00.000` (lokal anleggstid, mellomrom) | `2026-05-01T00:00:00.000Z` (ISO-8601 UTC, `T` + `Z`) |

(BOM finnes i begge — `StreamReader` fjerner den, så den er ikke problemet.)

Parseren krever **eksakt** «DateTime» i kol. 0 og parser kun det gamle lokaltids-formatet. Begge må gjøres tolerante.

## Filer

`src/KraftverkUptime.Modules.Scada/Import/ScadaMasterCsvParser.cs`
- **Enkelt-plant `Parse`** — header-sjekk linje **45**.
- **Multi-plant `ParseMultiPlant`** — header-sjekk linje **146**.
- **`TryParseTimestamp`** (privat, delt av begge) — linje **258**.

> Sjekk også at fine-importen (`IScadaImportService.ImportMasterCsvMultiPlantFineAsync`) går via samme parser/`TryParseTimestamp`. Hvis den har egen header-sjekk, fiks den tilsvarende. Grep: `Equals("DateTime"`.

## Endring 1 — tolerant header (linje 45 og 146)

```csharp
// FØR:
if (headerCols.Length < 3 || !headerCols[0].Trim().Equals("DateTime", StringComparison.OrdinalIgnoreCase))
// ETTER:
if (headerCols.Length < 3 || !headerCols[0].Trim().StartsWith("DateTime", StringComparison.OrdinalIgnoreCase))
```

`StartsWith` godtar `DateTime`, `DateTime (UTC)`, `DateTime (Local)` osv. Behold `headerCols.Length < 3`-guarden.

## Endring 2 — `TryParseTimestamp` håndterer ISO-8601 med sone

Legg til FØRST i metoden (før dagens `TryParseExact`): hvis strengen bærer eksplisitt sone (`Z` eller `+hh:mm`/`-hh:mm` etter et `T`), er den allerede zonet — parse direkte og **ikke** tidssone-konverter.

```csharp
var s = raw.Trim();

// Nytt eksportformat: ISO-8601 med eksplisitt sone (…T…Z eller …±hh:mm).
// Allerede UTC/zonet → ingen plant-tz-konvertering.
if (s.Contains('T') &&
    (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || HasExplicitOffset(s)))
{
    if (DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto))
    {
        utc = dto.ToUniversalTime();
        return true;
    }
    return false;
}

// …eksisterende TryParseExact (lokal anleggstid → UTC) uendret under …
```

Hjelpemetode (offset etter `T`, ikke forveksle med dato-bindestreker):
```csharp
private static bool HasExplicitOffset(string s)
{
    var t = s.IndexOf('T');
    if (t < 0) return false;
    var tail = s.AsSpan(t);
    return tail.Contains('+') || tail.LastIndexOf('-') > 0; // '-' i tids-delen = offset
}
```

> Behold den gamle lokaltids-grenen for filer uten sone. Ikke bruk `DateTimeOffset.TryParse` på sone-løse strenger (den antar maskinens lokaltid).

## Tester (`tests/KraftverkUptime.Infrastructure.Tests/Scada/ScadaMasterCsvParserMultiPlantTests.cs`)

- **Ny header godtas:** header `DateTime (UTC);Value (Cluster1.ORSDAL_G1_GEN_P_PV);Unit (…)` → ingen `InvalidDataException`.
- **ISO-Z-tidsstempel:** rad `2026-05-01T00:00:00.000Z;…` → sample med `TimestampUtc == 2026-05-01T00:00:00Z` (ingen ±2t forskyvning).
- **Gammelt format uendret:** `DateTime` + `2025-12-31 23:00:00.000` (lokal) → fortsatt korrekt UTC-konvertering (regresjon).
- **Blandet desimal-komma** (`2874,4444`) parses som før (allerede støttet).

## Akseptkriterier

- [ ] `export-44-tags-avg-hour-20260601-…` importeres uten 400.
- [ ] Mai scada-trend dukker opp i `data-status/matrix` (var 0 celler) med korrekte UTC-tidsstempler.
- [ ] Gamle eksportfiler importerer fortsatt likt (ingen regresjon).
- [ ] Fine-varianten (15-min) godtar samme nye header/tidsformat.

## Etterpå

Rebuild, så drop fila i `CSV Eksporter/`-roten (eller la Fiks 5 / manuell `/scada/multi-plant` ta den). Verifiser mai trend i matrisen.

## Sammenheng

Dette er den *egentlige* årsaken til at fila aldri kom inn — ikke dedup. Med gammel kode ga 400-en quarantine på første kopi, hash ble registrert (register-før-import), og resten havnet i `duplicates/`. Fiks 5 (register-etter-import) + denne parser-fiksen løser begge lag.
