# Spec: Bulk-import fra mappe

**Dato:** 2026-04-29
**Estimat:** 4-5 t
**Forutsetninger:** `SPEC-MULTIPLANT-OPERLOG.md` implementert (multi-plant operlog-endpoint er en byggekloss).

## Mål

Én operasjon som tar **alle filer i en mappe** og automatisk detekterer + ruter dem til riktig importer:

- KAIA settlement-Excel (.xlsx) → multi-plant settlement
- KraftScada operlog (.csv) → multi-plant operlog
- KraftScada master per anlegg (.csv) → SCADA master per plant

Brukeren legger alle filer i én mappe (f.eks. `C:\Morten\00 Oppetid\CSV Eksporter`) og kjører én UI-handling eller én PowerShell-kommando — systemet finner riktig type, router, og rapporterer hva som ble importert.

## To leveranser

**A. UI-bulk-importside** (`/upload-bulk`)
- Drag-drop flere filer eller hel mappe
- Viser per-fil-status mens prosessering pågår
- Aggregert rapport når alt er ferdig

**B. PowerShell-script** (`tools/import-mappe.ps1`)
- Kan kjøres lokalt fra terminalen
- Loop gjennom filer i en gitt mappe
- Gir tabell-output med filtype + antall events/imports

Begge bruker samme nye API-endepunkt + filtype-detektor.

## Filtype-detektor

Felles logikk i `Modules.Reporting.Bulk.FileTypeDetector` (ny). Tar `Stream` + filnavn, returnerer enum:

```csharp
public enum DetectedFileType
{
    SettlementMultiPlantXlsx,    // KAIA: .xlsx med "Summering"-ark + flere plant-ark
    SettlementSinglePlantXlsx,   // .xlsx med kun ett plant-ark
    OperlogMultiPlantCsv,        // .csv: header timestamp;station;username;tag;...
    ScadaMasterCsv,              // .csv: header Time;value;quality + 22 tags
    Unknown,                     // Skip + log
}
```

**Detekteringsregler:**

| Kjennetegn | Type |
|---|---|
| .xlsx med ark "Summering" + ≥ 1 ark som matcher `^\d+ \w+$` | `SettlementMultiPlantXlsx` |
| .xlsx med ett enkelt plant-ark | `SettlementSinglePlantXlsx` |
| .csv første linje starter med `timestamp;station;username;tag` | `OperlogMultiPlantCsv` |
| .csv første linje starter med `Time;` eller `;DRIVDAL_*_PV;` (SCADA tag-prefix) | `ScadaMasterCsv` |
| Annet | `Unknown` |

**Smart deteksjon for SCADA:** Plant-id detekteres fra første tag-prefiks i header (f.eks. `DRIVDAL_*` → `drivdal`, `LIAVATN_*` → `liavatn`). Hvis ingen match → `Unknown` + log med tag-listen.

## API-endepunkt

```
POST /api/v1/upload/bulk
Content-Type: multipart/form-data
```

Multipart med 1-N filer (feltnavn `files`). Maks 100 MB per request.

**Response:**

```json
{
  "totalFiles": 5,
  "processed": 4,
  "skipped": 1,
  "results": [
    {
      "fileName": "dataeksport_20260429.xlsx",
      "detectedType": "SettlementMultiPlantXlsx",
      "status": "Success",
      "details": "9 plants imported, periode feb-2026"
    },
    {
      "fileName": "operlog-Q1.csv",
      "detectedType": "OperlogMultiPlantCsv",
      "status": "Success",
      "details": "1362 events fordelt på 9 plants"
    },
    {
      "fileName": "drivdal-master.csv",
      "detectedType": "ScadaMasterCsv",
      "status": "Success",
      "details": "drivdal: 22 signals, 14278 samples"
    },
    {
      "fileName": "liavatn-master.csv",
      "detectedType": "ScadaMasterCsv",
      "status": "Success",
      "details": "liavatn: 22 signals, 14278 samples"
    },
    {
      "fileName": "random-readme.txt",
      "detectedType": "Unknown",
      "status": "Skipped",
      "details": "Ukjent filtype — verken xlsx eller forventet CSV-header"
    }
  ]
}
```

## UI-side: `/upload-bulk`

Single drop-sone øverst:
> "Dra alle import-filer hit (Excel + CSV-er) — systemet detekterer og ruter automatisk."

Etter slipp/valg:
1. List alle filer med detected-type i en tabell (før upload)
2. Knapp "Start import"
3. Mens prosessering: progress per fil, status-badge (Pending → Processing → Success/Error)
4. Når ferdig: oppsummering + lenke til `/portefolje` for å se oppdaterte tall

**MudBlazor-komponenter:**
- `MudFileUpload<IBrowserFile[]>` med `Multiple="true"`
- `MudTable` for fil-listen
- `MudProgressLinear` per rad mens prosessering pågår

## PowerShell-script (`tools/import-mappe.ps1`)

```powershell
param(
    [string]$Mappe = "C:\Morten\00 Oppetid\CSV Eksporter",
    [string]$ApiBase = "http://localhost:5080"
)

$ErrorActionPreference = "Stop"

$files = Get-ChildItem -Path $Mappe -File | Where-Object { $_.Extension -in @('.xlsx', '.csv') }

if ($files.Count -eq 0) {
    Write-Host "Ingen .xlsx eller .csv-filer i $Mappe" -ForegroundColor Yellow
    exit 0
}

Write-Host "Fant $($files.Count) filer i $Mappe" -ForegroundColor Cyan

# Bygg multipart-form med alle filer
$form = @{}
foreach ($f in $files) {
    $form["files"] = Get-Item $f.FullName
}

# (Bruker curl.exe siden Invoke-RestMethod sin multipart-håndtering er ujevn på Windows)
$args = @('-X', 'POST', "$ApiBase/api/v1/upload/bulk")
foreach ($f in $files) {
    $args += '-F'
    $args += "files=@$($f.FullName)"
}

$response = & curl.exe @args
$result = $response | ConvertFrom-Json

Write-Host ""
Write-Host "Resultat:" -ForegroundColor Cyan
$result.results | Format-Table fileName, detectedType, status, details -AutoSize

Write-Host ""
Write-Host "Sammendrag: $($result.processed)/$($result.totalFiles) prosessert, $($result.skipped) skippet" -ForegroundColor Green
```

Brukeren kjører:

```powershell
.\tools\import-mappe.ps1
# eller med custom-mappe:
.\tools\import-mappe.ps1 -Mappe "D:\Andre eksporter"
```

## Implementasjons-stegene

### Steg 1 — FileTypeDetector

**Ny fil:** `src/KraftverkUptime.Modules.Reporting/Bulk/FileTypeDetector.cs`

Pure-funksjon. Inn: filnavn + Stream. Ut: `DetectedFileType`. Tester for hver av de 5 typene.

### Steg 2 — BulkImportEndpoint

**Ny fil:** `src/KraftverkUptime.Api/Endpoints/BulkImportEndpoints.cs`

```csharp
group.MapPost("/upload/bulk", BulkUploadAsync)
    .WithName("BulkUpload")
    .DisableAntiforgery()
    .AllowAnonymous();
```

Implementasjon:
1. Les multipart, iterer over fil-seksjoner
2. For hver fil: detekter type
3. Ruter til:
   - `SettlementMultiPlantXlsx` → eksisterende multi-plant settlement-handler
   - `SettlementSinglePlantXlsx` → eksisterende single-plant handler (utled plant fra filnavn eller første ark)
   - `OperlogMultiPlantCsv` → ny multi-plant operlog-handler (fra forrige spec)
   - `ScadaMasterCsv` → eksisterende scada-master-handler (utled plant fra tag-prefiks)
   - `Unknown` → skip, legg til skipped-resultat
4. Returner aggregert respons

### Steg 3 — UI-side

**Ny fil:** `src/KraftverkUptime.Web/Pages/UploadBulk.razor` (rute `/upload-bulk`)

Bruker `MudFileUpload` med `Multiple="true"`. Live progress per fil.

Update `Layout/NavMenu.razor`:

```razor
<MudNavLink Href="upload-bulk" Icon="@Icons.Material.Filled.FolderZip">Bulk-import</MudNavLink>
```

### Steg 4 — PowerShell-script

**Ny fil:** `tools/import-mappe.ps1` — som vist over.

### Steg 5 — Tester

**Ny fil:** `tests/KraftverkUptime.EndToEnd.Tests/BulkImportTests.cs`
- 4 syntetiske filer (én av hver type) → 4 success-resultater
- 1 ukjent fil (.txt) → skipped med fornuftig melding
- Test idempotens: samme fil-set kjørt to ganger → samme resultat (ingen duplikater)
- Test stor batch: 11 SCADA-master-CSV-er + 1 settlement + 1 operlog → alle prosessert

## Verifisering

```powershell
# Etter implementasjon — test med faktisk innhold i CSV Eksporter
.\tools\import-mappe.ps1 -Mappe "C:\Morten\00 Oppetid\CSV Eksporter"

# Forventet output (omtrent):
# fileName                                              detectedType                status   details
# -------                                               ------------                ------   -------
# dataeksport_20260420104859.xlsx                       SettlementMultiPlantXlsx    Success  9 plants imported
# dataeksport_20260429103503.xlsx                       SettlementMultiPlantXlsx    Success  9 plants imported (overskriver)
# export-22-tags-avg-hour-20260428-150138_MASTER.csv    ScadaMasterCsv              Success  drivdal: 22 signals, 14278 samples
# operlog-export-2026-04-29T10-39-24-303Z.csv           OperlogMultiPlantCsv        Success  1362 events fordelt på 9 plants
# operlog-export-2026-04-29T10-27-24-577Z.csv           OperlogMultiPlantCsv        Success  0 events (Grødemfoss-data utenfor periode)

# Sammendrag: 5/5 prosessert, 0 skippet
```

Verifiser i UI: `http://localhost:5180/portefolje` — alle anlegg har data nå.

## Edge cases

1. **Tom mappe** → 0 filer, returnerer "Ingen filer å importere"
2. **Filer som ikke finnes** → 404
3. **Korrupt fil** → status="Error", details="Parse-feil: ..."
4. **For stor request (> 100 MB)** → 413, foreslår å splitte
5. **Plant ikke funnet i system (ScadaMaster med ukjent plant-prefiks)** → status="Error", details="Plant ikke registrert: kjent prefiks DRIVDAL_*/LIAVATN_*/..."
6. **Settlement-fil med 15-min-data** → eksisterende parser-logikk håndterer det allerede (aggregerer til time)

## Out-of-scope

- Auto-watch på en mappe (kan legges til senere som scheduled-task)
- E-post-rapport når bulk-import er ferdig
- Sletting av filer fra mappa etter import
- Historikk over tidligere bulk-imports

## Begrunnelse

Drifts-leder/operatør får én knapp og én mappe å forholde seg til. Mister ikke filer, glemmer ikke å laste opp, og slipper å huske hvilket endepunkt som tar hva. Det reduserer tid pr. månedsskifte fra 10 min til 30 sek + ingen feilbeslutninger.
