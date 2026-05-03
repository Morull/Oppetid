# Spec: Automatisk import fra overvåket mappe (hot folder)

**Status:** Klar til implementasjon (2026-05-03)
**Estimat:** 2-3 dager
**Avhengighet:** Eksisterende drag-drop-importere for settlement, SCADA, operlog. Bør implementeres etter `SPEC-IMPORT-COMPLETENESS.md` så status fra hot-folder oppdaterer dashboardet automatisk.

## Bakgrunn

Drifts-leder samler nå månedlige import-filer fra fire datakilder (settlement, SCADA, operlog, eventuelt Hydrogrid) for 11 anlegg = 30-50 filer per måned. Drag-drop én og én via webgrensesnittet er tungvint og feil-utsatt.

**Forslag:** En overvåket mappe på lokal disk eller nettverksshare hvor drifts-leder kan slippe alle filer på én gang. Appen oppdager nye filer, identifiserer hvilket anlegg og hvilken kilde de tilhører, ruter til riktig importør, og rapporterer status.

## Mål

1. Drag alle import-filer for måneden inn i én mappe — appen tar resten
2. Filer som lykkes flyttes til `done/` med tidsstempel og plant-navn
3. Filer som feiler havner i `quarantine/` med feillogg
4. Status synlig i UI med live-oppdatering (importer pågår, lyktes, feilet)
5. Integrerer automatisk med `data_imports`-tabellen og oppdaterer completeness-dashboard

## Mappestruktur

```
<HotFolderRoot>/
├── inbox/           ← Slipp filer her
├── processing/      ← Filer under prosessering (kortvarig — sekunder)
├── done/
│   └── 2026-05/     ← Sortert per måned, siden inntak
│       ├── drivdal_settlement_2026-04.xlsx
│       └── lindland_scada_2026-04.csv
├── quarantine/      ← Feilet — krever manuell oppmerksomhet
│   └── 2026-05-03/
│       ├── ukjent-fil.csv
│       └── ukjent-fil.csv.error.txt   (feillogg)
└── _watch.lock      ← Watcher-instans-lås (forhindre dobbel-prosessering)
```

**Default lokasjon (Windows):** `C:\Morten\00 Oppetid\imports\` med fire undermapper. Konfigurerbart i `appsettings.json`.

## Arkitektur

### Komponenter

1. **`HotFolderWatcher`** — `BackgroundService` som lytter på `inbox/` med `FileSystemWatcher`
2. **`FileTypeDetector`** — identifiserer kildetype basert på filnavn + content-sniffing
3. **`PlantDetector`** — identifiserer hvilket anlegg filen tilhører
4. **`ImportRouter`** — sender fil til riktig importør basert på (plant, source-type)
5. **`HotFolderUi`** — Razor-side som viser køen og siste prosesseringer

### Flyt per fil

```
inbox/<fil>
    │
    ▼
1. Vent til fil er ferdig kopiert (fil-stabilitets-sjekk)
    │
    ▼
2. Beregn SHA256-hash av innhold
    │
    ▼
3. Sjekk mot data_imports.file_hash — hvis duplikat: flytt til done/duplicates/
    │
    ▼
4. Detect filtype (settlement.xlsx / scada.csv / operlog.csv / hydrogrid.json)
    │
    ▼
5. Detect anlegg (filnavn-regex først, fallback content-sniff)
    │
    ▼
6. Flytt fil til processing/<unique-name>
    │
    ▼
7. Kall riktig importør (samme kode som drag-drop bruker i dag)
    │
    ▼
8. Hvis lyktes: flytt til done/YYYY-MM/<plant>_<source>_<periode>.<ext>
   Hvis feilet: flytt til quarantine/YYYY-MM-DD/ + skriv .error.txt
    │
    ▼
9. Skriv data_imports-rad (oppdaterer completeness-dashboard automatisk)
```

## Detektor-strategi

### FileTypeDetector

Steg 1 — utvidelses-basert hovedrouting:

| Utvidelse | Kandidat-typer |
|---|---|
| `.xlsx` | settlement (KAIA), eventuelt manuelt eksportert rapport |
| `.csv` | SCADA, operlog, Hydrogrid-export |
| `.json` | Hydrogrid API-respons (når den kommer) |

Steg 2 — content-sniffing for å bekrefte:

```csharp
public sealed record FileTypeMatch(SourceType Type, double Confidence, string Evidence);

public FileTypeMatch Detect(FileInfo file)
{
    // 1. XLSX-filer: sjekk om det er settlement (har "Avregning"-fane eller
    //    tilsvarende sheet-navn) eller noe annet
    if (file.Extension == ".xlsx")
    {
        var sheetNames = OpenXmlHelper.GetSheetNames(file);
        if (sheetNames.Any(s => s.Contains("Avregning", IgnoreCase) 
                            || s.Contains("Time", IgnoreCase)))
            return new FileTypeMatch(Settlement, 0.95, "Excel-fane Avregning/Time");
    }

    // 2. CSV: sniff første linje
    var firstLine = ReadFirstLine(file);

    // SCADA: header begynner med "DateTime" og inneholder "Cluster1."
    if (firstLine.StartsWith("DateTime") && firstLine.Contains("Cluster1."))
        return new FileTypeMatch(Scada, 0.99, "SCADA-eksport-header");

    // Operlog: header har spesifikke kolonner som "Tidsstempel" + "Hendelse"
    if (firstLine.Contains("Tidsstempel") && firstLine.Contains("Hendelse"))
        return new FileTypeMatch(Operlog, 0.95, "Operlog-header");

    return new FileTypeMatch(Unknown, 0, "Ingen kjente mønstre");
}
```

### PlantDetector

Strategi-rekkefølge per fil:

**1. Filnavn-regex (raskest, mest pålitelig når det fungerer):**

```csharp
// Eksempler på navnekonvensjoner som støttes:
private static readonly Regex[] FilenamePatterns = new[]
{
    // "drivdal_settlement_2026-04.xlsx" eller "Drivdal_Avregning_april2026.xlsx"
    new Regex(@"^(?<plant>drivdal|lindland|haukland|honnefoss|liavatnkraft|...)_", IgnoreCase),
    // "scada_drivdal_2026-04.csv"
    new Regex(@"_(?<plant>drivdal|lindland|...)_", IgnoreCase),
    // KAIA-eksport: "Avregning Drivdal April 2026.xlsx"
    new Regex(@"Avregning\s+(?<plant>\w+)\s+", IgnoreCase),
};
```

**2. Content-sniff (fallback for SCADA og operlog der filnavn ikke har plant):**

For SCADA-filer: sniff første kolonne-prefiks. F.eks. `Cluster1.HONNE_LIAVT_...` → Honnefoss.

```csharp
// SCADA: sniff dominerende anleggs-prefiks fra header
var prefixCounts = scadaHeader.Tags
    .Select(tag => Regex.Match(tag, @"Cluster1\.(\w+?)_").Groups[1].Value)
    .GroupBy(prefix => prefix)
    .ToDictionary(g => g.Key, g => g.Count());

var dominantPrefix = prefixCounts.OrderByDescending(kv => kv.Value).First();
// "HONNE" → "honnefoss", "DRIVDAL" → "drivdal", osv. via mapping-tabell
```

**3. Multi-plant-fil:** hvis content-sniff finner > 1 prefiks med signifikant andel (f.eks. begge har ≥ 20 % av tagsene), klassifiser som **multi-plant**. Eksempel: Honnefoss-eksporten med både HONNE_*- og REVSVT/NODLANDVT-tags (sistnevnte tilhører Liavatn-kraftverket).

For multi-plant-filer: splitt logisk under prosessering — hver plant får sine egne tags importert separat. Resultatet logges som to `data_imports`-rader (én per plant), og fila flyttes til `done/<måned>/multiplant_<originalname>` med kommentar.

### Konfigurerbar plant-prefiks-mapping

```json
{
  "PlantPrefixMap": {
    "DRIVDAL": "drivdal",
    "LINDLAND": "lindland",
    "HAUKLAND": "haukland",
    "HONNE": "honnefoss",
    "REVSVT": "liavatnkraft",
    "NODLANDVT": "liavatnkraft",
    "STOKKURHØLEN": "liavatnkraft",
    "LIAVATN": "liavatnkraft",
    "LIAVT": "honnefoss"
  }
}
```

**Merk:** "LIAVT" → honnefoss men "LIAVATN" → liavatnkraft. Dette er navnekollisjonen vi diskuterte. Mappingen er eksplisitt for å unngå tvetydighet.

## Edge-case-håndtering

| Situasjon | Håndtering |
|---|---|
| Fil legges til mens den fortsatt kopieres | Fil-stabilitets-sjekk: vent til filstørrelse + last-modified er uendret i 5 sekunder |
| Veldig stor fil (> 100 MB) | Samme stabilitets-sjekk, men med inkrementell hash-beregning for å unngå minne-blowup |
| Duplikat-fil (samme hash som tidligere import) | Flytt til `done/duplicates/<dato>/` med .info.txt som peker til original-importen |
| Kjent fil-navn men nytt innhold (revisjon) | Ny rad i `data_imports` med ny hash — siste vinner i completeness-view |
| Ukjent filtype | Quarantine + .error.txt med "Ingen kjente filtype-mønstre matchet" |
| Ukjent anlegg (ingen prefiks-treff) | Quarantine + .error.txt med liste over funne prefikser og anlegg-mapping |
| Multi-plant-fil | Logisk splitt under prosessering, lagres en gang fysisk i `done/multiplant/` |
| App restart med filer i `processing/` | Ved oppstart: flytt alle filer i `processing/` tilbake til `inbox/` (vil bli prosessert på nytt — idempotency-sjekk via hash forhindrer dobbel-import) |
| Watcher krasjer | Logges + ny watcher-instans startes automatisk via `BackgroundService` retry-policy |
| To watcher-instanser (f.eks. dev + prod på samme mappe) | `_watch.lock` med PID + timestamp. Andre instans logger advarsel og venter |
| Importør kaster exception under prosessering | Quarantine + .error.txt med full stack trace, fil ikke importert |

## Backend-implementasjon

### Hovedfiler

```
src/KraftverkUptime.Modules.HotFolder/
├── HotFolderModule.cs                    NY
├── HotFolderOptions.cs                   NY
├── HotFolderWatcher.cs                   NY (BackgroundService)
├── FileTypeDetector.cs                   NY
├── PlantDetector.cs                      NY
├── ImportRouter.cs                       NY
└── HotFolderQueue.cs                     NY (in-memory queue av pending filer)
```

### Konfigurasjon

```json
{
  "HotFolder": {
    "Enabled": true,
    "RootPath": "C:\\Morten\\00 Oppetid\\imports",
    "InboxFolderName": "inbox",
    "ProcessingFolderName": "processing",
    "DoneFolderName": "done",
    "QuarantineFolderName": "quarantine",
    "FileStabilityWaitSeconds": 5,
    "MaxConcurrentImports": 2,
    "PlantPrefixMap": { /* ... se over ... */ }
  }
}
```

### Service-registrering

```csharp
// I Program.cs
if (builder.Configuration.GetValue<bool>("HotFolder:Enabled"))
{
    builder.Services.Configure<HotFolderOptions>(builder.Configuration.GetSection("HotFolder"));
    builder.Services.AddSingleton<FileTypeDetector>();
    builder.Services.AddSingleton<PlantDetector>();
    builder.Services.AddSingleton<ImportRouter>();
    builder.Services.AddHostedService<HotFolderWatcher>();
}
```

## API-endepunkter

```
GET  /api/v1/hot-folder/queue              (filer som venter på prosessering)
GET  /api/v1/hot-folder/recent             (siste 50 prosesserte filer)
GET  /api/v1/hot-folder/quarantine         (filer som feilet og krever oppmerksomhet)
POST /api/v1/hot-folder/retry/{filename}   (flytt fil fra quarantine tilbake til inbox)
POST /api/v1/hot-folder/scan-now           (manuelt trigger scan av inbox)
```

## UI

### Hovedside `/hot-folder`

**Fil:** `src/KraftverkUptime.Web/Pages/HotFolder.razor` NY

Layout:

1. **Status-banner:** "Watcher aktiv — overvåker C:\Morten\00 Oppetid\imports\inbox"
2. **Køen** (filer i `inbox/` ikke prosessert ennå)
3. **Pågående prosessering** (i `processing/`, max 2 samtidig)
4. **Sist prosessert** — siste 20 importer med plant, kilde, status, tid
5. **Quarantine** — filer som feilet, med "Prøv på nytt"-knapp og full feilmelding

Live-oppdatering via SignalR eller polling hvert 3. sekund.

Eksempel-tabell for "Sist prosessert":

```
Filnavn                                    | Plant      | Kilde      | Status    | Tidspunkt
---------------------------------------------|------------|------------|-----------|------------
export-117-tags-...20260503-065415.csv     | honnefoss  | scada      | OK        | 13:42 (1.2s)
export-117-tags-...20260503-065415.csv     | liavatnkr. | scada      | OK        | 13:42 (1.2s)
                                            |            |            |           | (multi-plant)
Avregning Drivdal April 2026.xlsx          | drivdal    | settlement | OK        | 13:38 (3.1s)
ukjent-format.csv                          | -          | -          | QUARANTINE| 13:35
```

### Drag-drop fortsatt tilgjengelig

Eksisterende drag-drop på `/plants/{id}`-sider beholdes som fallback for ad-hoc imports. Hot folder er for batch-imports.

## Sikkerhet

- **Hot folder må være lokalt eller på trusted nettverksshare.** Ikke eksponer som åpen FTP eller WebDAV — alle som kan skrive til mappa kan injisere data.
- **Validering før import:** filer fra hot folder behandles like rigorøst som drag-drop. Samme parsing, samme integrity-sjekker.
- **Audit-logg:** hver hot-folder-import logger `user_id = "hotfolder:<machine-name>"` slik at det er sporbart at det var auto-import, ikke manuell.

## Akseptansekriterier

### Funksjonelle

1. `dotnet build` + `dotnet test` grønt — minst 16 nye tester
2. Tjenesten starter automatisk når `HotFolder:Enabled = true`
3. Når en fil dropps i `inbox/`: prosesseres innen 10 sekunder
4. Vellykket import: fil flyttes til `done/YYYY-MM/<plant>_<source>_<originalname>` og rad i `data_imports` skrives
5. Feilet import: fil flyttes til `quarantine/YYYY-MM-DD/` med `.error.txt`
6. Multi-plant SCADA-fil prosesseres logisk for begge anlegg, fysisk lagres én gang
7. Duplikat-fil (samme hash som tidligere) flyttes til `done/duplicates/`, ingen ny `data_imports`-rad
8. `/hot-folder`-siden viser live status

### Datakvalitet

9. Fil under kopiering venter til kopieringen er ferdig (fil-stabilitets-sjekk)
10. App restart: filer i `processing/` flyttes tilbake til `inbox/` ved oppstart
11. Filer som feiler kan retryes manuelt via UI eller via `POST /retry`-endepunkt
12. Det importerte fil-systemet ikke korrumperes ved samtidig drag-drop og hot-folder-import (samme låsing-mekanisme)

### Test-dekning

13. `FileTypeDetectorTests` — 5 tester (settlement, SCADA, operlog, hydrogrid, ukjent)
14. `PlantDetectorTests` — 6 tester (filnavn-match, content-sniff, multi-plant, ukjent prefiks, navnekollisjon LIAVT vs LIAVATN, edge cases)
15. `ImportRouterTests` — 3 tester (router til settlement-importør, til SCADA-importør, multi-plant-splitt)
16. `HotFolderWatcherIntegrationTests` — 4 tester (drop fil → ende-til-ende, fil under kopiering, app restart, watcher restart etter krasj)

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `HotFolderOptions` + mappestruktur-oppsett ved app start | 1 t |
| 2 | `FileTypeDetector` + tester | 2 t |
| 3 | `PlantDetector` + tester (inkl. multi-plant og navnekollisjon) | 3 t |
| 4 | `ImportRouter` + integrasjon med eksisterende importere | 2-3 t |
| 5 | `HotFolderWatcher` BackgroundService + fil-stabilitets-sjekk | 3-4 t |
| 6 | API-endepunkter + integrasjon med data_imports-loggen | 2 t |
| 7 | `/hot-folder`-side med live-oppdatering | 4-5 t |
| 8 | End-to-end-test: drop 5 filer for ulike anlegg, verifiser ende-til-ende | 1 t |

**Estimat totalt:** 2-3 dager.

## Antakelser

1. **Lokal disk er tilgjengelig.** For multi-bruker eller cloud-deployment må mappa være en delt nettverksressurs (Windows share, Azure Files, etc.)
2. **Fil-mønstre er stabile.** Hvis KAIA endrer Excel-format eller SCADA-eksport endrer header må detektorene oppdateres.
3. **Dagens import-pipeline kan kalles programmatisk** — hvis bare via HTTP (drag-drop), må pipelinen refaktoreres til å eksponere en metode/interface som hot-folder-router kan bruke direkte.
4. **Plant_id-mapping er case-insensitive** og dekker alle 11 anlegg + Liavatn-kraftverket.

## Ut-av-scope for v1

- Cloud-basert hot folder (Azure Blob trigger, OneDrive sync) — kan komme i v2
- E-post som drop-zone (forward attachments → import)
- ML-basert filtype-detektering (regex/sniffing er nok for kjente formater)
- Auto-trigging av andre jobber (f.eks. Hydrogrid-sync) etter vellykket settlement-import
- Roll-back av import (hvis noe gikk galt) — i dag må man slette manuelt fra DB
- Versjons-håndtering ved fil-revisjon (v1 logger bare siste versjon)

## Verifikasjon

```powershell
# 1. Sett opp mappestruktur
$root = "C:\Morten\00 Oppetid\imports"
New-Item -ItemType Directory -Force -Path "$root\inbox", "$root\processing", "$root\done", "$root\quarantine"

# 2. Start app, sjekk at watcher er aktiv
curl.exe "http://localhost:5080/api/v1/hot-folder/queue"
# Forventet: tom kø, "watcher: active"

# 3. Drop en fil
Copy-Item "C:\Path\To\drivdal_settlement_2026-04.xlsx" "$root\inbox\"

# 4. Etter 10 sekunder: sjekk at fila er flyttet
Test-Path "$root\inbox\drivdal_settlement_2026-04.xlsx"
# Forventet: False
Test-Path "$root\done\2026-05\drivdal_settlement_2026-04.xlsx"
# Forventet: True

# 5. Sjekk data_imports-logg
psql -d kraftverkuptime -c "SELECT plant_id, source_type, file_name, imported_at_utc 
                            FROM core.data_imports 
                            ORDER BY imported_at_utc DESC LIMIT 5;"

# 6. UI-test
# http://localhost:5180/hot-folder — vis kø + sist prosessert

# 7. Multi-plant-test (Honnefoss-eksport som inneholder Liavatn-data)
Copy-Item "C:\Path\To\export-113-tags-...HONNE.csv" "$root\inbox\"
# Verifiser at to data_imports-rader skrives (en for honnefoss, en for liavatnkraft)
```

## Referanser

- Drifts-leders forespørsel 2026-05-03: "Kan vi lage slik at appen sjekker en mappe etter nye importer automatisk?"
- Avhengighet: `SPEC-IMPORT-COMPLETENESS.md` (data_imports-tabellen brukes som logg)
- Eksisterende drag-drop-importere: `SettlementsEndpoints`, `ScadaEndpoints`, `MultiPlantOperlogEndpoints`
- Multi-plant-eksempel: Honnefoss SCADA-eksport som inneholder Liavatn-kraftverkets dam-tags
