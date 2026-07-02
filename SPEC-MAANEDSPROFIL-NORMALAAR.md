# Spec: Månedsprofil for normalårsproduksjon

**Status:** Klar til implementasjon
**Dato:** 2026-07-02
**Kilde:** Driftsleders kraftverkoversikt (Excel), fanen med middelproduksjon × månedsfordeling

---

## 1. Bakgrunn og mål

Appen sammenligner i dag faktisk produksjon mot normalår med **flat pro-rata**:
`forventet = NormalGwh × 1000 × dager / dager_i_året`. Det er feil for vannkraft —
produksjonen er sterkt sesongavhengig. Driftsleders ark fordeler middelproduksjonen
per måned med en prosentprofil (sum 100 %):

| Jan | Feb | Mar | Apr | Mai | Jun | Jul | Aug | Sep | Okt | Nov | Des |
|-----|-----|-----|-----|-----|-----|-----|-----|-----|-----|-----|-----|
| 13,1 | 11,0 | 9,1 | 8,1 | 4,7 | 2,3 | 1,3 | 4,9 | 8,7 | 10,4 | 12,4 | 14,0 |

**Mål:** Innfør månedsprofil **per kraftverk** (redigerbar, seedes med profilen over
som default for alle verk), og bruk den i alle normalår-sammenligninger i stedet for
flat pro-rata.

### Berørte visninger i dag

Normalår-sammenligning finnes to steder — begge bruker flat pro-rata og skal over
på månedsvektet beregning:

1. **Produksjon-siden** (`Produksjon.razor` ~linje 206–218 + `NormalAarsAndel()` ~linje 776):
   KPI-kortet «Faktisk vs normalår».
2. **Portefølje → Sammendrag** (`Portefolje.razor`): kolonnen «Normalår %» per verk
   (`NormalAarsAndel(Row)` ~linje 661) og totalen i KPI-kortet «Produksjon»
   (`NormalAarsAndelTotal()` ~linje 746).

**Skal IKKE endres:** `VaktRoi.razor` og `EconomyReportQueryService` bruker
`NormalAarsproduksjonGwh` kun til kostnadsfordeling etter GWh-andel av porteføljen
(tidsuavhengig). Månedsrapport/ReportDetail viser faktisk produksjon per måned uten
normalår-kobling — oppfølging, se §9.

---

## 2. Datamodell

`PlantRegistration` (Infrastructure/Persistence/Entities) får nytt felt:

```csharp
/// <summary>
/// Månedsfordeling av normalårsproduksjonen i prosent, indeks 0 = januar.
/// 12 verdier som summerer til 100. Null = ikke satt → forbrukskoden
/// faller tilbake til flat fordeling (dagbasert pro-rata).
/// Redigeres i PlantAdmin; seedes med felles Dalane-profil.
/// </summary>
public double[]? MaanedsprofilProsent { get; set; }
```

- Lagres som `jsonb` i Postgres (EF value converter + value comparer, kolonnenavn
  `maanedsprofil_prosent`). Egen tabell er unødvendig — profilen leses alltid
  sammen med anlegget.
- Ny EF-migrering (se `Generer migrasjoner.bat`).
- Validering ved lagring (API-nivå): nøyaktig 12 verdier, alle ≥ 0,
  sum = 100 ± 0,1. Ellers 400 med feilmelding.

---

## 3. Seeding

Ny `MaanedsprofilSeeder` etter mønster fra `NormalAarsproduksjonSeeder`
(idempotent — kun rader der kolonnen er `NULL` oppdateres, manuelt redigerte
verdier beholdes). Kalles fra samme sted i oppstarten som eksisterende seeder.

Default-profil (felles for alle 11 verk inntil driftsleder differensierer):

```csharp
private static readonly double[] DefaultProfil =
    [13.1, 11.0, 9.1, 8.1, 4.7, 2.3, 1.3, 4.9, 8.7, 10.4, 12.4, 14.0];
```

---

## 4. Beregningslogikk — delt kalkulator

Ny statisk klasse i **Core** (slik at både Web og fremtidige API-tjenester bruker
samme logikk; i dag ligger beregningen duplisert client-side i to razor-filer):

```csharp
namespace KraftverkUptime.Core.Domain;

public static class ForventetProduksjonKalkulator
{
    /// <summary>
    /// Forventet produksjon i MWh for perioden [from, to) gitt normalår (GWh)
    /// og månedsprofil. Delmåneder vektes dagbasert innenfor måneden.
    /// profil = null → flat fordeling (dagens pro-rata-oppførsel).
    /// </summary>
    public static double? ForventetMwh(
        double? normalGwh, double[]? profil, DateTime from, DateTime to);
}
```

Algoritme:

1. Returner `null` hvis `normalGwh` mangler/≤ 0 eller `to <= from`.
2. Iterér kalendermånedene perioden berører. For hver måned m i år y:
   `dekkedeDager = overlapp([from,to), måned m)` i dager,
   `forventet += normalGwh × 1000 × (profil[m-1]/100) × dekkedeDager / dagerIMåned(m, y)`.
3. Uten profil: behold dagens formel `normalGwh × 1000 × dager / dagerIAaret`
   (skuddår-håndtering som i dag).

Kontrollregning mot arket (Drivdal, 8 055 MWh middelproduksjon):
- Januar alene: 8 055 × 13,1 % = 1 055 MWh ✓ (arket viser 1 056, avrunding)
- Jan–mai akkumulert («Per Mai»): 13,1+11,0+9,1+8,1+4,7 = 46,0 % → 3 705 MWh ✓ (arket: 3 707)
- Helår: sum 100 % → 8 055 MWh ✓

Perioder som krysser årsskiftet skal fungere (profilen gjentas per år).

---

## 5. API

`PlantsEndpoints` (Api/Endpoints):

- `GET /api/v1/plants` og `GET /api/v1/plants/{plantId}`: legg `MaanedsprofilProsent`
  i respons-DTO-ene (samme steder som `NormalAarsproduksjonGwh` eksponeres i dag).
- `PUT /api/v1/plants/{plantId}` / `UpdatePlantRequest`: nytt felt
  `double[]? MaanedsprofilProsent` med valideringen fra §2.
- Web-siden: oppdater `ReportsApi.cs`-DTO-ene (og ev. `EconomyApi.cs` hvis
  plant-DTO deles) tilsvarende.

---

## 6. PlantAdmin (redigering per verk)

I `PlantAdmin.razor`, ved siden av feltet for normalårsproduksjon:

- 12 numeriske felt (Jan–Des), prosent med én desimal, norsk tallformat.
- Løpende sum vises under feltene: grønn ved 100 ± 0,1, rød ellers, med
  differansen («Sum 98,7 % — mangler 1,3 %»).
- Knapp **«Normaliser til 100 %»** som skalerer alle verdier proporsjonalt.
- Knapp **«Bruk standardprofil»** som fyller inn default-profilen fra §3.
- Lagre-knappen deaktiveres når summen er utenfor toleranse.

---

## 7. UI-endringer i visningene

### 7a. Produksjon-siden

`NormalAarsAndel()` erstattes med kall til `ForventetProduksjonKalkulator.ForventetMwh(...)`
(profilen hentes fra `_plant`, som allerede lastes for normalår-KPI-en).

Tekstjusteringer på KPI-kortet «Faktisk vs normalår»:
- SubText: `"{GWh:F1} GWh i et normalår · månedsvektet"` (var «pro-rata på perioden»).
  Fallback uten profil: behold «pro-rata på perioden».
- InfoText: forklar at forventet produksjon vektes etter verkets månedsprofil
  (hydrologisk sesong), og at profilen redigeres i PlantAdmin.

### 7b. Portefølje → Sammendrag

- `Row`-recorden får `double[]? MaanedsprofilProsent` (mappes fra plant-DTO, samme
  sted som `NormalAarsproduksjonGwh` i dag, ~linje 616).
- `NormalAarsAndel(Row)` og `NormalAarsAndelTotal()` bruker kalkulatoren.
  Totalen: summer forventet MWh per verk (hver med sin profil), del faktisk sum på
  forventet sum — ikke gjennomsnitt av andeler.
- Tooltip på «Normalår %»-kolonnen oppdateres: «…månedsvektet etter verkets
  produksjonsprofil. Faller tilbake til flat pro-rata hvis profil ikke er satt.»

---

## 8. Tester

`KraftverkUptime.Core.Tests` — `ForventetProduksjonKalkulatorTests`:

1. Helår med profil → nøyaktig `normalGwh × 1000`.
2. Januar alene med default-profil → 13,1 % av årsproduksjonen (Drivdal-fasit: 1 055 MWh).
3. Jan–mai → 46,0 % (Per Mai-kolonnen i arket).
4. Delmåned (1.–15. mai) → 4,7 % × 14/31 (dagvekting; [from,to) = 14 dager) — alternativt inklusiv to-dato hvis eksisterende filter-semantikk er inklusiv; **følg samme konvensjon som dagens `NormalAarsAndel()`** slik at tallene er sammenlignbare før/etter.
5. Periode over årsskiftet (des–jan).
6. `profil = null` → identisk med dagens flat pro-rata, inkl. skuddår.
7. Validering: 11 verdier, negativ verdi, sum ≠ 100 → avvist.

I tillegg: manuell kontroll mot arket for alle 9 verk (sum-raden: 181 882 MWh
totalt, 83 697 MWh per mai).

---

## 9. Ikke i scope (foreslåtte oppfølginger)

- **Forventet vs faktisk per måned i Månedsrapport/ReportDetail** — tabell som i
  driftsleders ark (verk × måned, forventet, faktisk, avvik). Naturlig neste steg
  når profilen finnes i datamodellen.
- Per-verk-profiler utledet fra produksjonshistorikk (i dag: manuell input).
- Kostnadsfordeling (VaktRoi/Økonomi) — fortsatt ren GWh-andel, tidsuavhengig.

---

## 10. Merknader til implementasjon

- Merk avvik i middelproduksjon: arket bruker f.eks. Drivdal 8 055 MWh og
  Haukland 19 733 MWh, mens seedede verdier er 8,0 og 21,0 GWh. Denne speken
  endrer **ikke** `NormalAarsproduksjonGwh`-verdiene — driftsleder justerer
  ev. selv i PlantAdmin. Kalkulatoren bruker det som står i databasen.
- Profilverdiene i arket gjelder porteføljen samlet; per-verk-redigering er
  bevisst valgt for å kunne differensiere senere (ulik hydrologi per felt).
