# Prompt 2 – Domenemoduler for oppetidsanalyse av vannkraftverk

**Bruk:** Dette er andre av to prompter. Forutsetter at plattformskjelettet fra Prompt 1 er levert og kjørende. Denne fyller inn hydrokraft-domenelogikken.

---

## Systemprompt (rolle)

Du er en senior dataingeniør og fagekspert med spisskompetanse innen:
- Norsk vannkraftbransje: magasin-, elve-, pumpe- og småkraftverk, tilgjengelighets- og ytelsesanalyse.
- IEEE 762 (Reliability, Availability, Productivity), NERC GADS-metodikk, og IEC 61400-26 (metodikk for Information vs. Technical Availability).
- Norsk kraftmarkedstrukturen: Elhub, eSett, Nord Pool, regulerkraft, ubalanse­oppgjør, NVE-rapportering.
- Data engineering i .NET / ASP.NET Core med pandas-lignende tidsserie­operasjoner.
- Datakvalitetsvalidering, skjema­drift, hull-deteksjon i tidsserier.

Du svarer alltid med konkret, kjørbar kode, begrunnede metodiske valg og eksplisitt flagg av fallgruver.

---

## Kontekst fra Prompt 1

Plattformskjelettet er allerede bygget. Du skal bare fylle inn domenemoduler innenfor den eksisterende arkitekturen. Du skal **ikke**:
- Endre kontrakter i `KraftverkUptime.Core`.
- Endre `Infrastructure`, `Api`, `Web` eller `Worker` unntatt én linje i composition root for modulregistrering.
- Innføre nye tverrgående abstraksjoner uten å begrunne hvorfor.

Du **skal**:
- Implementere konkrete moduler som følger `IDataSource`, `IAnalyzer`, `IReportBuilder`-kontraktene.
- Bruke `AssetEvent`, `ClassifiedPeriod`, `DataQualityState`, `UnitState`, `PlantType` fra Core.
- Bruke retrofit-sømmene (`IJobQueue`, `IFileStorage`, `IPlantConfiguration`, `INotificationService`, `IEventPublisher`).

---

## Domenekontekst

### Første kunde og anlegg
- **Kunde:** Dalane-Kraft.
- **Første anlegg:** Drivdal magasinkraftverk (~2,2 MW, regulert).
- **Plant type:** `Regulated` i v1, men løsningen skal kunne støtte `RunOfRiver` og `Mixed` uten refaktorering.

### Konsekvenser av plant type for klassifisering
Klassifiseringsreglene i `UptimeAnalyzer.Settlement` er **betinget på `PlantType`**:
- *Regulert:* null-produksjon er i utgangspunktet et valg (planlagt, markedsstyrt, eller feil).
- *Elvekraft:* null-produksjon er i utgangspunktet konsekvens av hydrologi (ressurs­begrenset) med mindre annet kan dokumenteres.

Samme heuristikk på tvers av anleggstyper vil gi systematisk feil klassifisering.

---

## Modenhetsstige for klassifiseringsnøyaktighet

Appens oppetidsanalyse bygges trinnvis. Denne prompten leverer **Nivå 0**. Kontrakter og data­modell må likevel støtte alle nivåer uten refaktorering.

| Nivå | Kilder | Forventet nøyaktighet | Leveres når |
|---|---|---|---|
| **0 (denne leveransen)** | Settlement (Elhub/eSett-eksport via portal) | 60–70 % klassifisering | Nå |
| 1 | + Hydrologi (NVE Sildre) | 75–85 % | Senere sprint |
| 2 | + Markedslogikk (Nord Pool + marginalkostnad) | 80–90 % | Senere |
| 3 | + SCADA (OPC UA / historian) | ~95 % | Krever eieravtale |
| 4 | + CMMS (arbeidsordre) | ~98 % | Integrasjon med IFS/Maximo |
| 5 | + Prediktiv (vær, vibrasjon, ML) | Prognose-nivå | Lengre sikt |

Alle KPI-er rapporteres med eksplisitt `confidence`-verdi slik at brukeren ser hvilket nivå tallene stammer fra.

---

## Første datasett (Drivdal settlement)

Månedlig oppgjørseksport fra portalen inneholder:

**Fane `Summering`** – aggregat for perioden.
Kolonner: `Tidsserie, MWh-Elhub, MWh-eSett, Spotbud, Spotomsetning, Ubalanse, RK-kjop, RK-salg, Nord Pool gebyr, eSett volumgebyr, eSett ubalansegebyr, Sum salg, Meglerprovisjon, Oppgjor`.

**Fane `<verk>`** (f.eks. `1 Drivdal`) – timeoppløsning.
- Rad 0: kolonnenavn.
- Rad 1: enheter (MWh / NOK / NOK/MWh) – **skal ignoreres i parsing**.
- Rad 2+: data.
- Kolonne 17 er tom spacer – ignoreres.

Kolonner: `Time, MWh-Elhub, MWh-eSett, Spotbud, Spotpris, Spotomsetning, Ubalanse, RK-pris, RK-kjop, RK-salg, Nord Pool gebyr, eSett volumgebyr, eSett ubalansegebyr, Sum salg, Meglerprovisjon, Oppgjor, <tom>, Brutto omsetning, Produksjonplan, Effektavlesninger, Absolutt ubalansevolum, Tap/gevinst ubalanse eks. gebyr`.

### Parsing­krav
- `Time` parses med `dayfirst=true` og lagres som UTC (kilden er Europe/Oslo).
- DST-overganger: mars-døgnet har 23 timer, oktober-døgnet har 25 timer. Håndter eksplisitt.
- Valider `MWh-Elhub == MWh-eSett`. Avvik → datakvalitets­flagg.
- Valider `Summering`-sum mot sum av time­rader (sanity check).

---

## 3-veis produksjonsplan-sammenligning

Portaleksporten brukes fremfor direkte API-er fordi den inneholder `Produksjonplan`, `Spotbud` og levert `MWh-Elhub` i samme fil. Dette gjør 3-veis avviksanalyse mulig og skal være **førstegrads-funksjonalitet**.

| Sammenligning | KPI-navn | Formel |
|---|---|---|
| Plan vs. levert | `PlanFulfillment` | Σ Elhub / Σ Plan |
| Bud vs. levert | `BidAccuracy` | Σ Elhub / Σ Spotbud |
| Plan vs. bud | `PlanToBidDeviation` | (Plan − Spotbud) / Plan |
| Kobling til ubalanse | `ImbalanceCausedByDeviation` | Korrelasjon mellom abs(Spotbud − Elhub) og Ubalanse |

Rapporten skal ha time-for-time figur med alle tre serier + markering av avviksterskler (terskel per `plantId` i `IPlantConfiguration`).

---

## Tilstandsklassifisering (IEEE 762 / NERC GADS)

Hver time per asset tilordnes nøyaktig én `UnitState`:

| Kode | Navn | Teller mot | Notater for hydro |
|---|---|---|---|
| `InService` | IS | Service Hours (SH) | Normal produksjon |
| `ReserveShutdown` | RS | Available Hours (AH) | Tilgjengelig, markedsstyrt stopp |
| `PlannedOutage` | PO | Unavailable Hours | Planlagt revisjon (> 4 uker varsel) |
| `MaintenanceOutage` | MO | Unavailable Hours | Vedlikehold (< 4 uker) |
| `ForcedOutage` | FO | Unavailable Hours | Trip / uvarslet stopp |
| `ForcedDerating` | D1–D4 | Partial Unavailable | Redusert pga. feil |
| `PlannedDerating` | PD | Partial Unavailable | Redusert pga. vedlikehold |
| `ResourceUnavailable` | RU | OMC | Vannmangel, islegging, minstevassføring |
| `InformationUnavailable` | IU | Egen kategori | Manglende data ≠ nedetid |

### "Outside Management Control" (OMC)
Hendelser utenfor operatørens kontroll (RU, nettpålegg) rapporteres i **to parallelle sett**:
- **Unit Performance Indices** – ekskluderer OMC (driftsorganisasjonens prestasjon).
- **System Reliability Indices** – inkluderer OMC (systemets tilgang til energien).

---

## KPI-katalog (implementeres som rene funksjoner med enhetstester)

### Tidsbaserte
```
PeriodHours (PH)              = timer i perioden
AvailableHours (AH)           = SH + RS
UnavailableHours (UH)         = PO + MO + FO
ServiceHours (SH)             = timer i InService
AvailabilityFactor (AF)       = AH / PH
EquivalentAvailabilityFactor  = (AH − ekvivalente deratingtimer) / PH
ServiceFactor (SF)            = SH / PH
ForcedOutageRate (FOR)        = FOH / (FOH + SH)
EquivalentForcedOutageRate    = (FOH + EFDH) / (FOH + SH + EFDHRS)
```

### Energibaserte
```
CapacityFactor (CF)           = Σ Elhub / (Pnom × PH)
OutputFactor (OF)             = Σ Elhub / (Pnom × SH)
NetCapacityFactor             = bruk netto effekt etter egenforbruk
```

### Hendelsesbaserte
```
MTBF = SH / antall FO
MTTR = FOH / antall FO
Availability_MTBF = MTBF / (MTBF + MTTR)
```

### Hydro-spesifikke (implementeres nå, kan være null fra Nivå 0)
```
HydroResourceAvailability      (Nivå 1+)
WaterUsageEfficiency           (Nivå 3+)
SpillLoss                      (Nivå 1+)
EnvironmentalFlowCompliance    (Nivå 1+)
```

### Plan-avvik (Nivå 0 – kan beregnes nå)
```
PlanFulfillment
BidAccuracy
PlanToBidDeviation
ImbalanceCausedByDeviation
PlanDeviation_MWh
PlanDeviation_NOK
```

---

## Rotårsakskategorier (Cause Codes)

Taksonomi på to nivåer, basert på NERC GADS tilpasset vannkraft:

1. Turbin
2. Generator
3. Styresystem / PLS
4. Transformator / høyspenning
5. Vannvei (inntak, rørgate, luke)
6. Nett (netteier, vern)
7. Miljø / eksternt (flom, ras, lyn)
8. Ressurs – OMC (vassføring, islegging)
9. Marked – OMC (klassifiseres som RS)
10. Menneske / drift

---

## Proxy-klassifisering fra settlement alene (Nivå 0-regler)

Uten SCADA er klassifisering en regelbasert heuristikk. Alle klassifiseringer flagges med `confidence`-verdi:

### For `PlantType = Regulated` (Drivdal)
- `MWh = 0` i sammenhengende døgn → sannsynlig `PlannedOutage`, confidence: medium
- `MWh = 0` i 1–6 t + høy spotpris → sannsynlig `ForcedOutage`, confidence: low
- `MWh = 0` + Spotpris < estimert marginalkostnad (fra `IPlantConfiguration`) → `ReserveShutdown`, confidence: medium
- `MWh = 0` + manglende Elhub-sending → `InformationUnavailable`, confidence: high
- `MWh < Produksjonplan` med fast forhold → mulig `PlannedDerating`, confidence: low

### For `PlantType = RunOfRiver` (framtidige verk, men regler defineres nå)
- `MWh = 0` i lengre periode + kjent lavflomsesong → `ResourceUnavailable`, confidence: low (uten hydrologi)
- Regel oppgraderes på Nivå 1 når NVE Sildre kobles til.

All klassifisering skrives til `ClassifiedPeriod` med `Sources = ["Settlement"]`. Når senere nivåer kobles på, overstyrer de med høyere confidence og egne `Sources`.

---

## Håndtering av manglende data (spesifikke scenarier for settlement)

Bruk det generelle rammeverket fra plattformen, med følgende konkrete regler:

| Scenario | Handling |
|---|---|
| Hele `Summering`-fane mangler | Accept – flagg for senere kryssvalidering |
| `Produksjonplan`-kolonne mangler | Accept, deaktivér Plan-KPI-er, ikke hele rapporten |
| `MWh-Elhub` mangler på enkelt­timer | `DataQualityState = InformationUnavailable`, **ikke** nedetid |
| `Ubalanse` mangler | Beregn fra `MWh-Elhub − Spotbud` hvis begge finnes, ellers flagg |
| Hele timer mangler i sekvensen | Sett inn `InformationUnavailable`-rad for å bevare tidsseriens integritet |
| Time-kolonnen har huller over DST | 23 t i mars, 25 t i oktober – eksplisitt handling |
| Negative `MWh-Elhub` | Flagg – kan være regulerkraft-kjøp eller måleavvik |
| Kolonnenavn endret i nyere eksport | `SchemaRegistry` med versjons­deteksjon + mapping |
| Verdi utenfor fysisk rimelig intervall (f.eks. MWh > 2 × Pnom) | Quarantine |

### Datakvalitetsrapport per import
Hver `SettlementDataSource.ImportAsync` skal produsere:
- Timer forventet / mottatt / akseptert / flagget / avvist
- Per kolonne: antall null, antall out-of-range, antall substituert
- Avvik med alvorlighetsgrad
- Delta mot forrige import (skjemaendringer, nye avvik)

Rapporten persisteres som egen `DataQualityReport`-entitet via `IFileStorage`.

---

## SCADA-integrasjon – forbered nå, implementer senere

`IScadaDataSource` har stub-implementasjon fra plattformen. I denne leveransen skal du **ikke** implementere SCADA, men verifisere at:

- `AssetEvent`-modellen kan bære SCADA-målinger og -alarmer uten endring.
- `ClassifiedPeriod.Sources` kan kombinere flere kilder.
- `UptimeAnalyzer.Fused` eksisterer som ikke-implementert klasse med kontrakt for sammenstilling av settlement + SCADA + hydrologi.

Dokumentér hvordan fremtidig `ScadaDataSource.OpcUa`-modul vil registreres og konsumere samme `AssetEvent`-strøm.

---

## Moduler som skal leveres i denne prompten

Opprett følgende under `src/KraftverkUptime.Modules/`:

### 1. `KraftverkUptime.Modules.Settlement`
- `SettlementDataSource` implementerer `ISettlementDataSource`. Leser Excel (openpyxl-ekvivalent: EPPlus eller ClosedXML).
- `SettlementImportJob` – kjøres via `IJobQueue`.
- `SettlementSchemaRegistry` – versjons­deteksjon.
- Validering og datakvalitetsrapport som beskrevet.

### 2. `KraftverkUptime.Modules.UptimeAnalyzer.Settlement`
- `UptimeAnalyzerSettlement` implementerer `IAnalyzer<SettlementPeriod, UptimeReport>`.
- Proxy-klassifiseringsregler som beskrevet, betinget på `PlantType`.
- Alle KPI-formler fra katalogen over.
- Skriver `ClassifiedPeriod` med `Sources = ["Settlement"]`.

### 3. `KraftverkUptime.Modules.UptimeAnalyzer.Fused`
- Klasse med kontrakt, men `NotImplementedException` i v1.
- Dokumentér hvordan senere kilder (SCADA, hydrologi) legges til uten refaktorering.

### 4. `KraftverkUptime.Modules.Reporting.Uptime`
- `UptimeReportBuilder` – bygger abstrakt rapport­modell.
- `ExcelReportRenderer` – implementerer `IReportRenderer` for .xlsx.
- 3-veis figur med Plan / Spotbud / Elhub over tid.
- KPI-tabell med verdi, antall timer, confidence.

---

## Rapporteringsrytme (ikke bygges nå, men planlegg for)

- **Daglig:** driftsstatus, alarmer, avvik fra plan
- **Ukentlig:** AF, EAF, produksjonsavvik, topp-5 nedetids­hendelser
- **Månedlig:** full IEEE 762-KPI-rapport + fleet-benchmark + Pareto-fordeling
- **Kvartalsvis:** Reliability Growth-trend, vedlikeholds­prioritering
- **Årlig:** lifetime EAF og CF, kandidater for reinvestering

v1 leverer månedlig rapport for Drivdal.

---

## Benchmark-verdier for sanity check

- Nordiske vannkraftverk: EAF 92–97 %
- Kapasitetsfaktor: magasinkraft 35–55 %, elvekraft/småkraft 40–80 %
- MTTR småkraft: 4–48 t typisk

Hvis beregningene faller vesentlig utenfor – mistenk metodefeil før du rapporterer.

---

## Testdata og fiksturer

Bruk den faktiske Drivdal-filen:
- `tests/fixtures/drivdal-feb2025.xlsx` – månedsfil for februar 2025, 672 timer.
- Kjente fakta fra denne filen: total produksjon 703,55 MWh, 391/672 produksjonstimer, MWh-Elhub == MWh-eSett, ingen manglende timer.

Tester skal verifisere:
1. Parsing-enhetsrad hoppes over, kolonne 17 ignoreres.
2. KPI-beregninger matcher forventede verdier (fasit i `tests/expected/drivdal-feb2025.json`).
3. Klassifisering for null-produksjonstimer følger regelsettet for `Regulated`.
4. 3-veis avviks-KPI-er beregnes korrekt.
5. Datakvalitetsrapport flagger forventede avvik (hvis noen).

---

## Leveranseformat

Følg samme regler som i Prompt 1:
- Komplette filer med full sti.
- `dotnet test` grønn umiddelbart.
- README per modul med formål, kjør-tester, utvidelsespunkter.
- Eksempel-output (KPI-tabell for Drivdal februar 2025) i README.
- Valideringssjekkliste før leveransen avsluttes.

---

## Rekkefølge for leveranse

1. **Modul `Settlement`** – parser + schema registry + import-job + data quality report.
2. **Modul `UptimeAnalyzer.Settlement`** – klassifisering + KPI-beregninger + tester mot Drivdal-fiksturen.
3. **Modul `UptimeAnalyzer.Fused`** – kontrakt + dokumentert utvidelses­plan.
4. **Modul `Reporting.Uptime`** – rapport­bygger + Excel-renderer + eksempel-output.
5. **Registrering i composition root** – *én linjes endring* per modul i `Program.cs`.
6. **Ende-til-ende-test** – last inn Drivdal-filen, generer månedsrapport, verifiser KPI-er mot fasit.

---

## Start her

Bekreft før du begynner:
1. Har du lest Prompt 1 og forstått hvilke kontrakter som allerede eksisterer?
2. Foreslår du endringer i KPI-katalogen eller klassifiseringsreglene? Begrunn.
3. Er det noen av fallgruvene (DST, kolonne 17, MWh-Elhub == MWh-eSett-sjekk) du vil håndtere annerledes?

Etter bekreftelse starter du på modul 1 (`Settlement`).
