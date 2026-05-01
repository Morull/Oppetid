# Neste sesjon — Capture Rate KPI

**Dato opprettet:** 2026-04-30
**Spec:** `docs/SPEC-CAPTURE-RATE.md`
**Estimat:** 1-2 dager
**Forrige sesjon:** `OVERLEVERING-2026-04-29-VEIKART.md` (Steg 0–7 ferdig, alle 11 anlegg har samme funksjonalitet)

## Mål

Implementer Capture Rate KPI for alle 11 anlegg basert på spec i `docs/SPEC-CAPTURE-RATE.md`. To varianter:

1. **Times-CR** (volumvektet, hovedversjon) — bransjestandard, bruker timesoppløsning fra settlement-modulen
2. **Dag-CR** (Excel-replika med 5/95-persentilfilter) — matcher eksisterende Excel-modell `Capture Rate KPI.xlsx`, brukes for regresjons-validering og rapportering til drifts-leder

KPI-en skal eksponeres via API, portefølje-dashboard, effektivitets-side og egen `/capture-rate/{plantId}`-side.

## Nåværende status

- Spec ferdigstilt og lagret som `docs/SPEC-CAPTURE-RATE.md`
- Excel-modell `Capture Rate KPI.xlsx` analysert — 13 anlegg, dagsbasert CR, 5/95-persentilfilter
- 2 av Excel-anleggene (Hisvatn, Kleivan) er bevisst **ikke** med i implementasjonen — kun de 11 i `core.plants`
- Ingen kode skrevet ennå — start fra steg 1 i specens "Implementasjons-rekkefølge"

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Varianter | Begge — times-CR (volumvektet) + dag-CR (Excel-replika) |
| Inntekt | Brutto spot — `SpotomsetningNok / MwhElhub` |
| Pris-baseline | `core.market_prices` ny tabell, kilde `settlement` (primær) + `entsoe` (backup for hull) |
| Anlegg | Kun de 11 i veikartet, ikke Hisvatn/Kleivan |
| Persentilgrenser | 5/95, konfigurerbare via `CaptureRateOptions` |
| Tidssone for dag-CR | Europe/Oslo (matcher Excel-modellens kalenderdag) |
| EUR→NOK kurs | Fast 11.5 i v1 (bare relevant for ENTSO-E backfill) |
| ENTSO-E token | Brukeren har egen — settes via Key Vault i prod, env-var i dev |

## Antakelser fra brukeren

- Settlement-fila inneholder **alle timer i måneden** med utfylt `SpotprisNokMwh`, også timer der anlegget hadde 0 produksjon. Dette gjør at NO2-baseline kan beregnes direkte fra settlement-data uten ekstern integrasjon.
- Hvis denne antakelsen brytes (verifiseres ved første implementasjon): ENTSO-E backfill-jobb (steg 10) fyller hull.

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `MarketPrice` entity + migrering | 1-2 t |
| 2 | Settlement → `market_prices` handler + tester | 2 t |
| 3 | `CaptureRateCalculator` (pure funksjon) + 12 unit-tester | 4 t |
| 4 | `CaptureRateQueryService` + DB-queries | 3 t |
| 5 | API-endepunkter + contracts | 1 t |
| 6 | **Regresjonstest** mot Excel-pivot for alle 11 anlegg | 2 t |
| 7 | Portefølje-kolonne (PortfolioQueryService + Razor-side) | 1-2 t |
| 8 | Effektivitets-side KPI-kort | 30 min |
| 9 | `/capture-rate/{plantId}`-side med graf/scatter/histogram | 3-4 t |
| 10 | ENTSO-E backfill-jobb (defer hvis settlement har komplette timer) | 3-4 t |

**Stopp etter steg 6** og verifiser at regresjonstesten matcher Excel-pivot for alle 11 anlegg innenfor ±0.005 før UI-arbeidet starter. Hvis avvikene er større, debugg formel/datakilde først.

## Regresjons-fasit (Excel-pivot 2025)

```
drivdal:    1.0857
grodemfoss: 0.9993
haukland:   1.0141
honnefoss:  1.0199
liavatn:    0.9953
lindland:   0.9927
logjen:     1.0464
stolskraft: 0.9937
vikesa:     1.0098
ogreyfoss:  1.0198
orsdalen:   0.99999
```

Tilsvarende inntekt og merverdi for utvalgte anlegg ligger i spec-en under "Akseptansekriterier".

## Åpne spørsmål

1. Eksisterer `Plant.PriceArea`-feltet allerede? Sjekk `PlantRegistration.cs` før migrering. Hvis ikke, legg til og backfill alle 11 til "NO2".
2. Settlement-import må trigge et `SettlementImportedEvent` for at `MarketPriceUpsertHandler` skal koble seg på. Verifiser at eventet finnes — hvis ikke, legg til.

## Spørre-policy

Følg samme mal som `docs/VEIKART-AUTONOM.md`:
- Tekniske valg (lib, navn, struktur): bestem selv
- Forretningslogikk (formel, terskler): bruk spec-en
- Hvis regresjons-fasiten ikke matcher etter steg 6: pause og rapporter til bruker
- Hvis ENTSO-E-integrasjonen krever ny token-håndtering: pause og spør

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Etter steg 6 skal denne PowerShell-bolken returnere CR-tall som matcher Excel-pivot innenfor ±0.005 for alle 11 anlegg.

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-CAPTURE-RATE.md` med kommit-tabell, test-status, eventuelle avvik fra spec, og forslag til neste prioriteter.

Foreslåtte neste prioriteter etter capture rate:
- Vannverdi-modell (alternativkost for vakt-ROI)
- Bytte til TimescaleDB (når sample-volum vokser)
- Multi-prisområde-støtte (når portefølje vokser utenfor NO2)
