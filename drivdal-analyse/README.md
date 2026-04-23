# Drivdal Nivå-0 oppetidsanalyse (Python PoC)

Dette er en proof-of-concept-implementasjon av Nivå-0-oppetidsanalyse basert
på settlement-data alene (Elhub/eSett-eksport fra portal). Formålet er
todelt:

1. Validere at KPI-formlene (IEEE 762 / NERC GADS, tilpasset hydro) gir
   fornuftige tall på faktiske Drivdal-data.
2. Produsere fasit (`fasit.json`) som senere brukes som regresjonstest for
   .NET-implementasjonen.

## Struktur

```
drivdal-analyse/
├── fixtures/drivdal-feb2025.xlsx   # Kildefil (Drivdal februar 2025)
├── src/
│   ├── parser.py     # SettlementDataSource - leser Excel, håndterer DST
│   ├── quality.py    # Datakvalitetsrapport med DataQualityState per time
│   ├── classifier.py # Proxy-klassifisering av UnitState per time
│   ├── kpi.py        # IEEE 762 KPI-katalog + 3-veis plan-avvik
│   ├── report.py     # Excel-rapport generator
│   └── main.py       # Orchestrator
└── output/
    ├── drivdal-feb2025-uptime-report.xlsx  # Menneskelesbar rapport
    ├── drivdal-feb2025-fasit.json          # Regresjons-fasit for .NET
    └── drivdal-feb2025-summary.txt         # Tekstlig sammendrag
```

## Kjøre

```bash
cd src
python3 main.py ../fixtures/drivdal-feb2025.xlsx ../output
```

## Resultat (Drivdal februar 2025)

**Total produksjon:** 703,55 MWh (matcher Summering-fane eksakt)

**Tilstandsfordeling (672 timer):**

| State              | Timer | Andel |
|--------------------|-------|-------|
| InService          | 370   | 55,1% |
| ForcedOutage       | 163   | 24,3% |
| PlannedOutage      |  67   | 10,0% |
| ReserveShutdown    |  51   |  7,6% |
| ForcedDerating     |  21   |  3,1% |

**Nøkkel-KPI-er:**

| KPI                        | Verdi    | Kommentar                                    |
|----------------------------|----------|----------------------------------------------|
| AvailabilityFactor (AF)    | 62,65%   | AH / (PH − OMC − IU)                         |
| EquivalentAvailability (EAF) | 61,09% | Justert for deratings                        |
| ServiceFactor (SF)         | 55,06%   | Timer i produksjon / totalt                  |
| ForcedOutageRate (FOR)     | 30,58%   | FOH / (FOH + SH)                             |
| CapacityFactor (CF)        | 47,59%   | MWh / (Pnom × PH), Pnom = 2,2 MW             |
| OutputFactor (OF)          | 86,43%   | MWh / (Pnom × SH) når i drift                |
| MTBF                       | 16,8 t   | Timer mellom forced outages                  |
| MTTR                       |  7,4 t   | Timer å reparere                             |
| **PlanFulfillment**        | 93,98%   | Elhub / Plan                                 |
| **BidAccuracy**            | 96,09%   | Elhub / Spotbud                              |
| **PlanDeviation_NOK**      | −52 826  | Verdien av underleveranse mot plan           |

## Tolkning av resultatene

**Dette er Nivå 0 (proxy-klassifisering) – ikke endelige tall.** Konklusjonene
må ses i lys av følgende:

- Klassifisering av null-produksjonstimer bygger på heuristikk og produksjonsplanen.
  Gjennomsnittlig confidence: 0,78.
- 24,3% "ForcedOutage" er sannsynligvis delvis overestimert fordi enkle null-
  produksjonstimer med lav confidence klassifiseres som FO når Plan > 0 eller
  spot-pris er høy.
- Faktisk tilgjengelighet er sannsynligvis høyere enn AF = 62,65 %.
  Benchmark for nordisk vannkraft er 92–97 %. Differansen mellom benchmark og
  beregnet AF er størrelsen på klassifiseringsusikkerheten.
- Når SCADA-data (Nivå 3) kobles på kan FO- og PO-klassifiseringer bekreftes
  eller overstyres basert på faktisk driftstilstand.

## Kjente begrensninger

- `marginal_cost_nok_mwh` er satt til 100 NOK/MWh som grov estimat. Endres i
  `main.py` per verk.
- Sammenhengende stopp ≥ 24 timer klassifiseres som PlannedOutage. Lengere
  sammenhengende feil kan feilklassifiseres som PO uten SCADA-bekreftelse.
- ImbalanceCorrelation er lav (0,07) – indikerer at ubalansevolum og RK-pris
  ikke er sterkt korrelert i denne måneden.

## Neste steg

1. Kjør på flere måneder og se hvor stabile KPI-ene er.
2. Bygg .NET-plattform (Prompt 1) som bruker `fasit.json` som regresjonstest.
3. Bygg domenemoduler (Prompt 2) som produserer identiske KPI-verdier.
4. Når SCADA kobles på, erstatt FO-klassifisering basert på faktisk driftstilstand.
