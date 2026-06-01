# Neste sesjon — KAIA-kostnad per rapportperiode

**Dato opprettet:** 2026-05-20
**Spec:** `docs/SPEC-KAIA-KOSTNAD.md`
**Estimat:** 1–2 dager
**Kjøres i:** Claude Code (multi-prosjekt .NET-endring + EF-migreringer + bygg/test)

## Mål

Vis hvor mye KAIA koster oss, **for rapportperioden som velges i appen**. To
kostnadskomponenter:

1. **Meglerprovisjon** — KAIAs provisjon på handlene de utfører for oss for å
   dekke ubalanser. Finnes allerede som kolonne `Meglerprovisjon` i
   oppgjørs-eksporten.
2. **Fast årskostnad** — 4 000 NOK per anlegg per år, pro-rata på periodens
   lengde. Ny konfigurasjon på anlegget.

KAIA-kostnaden skal vises som KPI-kort på `ReportDetail.razor` og som
porteføljesum på `Portefolje.razor`.

## Nåværende status

- Spec ferdigstilt: `docs/SPEC-KAIA-KOSTNAD.md`
- Datagrunnlag verifisert — `MeglerprovisjonNok` parses allerede korrekt i
  Settlement-modulen (`SettlementSummaryRow` + `SettlementHourlyRow`)
- Faktiske tall regnet ut fra filene i `CSV Eksporter/` — ligger som
  validerings-fasit i specen
- Ingen kode skrevet ennå — start fra steg 1 i specens "Implementasjons-rekkefølge"

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Hva er "KAIA-kostnad" | Kun meglerprovisjon + fast årsavgift. *Ikke* Nord Pool-/eSett-gebyrer (viderefakturerte, ikke KAIAs inntekt) |
| Rapportperiode | = importperioden (én import = ett anlegg, én måned i dagens modell) |
| Meglerprovisjon-kilde | `ParsedSettlement.Summary.MeglerprovisjonNok`, persisteres på `SettlementImportRecord` |
| Fast avgift | Ny `KaiaAnnualFeeNok` på `PlantRegistration`, default 4 000, per anlegg |
| Pro-rata | Dagbasert: `årsavgift × dager i periode / dager i året` |
| Fortegn | Kilden er negativ; snus til positiv kostnad i `KaiaCostQueryService` |
| Antall anlegg | 11 (Vikeså, Stølskraft, Løgjen, Drivdal, Grødemfoss, Haukland, Honnefoss, Lindland, Øgreyfoss, Ørsdalen, Liavatn) |

## Antakelser fra brukeren

- Den faste årskostnaden er 4 000 NOK **per anlegg** — bekreft satsen før
  produksjon. Lagres per anlegg slik at enkeltanlegg kan avvike.
- Stølskraft har 0,00 meglerprovisjon i jan/feb 2026 og Ørsdalen 0,00 i januar.
  Verifiser om dette er reelt eller importfeil før tallene brukes som fasit.

## Implementasjons-rekkefølge (fra spec)

| Steg | Innhold |
|---|---|
| 1 | `KaiaAnnualFeeNok` på `PlantRegistration` + migrering |
| 2 | `MeglerprovisjonNok` på `SettlementImportRecord` + migrering + populering i `ParseSettlementJobHandler` |
| 3 | `KaiaCostResult` DTO + `IKaiaCostQueryService` |
| 4 | `KaiaCostQueryService` + ren pro-rata-funksjon + unit-tester |
| 5 | API-endepunkt + contract |
| 6 | KPI-kort på `ReportDetail.razor` |
| 7 | Porteføljerollup på `Portefolje.razor` |
| 8 | Reimport av eksisterende settlement-filer (backfill `meglerprovisjon_nok`) |
| 9 | Verifiser mot validerings-fasit i specen |

## Verifikasjon

Etter implementasjon skal disse stemme (meglerprovisjon som positiv kostnad):

- Portefølje jan 2026: **7 965,80 NOK** · mar 2026: **12 918,36 NOK**
- Vikeså jan 2026: megler 495,10 + fast 339,73 = **834,83 NOK**
- Sum jan–apr 2026 (4 hele mnd): **35 731,12 NOK**

## Filer som berøres

- `src/KraftverkUptime.Infrastructure/Persistence/Entities/PlantRegistration.cs` + migrering
- `src/KraftverkUptime.Modules.Settlement/Persistence/SettlementImportRecord.cs` + migrering
- `src/KraftverkUptime.Modules.Settlement/Jobs/ParseSettlementJobHandler.cs`
- `src/KraftverkUptime.Modules.Reporting/` — ny DTO + query service + DI i `ReportingModule.cs`
- `src/KraftverkUptime.Api/` — nytt endepunkt + contract
- `src/KraftverkUptime.Web/Pages/ReportDetail.razor` + `Portefolje.razor`
- Tester i `tests/`
