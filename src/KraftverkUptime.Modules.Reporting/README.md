# KraftverkUptime.Modules.Reporting

Bygger oppetidsrapporter fra `UptimeReport`-modellen (produsert av
`Modules.Classification`) og rendrer dem som XLSX med fire faner:

1. **Sammendrag** – periode, plant, tilstandsfordeling, nøkkel-KPI-er
2. **KPI-katalog** – alle KPI-er med verdi, enhet, basis, confidence, definisjon
3. **3-veis** – time-for-time Plan / Spotbud / Elhub + avviks-kolonner
4. **Klassifisering** – per-time state, confidence, cause code, rationale

## Filer

    UptimeReportBuilder         - IReportBuilder<UptimeReport>-implementasjon
    UptimeReportRenderer        - IReportRenderer, format = "xlsx"
    ReportingModule             - DI-registrering

`IUptimePeriodProvider` er erklært i `UptimeReportBuilder.cs` men
implementeres i composition root / Infrastructure – den henter parsed
settlement fra lager.

## 3-veis-figur

3-veis-fanen har rådata som kan brukes til å bygge linjediagram i Excel
manuelt eller via en senere ChartRenderer-utvidelse. ClosedXML 0.104 har
begrenset chart-støtte – vi skriver data, ikke figur, i v1. Å legge til
automatisk chart er en enlinjers endring i `BuildThreeWaySheet` når
ClosedXML får bedre API (eller vi bytter til EPPlus med lisens).

## Rapportkadence (planlagt, ikke bygget i v1)

| Kadence | Innhold |
|---|---|
| Daglig | driftsstatus, alarmer, avvik fra plan |
| Ukentlig | AF, EAF, produksjonsavvik, topp-5 nedetids­hendelser |
| **Månedlig** | full IEEE 762-KPI-rapport (denne modulen bygger denne) |
| Kvartalsvis | Reliability Growth-trend, vedlikeholds­prioritering |
| Årlig | lifetime EAF og CF, kandidater for reinvestering |

Scheduling implementeres ikke her – konsument bruker `IJobQueue` med
cron-trigger (eller ekstern scheduler) til å kalle
`IReportBuilder<UptimeReport>.BuildAsync`.

## Utvidelsespunkter

- **Nytt format (PDF, HTML)** – implementer `IReportRenderer` med ny `Format`.
  Composition root velger riktig renderer ut fra forespørselens ønske.
- **Fleet-benchmark** – utvid `UptimeReportBuilder` med oppslag mot
  aggregerte fleet-data (snitt AF/CF for sammenlignbare anlegg).
- **Pareto-analyse** – legg til topp-5 FO-hendelser + varighet × frekvens
  i Sammendrag-fanen.

## Fallgruver

- ClosedXML er synkron – renderer returnerer `Task<Stream>` for kontrakt-
  konsistens, men gjør ikke faktisk async IO. Ikke bruk den i hot paths
  uten å wrappe i `Task.Run` hvis du har mange parallelle forespørsler.
- `IUptimePeriodProvider` må registreres separat i composition root.
  Uten den feiler DI med tydelig melding ved første `BuildAsync`-kall.
- Klassifisering-fanen kan bli stor (672 rader × 8 kolonner for en måned).
  For årlige rapporter bør man vurdere å droppe denne fanen eller dele
  rapporten i én fil per måned.
