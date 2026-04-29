# KraftverkUptime.Modules.Settlement

Leser månedlig oppgjørseksport fra **KAIA-portalen** (Excel) og produserer
normalisert tidsserie + datakvalitetsrapport. KAIA aggregerer underliggende
data fra Elhub (målt produksjon), eSett (ubalanse-oppgjør) og Nord Pool
(spotpris) — kolonnenavnene i eksporten beholder Elhub/eSett-terminologi
fordi KAIA bruker dem som-er. Dette er Nivå-0-dataen som
`UptimeAnalyzer.Settlement` bygger klassifisering og KPI-er på.

## Designvalg

**ClosedXML fremfor EPPlus.** EPPlus 5+ har kommersiell lisens for næringsbruk.
ClosedXML (MIT) gir dekkende .xlsx-lesing uten lisensrisiko og er tilstrekkelig
raskt for månedsfiler på 672 rader.

**Streng skjema-sjekk.** Filer med ukjent header-layout markeres
`SCHEMA_UNKNOWN` og parser tar ikke sjanser. Ny eksportversjon legges til ved å
utvide `SettlementSchemaRegistry.Detect` – ingen annen kode endres.

**Tidssoner.** Time-kolonnen er naiv Europe/Oslo og konverteres til UTC internt.
DST-ikke-eksisterende timer i mars forkastes med `DST_NONEXISTENT`-avvik.
Tvetydige timer i oktober løses deterministisk ved å velge den første
forekomsten (sommertid, UTC+2).

**Elhub/eSett-toleranse.** Sammenligning er floating-point med ε = 0.001 MWh.
Avvik over det flagges men stopper ikke importen.

**Manglende timer fylles inn.** `DataQualityReportBuilder` oppdager hull i
sekvensen og legger til `InformationUnavailable`-rader slik at tidsserien
alltid er komplett. Dette er en førsteklasses datakvalitetsregel –
manglende data er aldri det samme som nedetid.

## Filer

    Dtos/
        SettlementHourlyRow       - én time fra verk-fane
        SettlementSummaryRow      - aggregatrad fra Summering-fane
        ParsedSettlement          - samlet parse-resultat
    Parsing/
        SettlementColumnMapping   - norsk → kanonsk feltnavn
        SettlementSchemaRegistry  - versjonsdeteksjon
        ExcelSettlementParser     - ClosedXML-basert parser
    Quality/
        ValidationIssue           - én avviksrad + alvorlighetsgrad
        DataQualityReport         - persisterbar rapport per import
        DataQualityReportBuilder  - beriker hourly med DqState + bygger rapport
    Jobs/
        ParseSettlementJob        - DTO for jobbkøen
        ParseSettlementJobHandler - konsument (henter blob, parser, event)
    ISettlementParser             - modul-intern kontrakt
    SettlementModule              - DI-registrering

## Utvidelsespunkter

**Ny portalversjon** – legg til `SettlementSchemaV2`-klasse, registrér i
`SettlementSchemaRegistry.Detect`, utvid `SettlementColumnMapping` for nye
kolonnenavn. Ingen endringer i `ExcelSettlementParser`.

**Annet filformat (CSV, JSON)** – lag ny implementasjon av `ISettlementParser`
og registrér den med samme lifetime i `SettlementModule`. Consumers (analyzer,
reporting) ser bare interfacet.

**Persistert indeks for DataQualityReport** – blob-lagring via `IFileStorage`
er standard. Indeksering i relasjonslager (importId → blob-URL) legges til
som en separat `IDataQualityIndex`-seam – Settlement-modulen selv trenger
ikke å endres.

## Fallgruver

- ClosedXML leser datoer både som tekst og som `DateTime`-verdi. Parseren
  håndterer begge, men krever eksakt format `dd.MM.yyyy HH:mm` for tekst-
  varianten.
- Kolonne 17 er tom spacer i v1 – `SchemaRegistry` krever den ikke, men
  oppdager hvis nye versjoner fyller den ut.
- Norske spesialtegn (`Oppgjør`, `RK-kjøp`) fantes historisk i begge
  skrivemåter. `SettlementColumnMapping` har eksplisitte oppføringer for begge.
- `SummaryMismatchRelTolerance = 0.1 %` – små avvik ved avrunding mellom
  Elhub/eSett aksepteres uten avvik.

## Testing

Enhetstester ligger i `tests/KraftverkUptime.Modules.Settlement.Tests`.
Referansefikstur er `drivdal-analyse/fixtures/drivdal-feb2025.xlsx`;
fasit-KPI-ene er i `drivdal-feb2025-fasit.json`. Testene verifiserer:

1. Timerader parses korrekt (672 timer for februar 2025).
2. Enhetsrad (rad 2) hoppes over.
3. Kolonne 17 (spacer) ignoreres uten feil.
4. Summering-totaler matcher sum av timer innenfor 0.1 %.
5. MWh-Elhub == MWh-eSett per time (innenfor 0.001 MWh).
6. Datakvalitetsrapport rapporterer 672 mottatte / 672 aksepterte timer.
