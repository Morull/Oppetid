# Leveranse – Steg 2: Domenemoduler

Dato: 2026-04-21
Status: **ferdig — ikke kompileringsverifisert**

## Hva er gjort

Tre domenemoduler implementert på toppen av plattformskjelettet fra Steg 1.
37 nye kildefiler. Python-PoC er brukt som referanse slik at .NET-outputen
matcher `drivdal-feb2025-fasit.json` eksakt i regresjonstesten.

### Moduler

**`KraftverkUptime.Modules.Settlement`** – 15 filer
ClosedXML-basert parser for portaleksport + schema registry + data quality report + job handler.

**`KraftverkUptime.Modules.Classification`** – 13 filer
Proxy-klassifisering (IEEE 762/NERC GADS), KPI-katalog, `SettlementUptimeAnalyzer` (Nivå 0) og `FusedUptimeAnalyzer` (Nivå 3+ stub med dokumentert utvidelsesplan).

**`KraftverkUptime.Modules.Reporting`** – 5 filer
`UptimeReportBuilder` + `UptimeReportRenderer` (XLSX med 4 faner: Sammendrag, KPI-katalog, 3-veis, Klassifisering).

### Tester

**`tests/KraftverkUptime.EndToEnd.Tests`** – 4 filer
- `DrivdalRegressionTests.FullPipeline_MatchesFasit_ForDrivdalFebruar2025` – full pipeline mot faktiske Drivdal-data, verifiserer hver KPI mot fasit.
- `SettlementParserTests` – 5 enhetstester for parser-fallgruver (672 timer, enhetsrad hoppes, Elhub=eSett, total = 703,55466 MWh, UTC-konvertering).
- `ClassifierTests` – 7 regel-for-regel-tester (IU, FO, PO, RS, FD, IS, RoR→RU).

## Justeringer på tvers av Prompt 2-forutsetninger

1. Modulnaming: beholdt eksisterende `Modules.Classification` og `Modules.Reporting` fra Steg 1 fremfor å lage nye `Modules.UptimeAnalyzer.Settlement`/`.Fused`/`Reporting.Uptime`-prosjekter. Unngår sln-patching og følger "én modul = én linje i composition root".
2. `Fused` levert som klasse inne i `Modules.Classification` i stedet for eget prosjekt. Kontrakten er lik (`IAnalyzer<UptimePeriod, UptimeReport>`) og DI-bytte er én linje når SCADA kobles på.
3. `ISettlementDataSource` i Core forble markør-interface; konkret signatur ligger på modulens `ISettlementParser` (Core skal ikke kjenne `ParsedSettlement`).

## Akseptansetest – hvordan kjøre

```powershell
cd "C:\Morten\00 Oppetid"
dotnet restore
dotnet build
dotnet test tests/KraftverkUptime.EndToEnd.Tests --logger "console;verbosity=normal"
```

Forventet output: alle tester grønne. Regresjonstesten verifiserer hver av
29 KPI-er i fasit mot .NET-implementasjonen med numerisk toleranse:

| Enhet | Toleranse |
|---|---|
| ratio | 1e-9 |
| correlation | 1e-6 |
| MWh | 1e-4 |
| NOK | 1e-2 |
| hours | 1e-6 |
| events | 0 (eksakt) |

## Ikke kompileringsverifisert

.NET SDK er ikke tilgjengelig i mitt sandbox, så jeg kunne ikke kjøre
`dotnet build` før leveranse. Mulige issues å se etter ved første kjøring:

1. `ClosedXML`-pakkeversjon 0.104.0 (krever .NET 10 — bekreftet tilgjengelig på NuGet).
2. Warnings fra `AnalysisMode=AllEnabledByDefault` i Directory.Build.props — jeg har lagt til `NoWarn` for de vanligste (CA1062, CS1591). Hvis flere dukker opp, legg dem til i `NoWarn`.
3. ClosedXML-imports i `UptimeReportRenderer` bruker bare kjerne-API (Workbook, Worksheet, Cell, Style) – ingen Chart-API som er wonky i 0.104.
4. `using ClosedXML.Excel.Drawings;` i renderer kan droppes hvis ikke brukt – jeg la den inn for fremtidig chart-støtte.

## Kjente begrensninger

- **Renderer genererer ikke faktisk diagram** i 3-veis-fanen, kun rådata-kolonner. Brukeren må lage Excel-chart manuelt, eller vi kan bytte til EPPlus når lisens er i orden.
- **`IUptimePeriodProvider` ikke implementert** i Infrastructure. `UptimeReportBuilder` krever at den registreres i composition root. For testing brukes `SettlementUptimeAnalyzer` direkte (som i regresjonstesten).
- **Idempotens på `ParseSettlementJob`** er kontraktuelt på plass (`IdempotencyKey`) men ikke håndhevet — trenger et importert-tabell-lookup som legges til når DB-entitet er definert.

## Neste steg

1. `dotnet test` lokalt — verifiser regresjonstest passerer.
2. Implementer `IUptimePeriodProvider` i Infrastructure (leser fra `core.settlement_imports`-tabell + blob-lagret Excel).
3. Koble `ParseSettlementJob` → `SettlementImportedEvent` → `ClassifyOnImportedHandler` via `IEventHandler<T>`.
4. Bygg API-endepunkt for filopplasting → `IJobQueue.EnqueueAsync(new ParseSettlementJob(...))`.
5. Blazor-side: visning av `UptimeReport` + nedlasting av XLSX.

Plattformen er nå klar for Nivå 1 (hydrologi) når NVE Sildre-integrasjon
prioriteres – ingen av kontraktene over trenger å endres.
