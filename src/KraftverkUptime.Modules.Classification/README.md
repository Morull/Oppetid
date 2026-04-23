# KraftverkUptime.Modules.Classification

Proxy-klassifisering (IEEE 762 / NERC GADS) og KPI-beregning basert på
settlement-data alene. Dette er **Nivå 0** i modenhetsstigen — 60–70 %
klassifiseringsnøyaktighet. Hydrologi (Nivå 1), marked (Nivå 2) og SCADA
(Nivå 3) legges til uten å endre denne modulen.

Modulen dekker det Prompt 2 kalte `UptimeAnalyzer.Settlement` og
`UptimeAnalyzer.Fused` (stub).

## Struktur

    Config/PlantClassificationConfig   - terskelverdier per anlegg
    Dtos/
        ClassifiedHourlyRow            - time + state + confidence
        KpiResult                      - én KPI med metadata
        UptimePeriod                   - analyzer-input
        UptimeReport                   - analyzer-output
    Classification/
        RunLengthCalculator            - run-length for null-produksjons-blokker
        SettlementClassifier           - klassifiseringsregler
    Kpi/
        UptimeKpiCalculator            - IEEE 762 KPI-katalog
    Analyzers/
        SettlementUptimeAnalyzer       - Nivå 0 (implementert)
        FusedUptimeAnalyzer            - Nivå 3+ (stub, utvidelsesplan i kommentarer)
    ClassificationModule               - DI-registrering

## Klassifiseringsregler

Alle regler betinget på `PlantType`. For `Regulated` (Drivdal):

| Situasjon | UnitState | Confidence |
|---|---|---|
| Mangler Elhub-data | InformationUnavailable | 1.0 |
| Elhub < 0 | InformationUnavailable | 0.6 |
| Elhub = 0, Plan > 0 | ForcedOutage | 0.80 |
| Elhub = 0, Plan = 0, sammenhengende ≥ 24h | PlannedOutage | 0.70 |
| Elhub = 0, Plan = 0, Spot < median | ReserveShutdown | 0.55 |
| Elhub = 0, Plan = 0, Spot ≥ median | ForcedOutage | 0.45 |
| Elhub = 0, Plan = 0, ingen Spot | ReserveShutdown | 0.40 |
| Elhub > 0, ratio < 0.90 av Plan | ForcedDerating | 0.65 |
| Elhub > 0, ratio ≥ 0.90 | InService | 0.95 |
| Elhub > 0, ingen Plan | InService | 0.80 |

For `RunOfRiver`: Elhub = 0 uten hydrologi → ResourceUnavailable. Regelen
oppgraderes når hydrologi-modul er koblet inn (Nivå 1).

## KPI-katalog

Alle følger IEEE 762 / NERC GADS. Nevnere:

- **Unit Performance**: `effective_hours = PH − RU − IU` (ekskluderer OMC og manglende data)
- **System Reliability**: `PH − IU` (inkluderer OMC)

Samsvarer 1:1 med `drivdal-feb2025-fasit.json`. Regresjonstest sammenligner
hver KPI mot fasit innen numerisk toleranse (1e-9 for ratioer, 1e-4 for MWh/NOK).

## Utvidelsesplan (Nivå 1 →)

Fused-analyzeren er tydelig dokumentert i `FusedUptimeAnalyzer.cs` som
utvidelsespunkt. Kort sagt:

1. **Hydrologi** overstyrer `ResourceUnavailable`-heuristikken.
2. **Marked** forbedrer `ReserveShutdown`-terskelen med vannverdi.
3. **SCADA** overstyrer `FO`/`PO` med faktisk driftstilstand.
4. **CMMS** beriker CauseCode med arbeidsordrereferanser.

Hver tilkobling er en ny modul — ingen eksisterende moduler endres.

## Fallgruver

- Median spotpris beregnes én gang per periode. Hvis måneden har ekstrem
  prisvariasjon (f.eks. noen uker nær 0 og noen nær 5000 NOK/MWh), blir
  medianen lite informativ. En tidsvindus-basert median er en kandidat for
  senere.
- `SustainedStopHours = 24` er hardkodet som default. Noen anlegg har
  planlagte stopp som er kortere (f.eks. skift-stopp på 12 t). Sett per-anlegg
  via `IPlantConfiguration`.
- Confidence aggregeres ikke over hendelser i v1. En FO-hendelse som varer
  10 timer har samme confidence som én som varer 1 time. Rapport-modulen
  kan vekte på varighet ved behov.

## Testing

Tester ligger i `tests/KraftverkUptime.Modules.Classification.Tests`.
Regresjonstest: `ClassifyAndComputeKpis_MatchesFasit_ForDrivdalFeb2025` laster
`drivdal-analyse/fixtures/drivdal-feb2025.xlsx`, kjører full pipeline og
verifiserer hver KPI mot `drivdal-feb2025-fasit.json` med numerisk toleranse.
