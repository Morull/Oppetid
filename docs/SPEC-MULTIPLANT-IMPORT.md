# Spec: Multi-anleggs settlement-import + 15-min aggregering

**Status:** Klar til implementasjon (2026-04-29)
**Estimat:** 4-6 timer
**Forutsetning:** `OVERLEVERING-2026-04-29.md` Prioritet 1 levert. Vakt-ROI kan være under arbeid (overløp/ubalanse) — denne specen krasjer ikke med den.

## Bakgrunn

To strukturelle endringer i settlement-eksporten fra Dalane Kraft:

1. **Multi-anleggs-fil** — én Excel inneholder nå alle anleggene i porteføljen (9 anlegg i denne, pluss Vikeså og Stølskraft i egne filer). Tidligere var det én fil per anlegg.
2. **15-min granularitet** — fra mars 2025 har eksporten 15-min rader (96 per dag) pga. EU-direktivets ISP-overgang. Dagens parser forventer 60-min.

I tillegg har eksporten et **tegnsett-problem** der fane-navnene mister norske bokstaver (`Løgjen` → `Lgjen`, `Grødemfoss` → `Grdemfoss`, `Øgreyfoss` → `greyfoss`, `Ørsdalen` → `rsdalen`). De **kanoniske navnene** står riktig i:
- Summering-fanens kolonne A
- R1 i hver anleggsfane (tittelen "Løgjen 01.02.2026 - 28.02.2026")

## Fil-struktur (verifisert mot `dataeksport_20260429103503.xlsx`)

### Summering-fane

```
R1: ('Periode', '01.02.2026 - 28.02.2026', ...)
R2: ('Valuta', 'NOK', ...)
R4: ('Tidsserie', 'MWh-Elhub', 'MWh-eSett', 'Spotbud', 'Spotomsetning', 'Ubalanse', 'RK-kjøp', 'RK-salg')
R5: (None, 'MWh', 'MWh', 'MWh', 'NOK', 'MWh', 'NOK', 'NOK')
R6+: ('Løgjen', 52.71, 52.71, 70.7, 79142.24, 17.99, -31126.84, 7537.34)
     ('Drivdal', 214.28, 214.28, ...)
     ... (9 rader, ett per anlegg)
```

Bruk denne fanen til å bygge plant-mapping (fane-indeks → kanonisk navn).

### Anleggs-fane (eksempel: `1 Lgjen`)

```
R1: ('Løgjen 01.02.2026 - 28.02.2026', ...)   ← KANONISK NAVN her
R2: ('Time', 'MWh-Elhub', 'MWh-eSett', 'Spotbud', 'Spotpris',
     'Spotomsetning', 'Ubalanse', 'RK-pris', 'RK-kjøp', 'RK-salg',
     'Nord Pool gebyr', 'eSett volumgebyr')
R3: (None, 'MWh', 'MWh', 'MWh', 'NOK', 'NOK', 'MWh', 'NOK', 'NOK', 'NOK', 'NOK', 'NOK')
R4+: 15-min-rader, 2688 stk for feb (28 × 96)
     ('01.02.2026 00:00', 0, 0, 0, 1132.87, 0, 0, 1113.92, 0, 0, 0, 0)
     ('01.02.2026 00:15', 0, 0, 0, 1124.50, ...)
     ('01.02.2026 00:30', ...)
     ...
```

## Anlegg som skal opprettes

Fra denne fila + de to ekstra som kommer i egne filer:

| Plant-ID | Kanonisk navn | I denne fila |
|---|---|---|
| `logjen` | Løgjen | ✓ |
| `drivdal` | Drivdal | ✓ (eksisterer allerede) |
| `grodemfoss` | Grødemfoss | ✓ |
| `haukland` | Haukland | ✓ |
| `honnefoss` | Honnefoss | ✓ |
| `lindland` | Lindland | ✓ |
| `ogreyfoss` | Øgreyfoss | ✓ |
| `orsdalen` | Ørsdalen | ✓ |
| `liavatn` | Liavatn | ✓ |
| `vikesa` | Vikeså | Egen fil senere |
| `stolskraft` | Stølskraft | Egen fil senere |

Plant-ID = navn slugifisert (lowercase + `æ→a`, `ø→o`, `å→a`, fjern alt ikke-alfanumerisk).

`InstalledCapacityMw` er IKKE i fila — sett til `0` ved auto-opprettelse, flagg som "trenger oppsett". Brukeren oppdaterer manuelt senere via /plants-admin (eller direkte i DB).

## Implementasjons-stegene

### Steg 1 — Plant slugification + auto-opprett

**Ny fil:** `src/KraftverkUptime.Core/Domain/PlantSlug.cs`

```csharp
namespace KraftverkUptime.Core.Domain;

public static class PlantSlug
{
    /// <summary>
    /// Konverterer et plant-navn til en stabil slug for bruk som plant_id.
    /// "Løgjen" → "logjen", "Grødemfoss" → "grodemfoss",
    /// "Øgreyfoss" → "ogreyfoss", "Stølskraft" → "stolskraft"
    /// </summary>
    public static string ToSlug(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            sb.Append(c switch
            {
                'æ' => "a",
                'ø' => "o",
                'å' => "a",
                ' ' or '-' or '_' => "",
                _ when char.IsLetterOrDigit(c) => c.ToString(),
                _ => ""
            });
        }
        return sb.ToString();
    }
}
```

Tester: dekk alle 11 anleggene + edge-cases (tomt, tomme mellomrom, allerede slug-format).

### Steg 2 — Plant auto-opprett ved import

I `SettlementUploadHandler` (eller `ParseSettlementJobHandler`):
- Etter parsing av hver anleggsfane, sjekk om `core.plants` har en rad med `Id = slug`
- Hvis ikke: opprett ny `PlantRegistration` med `Name = kanoniskNavn`, `Type = PlantType.Regulated` (default for vannkraft), `InstalledCapacityMw = 0`, `TimeZone = "Europe/Oslo"`, `OwnerOrgId = "dev-org"` (samme som settlements)
- Logg at nytt anlegg ble opprettet

### Steg 3 — Multi-sheet parser

**Endre:** `src/KraftverkUptime.Modules.Settlement/Parsing/ExcelSettlementParser.cs`

Endre `ISettlementParser`-kontrakten til:

```csharp
public interface ISettlementParser
{
    /// <summary>
    /// Parser én eller flere anleggs-faner fra en .xlsx. Returnerer ett resultat
    /// per anlegg som ble funnet. Hvis fila er enkelt-anleggsformatet (ett ark
    /// med plant-data), returneres en liste med ett element.
    /// </summary>
    Task<IReadOnlyList<ParsedSettlement>> ParseAllAsync(
        Stream xlsx, string ownerOrgId, CancellationToken ct);
}
```

Oppdag fil-format basert på fane-mønster:
- Hvis det finnes en "Summering"-fane OG flere faner som matcher `^\d+ \w+$` → multi-plant
- Ellers → enkelt-plant (gammelt format)

For hver anleggsfane:
1. Les R1 (tittelen) — hent plant-navn (alt før første dato-substring `dd.MM.yyyy`)
2. Beregn slug via `PlantSlug.ToSlug(plantName)`
3. Les header R2 + enheter R3 for kolonne-mapping
4. Les data-rader fra R4 og utover
5. **Detekter granularitet**: forskjell mellom rad 4 og rad 5 sin time-stempel
   - 60 min → behold som er
   - 15 min → aggreger til timer ved import (se steg 4)
6. Konstruer `ParsedSettlement` med riktig plant-id

### Steg 4 — Aggregering 15-min → 60-min

I `ExcelSettlementParser` eller separat helper:

For hver gruppe på 4 rader med samme time-prefiks (`00:00`, `00:15`, `00:30`, `00:45` → time `00:00`):

| Felt | Aggregering |
|---|---|
| `MwhElhub`, `MwhESett`, `SpotbudMwh`, `UbalanseMwh` | SUM |
| `SpotomsetningNok`, `RkKjopNok`, `RkSalgNok`, `NordPoolGebyrNok`, `ESettVolumgebyrNok`, `ESettUbalansegebyrNok`, `OppgjorNok` | SUM |
| `SpotprisNokMwh` | Vektet snitt med `\|MwhElhub\|`, fallback rent gjennomsnitt hvis sum=0 |
| `RkPrisNokMwh` | Vektet snitt med `\|UbalanseMwh\|`, fallback rent gjennomsnitt hvis sum=0 |
| `ProduksjonplanMwh`, `EffektavlesningerMw`, `AbsUbalansevolumMwh` | SUM hvis MWh, snitt hvis MW |
| `BruttoOmsetningNok`, `UbalanseResultatNok`, `MeglerprovisjonNok`, `SumSalgNok` | SUM |

Hvis 4 rader ikke har eksakt 15-min mellomrom → flagg som datakvalitet-issue, men aggreger likevel.

### Steg 5 — DataQuality + IdempotencyKey

`DataQualityReportBuilder` må håndtere:
- Forventet rad-antall: 60-min: timer-i-måneden, 15-min: timer-i-måneden × 4
- Sjekk at alle kvarter i en time finnes (96 per døgn for ikke-DST-dager, 92 for vår-DST, 100 for høst-DST)

`IdempotencyKey` per anleggsfane: SHA256 av `(plantId|periode|hash av rene tall fra fanen)`. Multi-plant-fila skal generere én rad per anlegg.

### Steg 6 — Tester

**Ny fil:** `tests/KraftverkUptime.Infrastructure.Tests/Settlement/MultiPlantParserTests.cs`

- `Parse_DataeksportFormat_Returnerer_Et_Resultat_Per_Anlegg` (bruk en kopi av brukerens fil i `tests/fixtures/`)
- `Parse_15Min_Granularitet_Aggregeres_Til_Timer`
- `Parse_60Min_Granularitet_Beholdes`
- `Parse_Bevarer_Norske_Tegn_Fra_Cell_R1` (verifiser at "Løgjen" hentes selv om fane heter "1 Lgjen")
- `Parse_Vektet_Snitt_For_Spotpris` (kvartersrader med ulik MWh og pris → riktig vektet snitt)

**Ny fil:** `tests/KraftverkUptime.Core.Tests/Domain/PlantSlugTests.cs`

- `[Theory]` med alle 11 anleggene → forventet slug
- Edge: tomt navn, kun mellomrom, navn med tall

### Steg 7 — Database-migrering for nye anlegg

For å bootstrappe alle 11 anlegg uten å vente på først import:

```sql
INSERT INTO core.plants (id, owner_org_id, name, type, installed_capacity_mw, time_zone, created_at)
VALUES
  ('logjen',     'dev-org', 'Løgjen',     0, 0, 'Europe/Oslo', now()),
  ('grodemfoss', 'dev-org', 'Grødemfoss', 0, 0, 'Europe/Oslo', now()),
  ('haukland',   'dev-org', 'Haukland',   0, 0, 'Europe/Oslo', now()),
  ('honnefoss',  'dev-org', 'Honnefoss',  0, 0, 'Europe/Oslo', now()),
  ('lindland',   'dev-org', 'Lindland',   0, 0, 'Europe/Oslo', now()),
  ('ogreyfoss',  'dev-org', 'Øgreyfoss',  0, 0, 'Europe/Oslo', now()),
  ('orsdalen',   'dev-org', 'Ørsdalen',   0, 0, 'Europe/Oslo', now()),
  ('liavatn',    'dev-org', 'Liavatn',    0, 0, 'Europe/Oslo', now()),
  ('vikesa',     'dev-org', 'Vikeså',     0, 0, 'Europe/Oslo', now()),
  ('stolskraft', 'dev-org', 'Stølskraft', 0, 0, 'Europe/Oslo', now())
ON CONFLICT (id) DO NOTHING;
```

(Drivdal eksisterer allerede.)

`InstalledCapacityMw = 0` er placeholder — Morten oppdaterer manuelt etter at han har bekreftet effekten for hvert anlegg. Vakt-ROI vil gi 0 MWh-tall for anlegg med 0 kapasitet, så det blir tydelig hva som mangler.

`type = 0` = `PlantType.Regulated` (default — alle Dalane-anleggene er regulerte vannkraftverk).

Legg migreringen som ny EF-migration eller som idempotent SQL-script kjørt fra `DatabaseBootstrapper.ApplyMigrationsAsync`.

### Steg 8 — API-endepunkt (eller dropp og bruk eksisterende)

Eksisterende `POST /api/v1/plants/{plantId}/settlements` forventer en plant-id i URL. For multi-anleggsfila gir det ikke mening — vi vet ikke plantId før vi har parset.

To valg:

**A. Nytt endepunkt:** `POST /api/v1/settlements/multi-plant` — uten plant-id i URL. Parser fila, oppretter eventuelle nye plants, lagrer én rad per anlegg.

**B. Auto-detect i eksisterende:** la `{plantId}` være en magic-verdi som `_auto`, og hvis den er det, parse alle faner.

**Anbefales A** — tydeligere kontrakt, ingen magic strings.

### Steg 9 — UI-tilpasninger

Mindre, men nyttige:
- `/upload`-siden: legg til radio-knapp "Multi-anleggsfil (ny eksport-format)" som kaller det nye endepunktet
- `/plants`-siden: vis alle anlegg, marker de med `InstalledCapacityMw = 0` med rødt flagg ("krever oppsett")
- (Senere) UI for å sette `InstalledCapacityMw` per anlegg uten DB-direkte-redigering

## Verifisering

### Etter Steg 1-2 (slug + auto-create):

```powershell
# Manuell test i Postgres
docker compose exec postgres psql -U kraftverk -d kraftverk -c "SELECT id, name FROM core.plants ORDER BY id;"
```

Forventet: 11 rader.

### Etter Steg 3-7 (parser + aggregering):

```powershell
# Last opp test-fila (kvartersdata for feb-2026, 9 anlegg)
Push-Location "C:\Morten\00 Oppetid\CSV Eksporter"
curl.exe -X POST "http://localhost:5080/api/v1/settlements/multi-plant" `
         -F "file=@dataeksport_20260429103503.xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
Pop-Location

# Forventet: 9 settlement_imports-rader, alle for periode 2026-02-01 → 2026-03-01
# hour_count = 672 (28 × 24) etter 15→60 min aggregering, IKKE 2688
docker compose exec postgres psql -U kraftverk -d kraftverk -c "
SELECT plant_id, period_start_utc, period_end_utc, hour_count, issue_count
FROM core.settlement_imports
WHERE imported_at_utc > now() - interval '5 minutes'
ORDER BY plant_id;"
```

### Cross-check Drivdal:

For Drivdal feb-2026 (nå tilgjengelig som ekte data):
```powershell
curl.exe "http://localhost:5080/api/v1/plants/drivdal/nedetid?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json | Select-Object antallEvents, totalNedetidTimer, totalTapNok
```

Sammenlign med tall fra feb-2025 (8 events, 32 t, 22 932 NOK) — tall vil være forskjellige men i samme størrelsesorden.

## Forventede gotchas / risikomomenter

1. **DST-overgang i mars og oktober**: 15-min-data har 92 eller 100 kvarter på DST-dager. Aggregeringen må gruppere på time, ikke anta nøyaktig 4 rader.

2. **Tegnsett i fane-navnene**: ikke prøv å rekonstruere norske bokstaver fra fane-navn — bruk R1-tittelen og Summering-fanen som ground truth.

3. **Kolonneorden** i anleggsfanene må sjekkes — schema-detection bør være robust mot at en kolonne flytter seg. Bruk header-tekst (R2) som lookup, ikke posisjon.

4. **Idempotens** ved re-import: samme fil lastet opp to ganger skal gi samme idempotency-key og overskrive med upsert-semantikk. Test dette.

5. **Tom fane** (anlegg uten produksjon hele perioden): skal fortsatt parses og lagres, ikke skippes.

6. **Faner som ikke følger `\d+ Navn`-mønsteret** (nye faner som "Notater" eller liknende fra eksport-systemet): skip dem stilt, logg på info-nivå.

## Out-of-scope

- UI for å sette `InstalledCapacityMw` per anlegg — gjøres i senere oppgave (Prioritet 2.5)
- Native 15-min support i klassifikator/UI — denne specen aggregerer til timer
- Vannverdi-beregning per anlegg
- Nøyaktige magasin-modeller for de andre anleggene (overløp i Vakt-ROI vil bare fungere for anlegg som har overløp-tag)

## Referanser

- Test-fil: `C:\Morten\00 Oppetid\CSV Eksporter\dataeksport_20260429103503.xlsx`
- Eksisterende parser: `src/KraftverkUptime.Modules.Settlement/Parsing/ExcelSettlementParser.cs`
- Plant-entitet: `src/KraftverkUptime.Infrastructure/Persistence/Entities/PlantRegistration.cs`
- DB-skjema: `core.plants`, `core.settlement_imports`
