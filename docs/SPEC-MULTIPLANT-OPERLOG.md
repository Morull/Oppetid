# Spec: Multi-anleggs operlog-import

**Dato:** 2026-04-29
**Estimat:** 2-3 t
**Spec-grunnlag:** `docs/SPEC-MULTIPLANT-IMPORT.md` (samme mønster, ulik datakilde)

## Mål

La operatøren laste opp **én operlog-CSV-fil** som dekker hele porteføljen, istedenfor 11 separate filer. Parser splitter rader basert på `station`-feltet og ruter hver event til riktig anlegg.

## Bakgrunn

Dagens operlog-import krever én fil per anlegg — endepunkt `POST /api/v1/plants/{plantId}/scada/operlog`. KraftScada støtter eksport av flere stasjoner i én fil (testet med 12 MB Q1-2026-eksport som dekker alle 11 anlegg).

Filen har samme format som dagens parser, men `station`-feltet (kolonne 2) varierer per rad istedenfor å være konstant.

## Filformat (verifisert mot Drivdal-eksport)

```
timestamp;station;username;tag;text;value;operatorType;originTable;alarmType;categoryNumber;offTimestamp
2026-02-04T07:51:02.000Z;Drivdal;Drivdal;DRIVDAL_G1_KONTROLL_STARTER_AL;...
2026-03-15T14:23:11.000Z;Grødemfoss;...;GRODEM_G2_KONTROLL_FEIL_AL;...
2026-01-20T09:00:00.000Z;Honnefoss;...;HONNE_G1_KONTROLL_STOPPER_AL;...
```

## Plant-mapping fra `station`-felt

`station` matcher `Plant.Name` (kanonisk navn med æøå). Mapping til plant_id:

```csharp
// Bruk eksisterende PlantSlug-funksjon
var plantId = PlantSlug.ToSlug(station);
// "Drivdal" → "drivdal"
// "Grødemfoss" → "grodemfoss"
// "Øgreyfoss" → "ogreyfoss"
// "Stølskraft" → "stolskraft"
```

Hvis ingen plant matcher slug → skip rad og logg som datakvalitet-issue.

## Implementasjons-stegene

### Steg 1 — Utvid OperlogCsvParser

**Fil:** `src/KraftverkUptime.Modules.Scada/Import/OperlogCsvParser.cs`

Endre `Parse(...)` til ikke å ta `plantId` som parameter, men returnere events per plantId:

```csharp
public sealed record MultiPlantOperlogParseResult(
    int RowsParsed,
    int RowsSkipped,
    int UnknownStations,
    IReadOnlyDictionary<string, IReadOnlyList<ClassifiedEvent>> EventsByPlantId);

public MultiPlantOperlogParseResult ParseMultiPlant(
    string ownerOrgId,
    TextReader reader,
    Func<string, string?> stationToPlantId)
{
    // ... eksisterende parsing
    // For hver rad: stationToPlantId(row.Station) → plantId
    // Hvis null: rowsSkipped++, unknownStations++
    // Hvis match: legg event i bucket EventsByPlantId[plantId]
}
```

Behold eksisterende `Parse(plantId, ...)` for bakoverkompatibilitet (single-plant-endepunktet).

### Steg 2 — Nytt API-endepunkt

**Fil:** `src/KraftverkUptime.Api/Endpoints/MultiPlantOperlogEndpoints.cs` (ny)

```csharp
group.MapPost("/operlog/multi-plant", UploadMultiPlantOperlogAsync)
    .WithName("UploadMultiPlantOperlog")
    .WithSummary("Tar imot én operlog-CSV med events fra alle anlegg, splitter på station-feltet.")
    .AllowAnonymous()
    .Produces<MultiPlantOperlogResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest);
```

Implementasjon:
1. Les multipart-fil
2. Hent alle plants fra DB → bygg lookup `plantName → plantId`-mapping
3. Kall `parser.ParseMultiPlant(ownerOrgId, reader, station => lookup[station])`
4. For hvert plantId i resultatet: `_repo.UpsertManyAsync(events)`
5. Returner per-plant-statistikk

Response-DTO:

```csharp
public sealed record MultiPlantOperlogResponse(
    int TotalRowsParsed,
    int TotalRowsSkipped,
    int UnknownStations,
    IReadOnlyList<PlantOperlogResult> PerPlant);

public sealed record PlantOperlogResult(
    string PlantId,
    string PlantName,
    int EventsImported);
```

Registrer i `Program.cs`:

```csharp
app.MapMultiPlantOperlogV1(apiV1);
```

### Steg 3 — UI-integrasjon

**Fil:** `src/KraftverkUptime.Web/Pages/Upload.razor` (eller ny side `/upload-operlog`)

Legg til en ny opplastings-seksjon: "Operlog (alle anlegg)". Drag-drop av CSV → kaller nytt endepunkt.

Etter opplasting: vis tabell med per-plant-statistikk:

| Anlegg | Events importert |
|---|---|
| Drivdal | 64 |
| Grødemfoss | 0 |
| Lindland | 23 |
| ... | ... |

Hvis `UnknownStations > 0`: vis advarsel med liste over ukjente stations som ble skippet.

### Steg 4 — Tester

**Ny fil:** `tests/KraftverkUptime.EndToEnd.Tests/MultiPlantOperlogParserTests.cs`

- `ParseMultiPlant_Splitter_Til_Riktig_Plant` — syntetisk CSV med 3 anlegg → 3 buckets
- `ParseMultiPlant_Ukjent_Station_Skippes_Med_Logg`
- `ParseMultiPlant_Bevarer_Kanoniske_Navn` — "Grødemfoss" → grodemfoss
- `ParseMultiPlant_Tom_Fil` — 0 rader, ingen feil
- Integrasjonstest med ekte 12 MB-eksport (i `tests/fixtures/`)

### Steg 5 — Filgrense-konfigurasjon

**Fil:** `src/KraftverkUptime.Api/Options/SettlementUploadOptions.cs` (eller ny `OperlogUploadOptions.cs`)

Heve grensen for operlog-endepunktet til 50 MB (12 MB nå er trygt under, men gi rom for vekst):

```csharp
public sealed class OperlogUploadOptions
{
    public const string SectionName = "OperlogUpload";
    public long MaxUploadBytes { get; init; } = 50 * 1024 * 1024; // 50 MB
}
```

## Verifisering

```powershell
# Last opp Q1-fila (12 MB)
Push-Location "C:\Morten\00 Oppetid\CSV Eksporter"
curl.exe -X POST "http://localhost:5080/api/v1/operlog/multi-plant" `
         -F "file=@operlog-Q1-2026-alle-anlegg.csv;type=text/csv" `
         -H "Content-Type: multipart/form-data"
Pop-Location

# Forventet output (eksempel):
# {
#   "totalRowsParsed": 287,
#   "totalRowsSkipped": 1453,
#   "unknownStations": 0,
#   "perPlant": [
#     { "plantId": "drivdal", "plantName": "Drivdal", "eventsImported": 64 },
#     { "plantId": "grodemfoss", "plantName": "Grødemfoss", "eventsImported": 12 },
#     ...
#   ]
# }

# Sjekk DB
docker compose exec postgres psql -U kraftverk -d kraftverk -c "
SELECT plant_id, COUNT(*) AS antall, MIN(start_utc) AS fra, MAX(start_utc) AS til
FROM core.classified_events
GROUP BY plant_id
ORDER BY plant_id;"
```

Forventet: alle 11 anlegg har events for Q1 2026 (eller minst de som faktisk har hatt events i perioden).

## Kritiske detaljer

**1. Stations-navn-variasjoner.** Hvis KraftScada bruker forskjellige varianter (f.eks. "Drivdal kraftverk" vs "Drivdal"), bygg en alias-tabell:

```csharp
private static readonly Dictionary<string, string> StationAliases = new(StringComparer.OrdinalIgnoreCase)
{
    ["Drivdal kraftverk"] = "drivdal",
    ["Drivdal"] = "drivdal",
    ["Smievatn"] = "grodemfoss", // Smievatn er en del av Grødemfoss-anlegget
    // ... legg til etter behov når data viser hva KraftScada faktisk bruker
};
```

I Grødemfoss-fila vi så tidligere stod "username=Smievatn" men "station=Grødemfoss" — så hovedfeltet `station` ser ut til å være konsistent. Men aliaser kan være nødvendig for noen anlegg.

**2. Idempotens.** `IClassifiedEventRepository.UpsertManyAsync` dedupliserer allerede på `(plant_id, start_utc, state)` — det betyr at samme operlog kan importeres flere ganger uten å lage duplikater. Test dette eksplisitt.

**3. Ytelsen.** 12 MB CSV med ~30k rader bør parses på under 5 sekunder. Bruk streaming (linje-for-linje) istedenfor å lese hele fila i minnet. Eksisterende `OperlogCsvParser` gjør allerede det.

**4. Datakvalitet.** Gi god feedback hvis filen er feil format:
- "Manglende `station`-kolonne" → 400 Bad Request
- Forskjellige tegnsett (UTF-8 vs Windows-1252) → autodetekter med BOM-sjekk

## Migrasjonsstrategi

Etter at multi-plant-endepunktet er klart:
1. Behold eksisterende `POST /plants/{plantId}/scada/operlog` for bakoverkompatibilitet
2. Marker det som "deprecated" i UI med info-tooltip
3. Promoter `POST /operlog/multi-plant` som anbefalt vei

## Out-of-scope

- Auto-trigger av re-klassifisering av berørte perioder etter operlog-import (er allerede dekket av annet system)
- Sletting av operlog per anlegg (egen spec — `SPEC-DATA-RESET.md`)
- Periodisk auto-import fra KraftScada API (krever API-tilgang som ikke finnes ennå)
