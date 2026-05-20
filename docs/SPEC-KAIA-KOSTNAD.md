# Spec: KAIA-kostnad per rapportperiode

**Status:** Klar til implementasjon (2026-05-20)
**Estimat:** 1–2 dager
**Avhengighet:** Ingen eksterne — all data finnes allerede i importerte settlement-oppgjør
**Forutsetning:** Settlement-import kjører som i dag; `SettlementSummaryRow.MeglerprovisjonNok` parses allerede korrekt

## Bakgrunn

Drifts-leder spurte: *"Hvor mye koster KAIA oss totalt sett?"* I dag har vi
ingen samlet visning av hva forvaltningen via KAIA koster — tallet ligger spredt
i hver månedseksport.

KAIA fakturerer oss på to måter:

1. **Meglerprovisjon** — KAIAs egen provisjon på handlene de utfører for oss
   (spot + regulerkraft for å dekke ubalanser). Dette er allerede en kolonne i
   oppgjørs-Excelen: `Meglerprovisjon` på både Summering-fanen og verk-fanen.
2. **Fast årskostnad** — 4 000 NOK per anlegg per år. Faktureres separat, finnes
   *ikke* i oppgjørs-eksporten og må legges inn som konfigurasjon.

> **Avgrensning bekreftet av bruker (2026-05-20):** Kun *meglerprovisjon* regnes
> som "KAIA-kostnad" i transaksjonene. De øvrige gebyrkolonnene i oppgjøret
> — `Nord Pool gebyr`, `eSett volumgebyr`, `eSett ubalansegebyr` — er
> viderefakturerte kostnader fra Nord Pool og eSett, ikke KAIAs inntekt, og
> holdes utenfor denne funksjonen.

Funksjonen skal vise KAIA-kostnaden **for rapportperioden som velges** i appen.
I dagens datamodell er én rapport = én settlement-import = én kalendermåned for
ett anlegg. Rapportperioden er derfor importens periode
(`PeriodStartUtc`–`PeriodEndUtc`).

## Beslutning

Bygg en `IKaiaCostQueryService` i Reporting-modulen som for et gitt anlegg og en
gitt periode returnerer tre tall:

| Felt | Kilde | Beregning |
|---|---|---|
| `MeglerprovisjonNok` | Settlement-import | Periodens totale meglerprovisjon, som **positiv kostnad** |
| `FastAvgiftNok` | `PlantRegistration.KaiaAnnualFeeNok` | Årskostnad pro-rata på periodens lengde |
| `TotalNok` | — | `MeglerprovisjonNok + FastAvgiftNok` |

KAIA-kostnaden eksponeres to steder i UI:

- **Per rapport** — et nytt KPI-kort på `ReportDetail.razor` ("KAIA-kostnad").
- **Per portefølje** — en sumrad/kort på `Portefolje.razor` som summerer alle
  anlegg for perioden valgt i `FilterState`.

### Fortegn

`Meglerprovisjon` lagres **negativt** i oppgjøret (det er et fradrag i
`Oppgjør`-kolonnen). Funksjonen skal presentere kostnaden som **positivt tall**:
`kostnad = -summary.MeglerprovisjonNok`. Dette gjøres ett sted —
`KaiaCostQueryService` — slik at API og UI alltid får positivt fortegn.

### Pro-rata av fast årskostnad

```
fastAvgift = KaiaAnnualFeeNok * (antallDagerIPerioden / antallDagerIÅret)
antallDagerIPerioden = (PeriodEndUtc.Date - PeriodStartUtc.Date).Days   // se fallgruve
antallDagerIÅret     = DateTime.IsLeapYear(periodeStart.Year) ? 366 : 365
```

Dette gir at tolv hele månedseksporter summerer til nøyaktig `KaiaAnnualFeeNok`.
For en delvis måned (f.eks. mai 1.–17.) blir avgiften forholdsmessig lavere.

Alternativ vurdert: flat `KaiaAnnualFeeNok / 12` per måned. Forkastet fordi
rapportperioden kan være en delvis måned — dagbasert pro-rata håndterer begge.

## Datamodell

### Endring 1 — `PlantRegistration` (ny kolonne)

Fil: `src/KraftverkUptime.Infrastructure/Persistence/Entities/PlantRegistration.cs`

```csharp
/// <summary>
/// KAIAs faste årsavgift for forvaltning av dette anlegget, i NOK.
/// Default 4 000 (avtalt sats per 2026). Per anlegg slik at enkeltanlegg
/// kan ha avvikende sats uten kodeendring.
/// </summary>
public double KaiaAnnualFeeNok { get; set; } = 4000;
```

Krever EF-migrering — kolonne `kaia_annual_fee_nok` på `core.plants`, default 4000
slik at eksisterende rader får riktig verdi uten backfill-skript.

### Endring 2 — `SettlementImportRecord` (ny kolonne)

Fil: `src/KraftverkUptime.Modules.Settlement/Persistence/SettlementImportRecord.cs`

I dag lagrer import-raden kun metadata; finansielle tall ligger inni bloben.
Å lese og deserialisere bloben for hver rapportvisning er unødvendig når
rapportperioden alltid er lik importperioden. Persister derfor periodens
meglerprovisjon på import-raden:

```csharp
/// <summary>
/// Periodens totale meglerprovisjon i NOK, hentet fra Summering-fanen
/// (<c>ParsedSettlement.Summary.MeglerprovisjonNok</c>). Negativ i kilden
/// (fradrag i oppgjøret); lagres som-er og snus til positiv kostnad i
/// query-laget. Null hvis Summering-fanen manglet i eksporten.
/// </summary>
public double? MeglerprovisjonNok { get; init; }
```

Krever EF-migrering — nullbar kolonne `meglerprovisjon_nok` på
`core.settlement_imports`.

Populeres i `ParseSettlementJobHandler.HandleAsync` ved bygging av
`SettlementImportRecord`:

```csharp
MeglerprovisjonNok = parsed.Summary?.MeglerprovisjonNok,
```

> **Backfill:** Eksisterende import-rader får `null` her. Enten kjør en
> engangs-reimport (filene ligger i `CSV Eksporter/done/` og duplikat-mappen),
> eller la `KaiaCostQueryService` falle tilbake til blob-lesing når kolonnen er
> null (se Fallgruver). Anbefalt: reimport — enklest og gir konsistente data.

## Beregningskontrakt

Ny DTO i `KraftverkUptime.Modules.Reporting`:

```csharp
public sealed record KaiaCostResult
{
    public required string PlantId { get; init; }
    public required string PlantName { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }

    /// <summary>Meglerprovisjon som positiv kostnad. Null hvis ukjent.</summary>
    public double? MeglerprovisjonNok { get; init; }

    /// <summary>Fast årsavgift pro-rata periodens lengde.</summary>
    public double FastAvgiftNok { get; init; }

    /// <summary>MeglerprovisjonNok + FastAvgiftNok. Null hvis meglerprovisjon ukjent.</summary>
    public double? TotalNok { get; init; }

    /// <summary>Datakvalitets-merknad, f.eks. "Meglerprovisjon mangler i eksport".</summary>
    public string? Note { get; init; }
}
```

`IKaiaCostQueryService`:

```csharp
public interface IKaiaCostQueryService
{
    /// <summary>KAIA-kostnad for én import (= ett anlegg, én periode).</summary>
    Task<KaiaCostResult> GetForImportAsync(
        string plantId, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// KAIA-kostnad for alle anlegg i porteføljen, for importer som dekker
    /// [fromUtc, toUtc]. Brukes av portefølje-rollupen.
    /// </summary>
    Task<IReadOnlyList<KaiaCostResult>> GetForPortfolioAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
```

Registreres i `ReportingModule.RegisterServices`:

```csharp
services.AddScoped<IKaiaCostQueryService, KaiaCostQueryService>();
```

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `KaiaAnnualFeeNok` på `PlantRegistration` + EF-migrering | 1 t |
| 2 | `MeglerprovisjonNok` på `SettlementImportRecord` + EF-migrering + populering i `ParseSettlementJobHandler` | 1–2 t |
| 3 | `KaiaCostResult` DTO + `IKaiaCostQueryService` | 1 t |
| 4 | `KaiaCostQueryService` (pro-rata-funksjon ren og isolert) + unit-tester | 3 t |
| 5 | API-endepunkt + contract (følg mønster fra Nedetid/Effektivitet) | 1 t |
| 6 | KPI-kort "KAIA-kostnad" på `ReportDetail.razor` | 1–2 t |
| 7 | Portefølje-rollup på `Portefolje.razor` | 2 t |
| 8 | Reimport av eksisterende settlement-filer (backfill) | 0,5 t |
| 9 | Verifiser mot fasit-tabellen under | 0,5 t |

Pro-rata-beregningen i steg 4 skal være en ren, statisk funksjon
(`KaiaFeeProration.Compute(annualFee, periodStart, periodEnd)`) med egne
unit-tester — den er lett å teste isolert og er kjernen i forretningslogikken.

## Validerings-fasit (faktiske tall fra importerte filer)

Regnet ut fra oppgjørsfilene i `CSV Eksporter/` 2026-05-20. Meglerprovisjon
vist som **positiv kostnad** (NOK). Bruk disse i regresjonstest.

### Portefølje per måned

| Periode | Meglerprovisjon (NOK) | Merknad |
|---|---:|---|
| Jan 2026 (01.–31.) | 7 965,80 | komplett |
| Feb 2026 (01.–28.) | 3 956,32 | komplett |
| Mar 2026 (01.–31.) | 12 918,36 | komplett |
| Apr 2026 (01.–30.) | 10 890,64 | komplett |
| **Sum jan–apr** | **35 731,12** | 4 hele måneder |
| Mai 2026 (01.–17.) | 3 003,72 | delvis — Stølskraft mangler |

### Meglerprovisjon per anlegg, jan–apr 2026 (sum 4 mnd)

| Anlegg | NOK | Anlegg | NOK |
|---|---:|---|---:|
| Øgreyfoss | 10 550,33 | Honnefoss | 2 093,86 |
| Lindland | 8 319,69 | Vikeså | 2 858,17 |
| Haukland | 4 653,68 | Drivdal | 1 370,56 |
| Grødemfoss | 3 523,21 | Ørsdalen | 1 461,39 |
| Løgjen | 335,95 | Liavatn | 392,45 |
| Stølskraft | 211,83 | | |

### Pro-rata fast avgift (eksempel, `KaiaAnnualFeeNok` = 4000)

| Periode | Dager | Fast avgift (NOK) |
|---|---:|---:|
| Jan 2026 | 31 | 4000 × 31/365 = 339,73 |
| Feb 2026 | 28 | 4000 × 28/365 = 306,85 |
| Mai 01.–17. | 17 | 4000 × 17/365 = 186,30 |

Eksempel total: Vikeså jan 2026 = 495,10 (megler) + 339,73 (fast) = **834,83 NOK**.

## Helårsbilde (estimat til drifts-leder)

| Komponent | Beløp | Grunnlag |
|---|---:|---|
| Meglerprovisjon, annualisert | ~107 000 NOK/år | jan–apr-snitt × 12 |
| Fast årskostnad | 44 000 NOK/år | 11 anlegg × 4 000 |
| **Estimert total KAIA-kostnad** | **~151 000 NOK/år** | |

Tre anlegg (Øgreyfoss, Lindland, Haukland) står for ~65 % av meglerprovisjonen.

## Fallgruver

- **Fortegn.** `MeglerprovisjonNok` er negativ i kilden. Snu til positiv kostnad
  ett sted (query-laget). Ikke snu i parseren — det ville bryte
  `Oppgjør = SumSalg + Meglerprovisjon`-summeringen i Settlement-modulen.
- **Manglende Summering-fane.** `ParsedSettlement.Summary` kan være null
  (eldre/avvikende eksport). Da blir `MeglerprovisjonNok` null → vis "ukjent" i
  UI, ikke 0. 0 og null er funksjonelt forskjellige (jf. `SettlementHourlyRow`).
- **Periodelengde i dager.** Importperioden er `[PeriodStartUtc, PeriodEndUtc]`
  i UTC. Bruk `.Date`-differanse + 1 for inklusiv dagtelling, eller regn
  `Math.Round((End-Start).TotalDays)`. Verifiser at en hel januar gir 31, ikke
  30 eller 32. DST gjør at `TotalDays` for mars/oktober ikke er heltall.
- **Stølskraft og Ørsdalen — nullverdier.** Stølskraft har 0,00 meglerprovisjon
  i jan og feb 2026 og kun -0,05 i mars; Ørsdalen har 0,00 i januar. Verifiser
  om dette er reelt (ingen handler / anlegget kom på KAIA senere) eller en
  importfeil før tallene presenteres som fasit. Stølskraft eksporteres i egen
  fil og mangler i mai-multifila.
- **Backfill.** Eksisterende `core.settlement_imports`-rader har
  `meglerprovisjon_nok = null` til reimport er kjørt. Enten reimport (anbefalt),
  eller la `KaiaCostQueryService` falle tilbake til å lese `Summary` fra bloben
  via `IFileStorage` når kolonnen er null.
- **Delvis måned i porteføljerollup.** `GetForPortfolioAsync` må ikke
  dobbelttelle hvis flere importer for samme anlegg overlapper perioden — bruk
  `FindLatestCoveringAsync`-semantikken (nyeste import som dekker perioden).

## Senere utvidelser (ikke v1)

- **Vilkårlig delperiode.** Hvis rapportperioden skal kunne være en del av en
  måned: summer `SettlementHourlyRow.MeglerprovisjonNok` over timene i
  `[from, to]` i stedet for å bruke Summering-totalen. Krever blob-lesing.
- **Historikk-graf.** KAIA-kostnad per måned over tid på portefølje-dashbordet.
- **De øvrige gebyrene.** Hvis drifts-leder senere vil se totale
  transaksjonskostnader (Nord Pool + eSett), er kolonnene allerede parset —
  utvid `KaiaCostResult` med felter; ingen ny datainnhenting trengs.
