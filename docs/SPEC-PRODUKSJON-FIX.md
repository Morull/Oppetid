# Spec: Produksjons-modul — fix og forbedringer

**Status:** Klar til implementasjon (2026-04-30)
**Estimat:** 3-5 timer
**Bakgrunn:** Audit av drifts-leder + bruker 2026-04-30 — beregningene er korrekte, men UI-tooltips og manglende test-dekning skaper risiko for misbruk og regresjon.
**Avhengighet:** Worktree `beautiful-elbakyan-dd576d` må merges til main før fixen kjøres.

## Bakgrunn

Drifts-leder så Drivdal feb-2026 og spurte: *"Når vi har produsert i så få timer, burde vi kun ha produsert i pristoppene. Er beregningene riktige?"*

Audit avdekket at:

1. **Beregningene i `ProduksjonAnalyseCalculator` er matematisk korrekte og matcher det de heter i koden.**
2. **UI-en er misvisende på to punkter:**
   - Plan-treff-subtext sier "Σ avvik / Σ plan", men den faktiske formelen er `1 − Σ|avvik|/Σplan`
   - "Topp-prisperiode-utnyttelse" er ikke åpenbart for leseren at det måler **MWh-volum-andel**, ikke **drifts-time-andel**. Drifts-leder leste den som det andre.
3. **Ingen tester finnes for `ProduksjonAnalyseCalculator`.** Risikabel — neste regresjon vil ikke fanges.
4. **`andelBunn` beregnes men brukes ikke i UI.** Enten dødt felt eller manglende eksponering.
5. **Filene ligger i worktree `beautiful-elbakyan-dd576d`** — ikke merget til main. Hele modulen er live-deployed fra worktree, hvilket er imot prosjektets normale arbeidsflyt.

## Beslutning

Tre konkrete tiltak:

| Tiltak | Estimat | Begrunnelse |
|---|---|---|
| **A. Merge worktree til main** | 30 min | Modulen er i drift, må følge samme arbeidsflyt som resten av kodebasen |
| **B. UI-tekst-forbedringer + ny komplementær KPI** | 1-2 t | Forhindrer fremtidige tolkningsfeil, hjelper drifts-leder forstå hva tallet betyr |
| **C. Komplett unit-test-suite for calculator** | 2 t | Beskytter mot regresjon. Brukerens scenario blir én av test-casene. |

## Tiltak A: Merge worktree

```powershell
cd C:\Morten\00 Oppetid
git fetch origin
git checkout main
git merge beautiful-elbakyan-dd576d
git push origin main
git worktree remove .claude\worktrees\beautiful-elbakyan-dd576d
git branch -d beautiful-elbakyan-dd576d
```

Etter merge: alle filer som lå i worktree skal finnes på `C:\Morten\00 Oppetid\src\`-stiene. Verifisering:

```powershell
Test-Path "C:\Morten\00 Oppetid\src\KraftverkUptime.Modules.Reporting\Produksjon\ProduksjonAnalyseCalculator.cs"
# Forventet: True
```

Påfølgende endringer i denne specen referer til main-stier, ikke worktree.

## Tiltak B: UI-tekst-forbedringer + komplementær KPI

### B1. Ny KPI-felt: `AndelTimerProduksjonIToppKvartil`

Drifts-leders intuisjon ("114 av 672 timer burde være topp-pris") tilsvarer en KPI vi ikke har: **andel av drifts-timer som faller i øverste 25 % spot-vindu**. Dette er komplementært til den eksisterende `AndelProdIToppKvartil` (som måler MWh-volum-andel).

**Fil:** `src/KraftverkUptime.Modules.Reporting/Produksjon/IProduksjonAnalyseService.cs`

Utvid `ProduksjonAnalyseResult` og `ProduksjonMonthly` med to nye felt:

```csharp
double AndelTimerProdIToppKvartil,    // antall prod-timer i topp-25% / antall prod-timer totalt
double AndelTimerProdIBunnKvartil,    // antall prod-timer i bunn-25% / antall prod-timer totalt
```

**Fil:** `src/KraftverkUptime.Modules.Reporting/Produksjon/ProduksjonAnalyseCalculator.cs`

I `ComputeAggregate`, etter eksisterende kvartil-beregning (rundt linje 134):

```csharp
// Tids-andel (komplementær til volum-andel) — drifts-leders intuisjon:
// "av timene vi kjørte, hvor mange falt i topp-pris-vinduet?"
var antTimerProdMedSpot = medSpot.Count(r => (r.ElhubMwh ?? 0) > 0);
double andelTimerTopp = 0, andelTimerBunn = 0;
if (antTimerProdMedSpot > 0)
{
    var antProdITopp = toppTimer.Count(r => (r.ElhubMwh ?? 0) > 0);
    var antProdIBunn = bunnTimer.Count(r => (r.ElhubMwh ?? 0) > 0);
    andelTimerTopp = (double)antProdITopp / antTimerProdMedSpot;
    andelTimerBunn = (double)antProdIBunn / antTimerProdMedSpot;
}
```

Returner i tuple og ProduksjonAnalyseResult/ProduksjonMonthly.

### B2. UI-tekst på Produksjon.razor

**Fil:** `src/KraftverkUptime.Web/Pages/Produksjon.razor`

Endre subtext på Plan-treff-card (linje 68):

```diff
- SubText="@($"Σ avvik / Σ plan over {_data.AntallTimerMedPlan} timer")"
+ SubText="@($"1 − MAE/Σplan over {_data.AntallTimerMedPlan} timer med plan > 0")"
```

Endre subtext og info-tooltip på Topp-prisperiode-utnyttelse-card (linje 71-78):

```diff
  <KpiTrendCard Label="Topp-prisperiode-utnyttelse"
                Value="@(_data.AndelProdIToppKvartil * 100)"
                PreviousValue="@(_previous?.AndelProdIToppKvartil * 100)"
                Format="F1" Unit="%"
-               SubText="@TimingTolkning(_data.AndelProdIToppKvartil)"
+               SubText="@($"MWh-andel i øverste 25 % spot-timer ({(_data.AndelTimerProdIToppKvartil * 100):F0} % av drifts-timer). Naiv baseline = 25 %.")"
                HigherIsBetter="true" />
```

Oppdater `TimingTolkning`-metoden (kode-blokk i samme fil) til å være tydelig på hva tallet betyr:

```csharp
private string TimingTolkning(double andelTopp) =>
    andelTopp switch
    {
        >= 0.40 => "Sterkt — produksjonen veies tungt i topp-pris-timer",
        >= 0.30 => "Bra — produksjonen samsvarer godt med høye priser",
        >= 0.25 => "Nøytralt — jevn fordeling (baseline = 25 %)",
        >= 0.15 => "Svak — produksjonen vektes mot lavere priser",
        _ => "Dårlig — produserte hovedsakelig i lavpris-perioder"
    };
```

Oppdater `HelhetligTolkning()` til å forklare når `AndelProdIToppKvartil < 0.25` at det betyr **dårlig timing eller tvungen produksjon (overløp/minstevannføring)** — ikke bare en lav score:

```csharp
private string HelhetligTolkning()
{
    var planTreffStr = _data!.PlanTreffProsent >= 0.85 ? "godt på plan" :
                       _data.PlanTreffProsent >= 0.65 ? "moderate avvik fra planen" :
                       "store avvik fra planen";
    var timingStr = _data.AndelProdIToppKvartil >= 0.30 ? "Hydrogrids timing er sterk"
                  : _data.AndelProdIToppKvartil >= 0.25 ? "Hydrogrids timing er nøytral"
                  : "Hydrogrids timing er svak — produserer mest når prisen er lav. Sjekk om dette skyldes overløpsrisiko eller minstevannføring.";
    var merverdiStr = _data.HydrogridMerverdiNok >= 0
        ? $"Hydrogrid-merverdien er positiv ({_data.HydrogridMerverdiNok:N0} NOK)."
        : $"Hydrogrid-merverdien er negativ ({_data.HydrogridMerverdiNok:N0} NOK) — planen plasserte volum under snittpris.";
    return $"Det er {planTreffStr}. {timingStr} {merverdiStr}";
}
```

### B3. Eksponer `andelBunn` eller fjern det

Velg ett:

- **Alternativ a (anbefalt):** ekspander tabellen "Måneds-trend" med en kolonne "Bunn-pris-andel" som viser `AndelProdIBunnKvartil`. Hvis bunn > topp er det et tydelig drifts-flagg.
- **Alternativ b:** fjern `andelBunn`-beregningen fra calculator hvis det aldri skal brukes.

## Tiltak C: Unit-test-suite

**Fil:** `tests/KraftverkUptime.Infrastructure.Tests/Produksjon/ProduksjonAnalyseCalculatorTests.cs` NY

Minst 12 tester. Hver test bruker en hardkodet liste av `HourlyInput`-rader og verifiserer eksakte forventede verdier (med `Assert.Equal(forventet, faktisk, precision: 4)`).

Test-katalog:

| # | Navn | Scenario | Forventet |
|---|---|---|---|
| 1 | `Compute_TomtInput_ReturnererNullKpi` | `hours = []` | Alle felt = 0, ingen exception |
| 2 | `Compute_KunPlan_NoElhub_PlanTreffNull` | 24 timer plan, 0 produksjon | PlanTreff = 0, AntallTimerProduksjon = 0 |
| 3 | `Compute_PerfektTreff_PlanLikElhub` | 24 timer hvor ElhubMwh = PlanMwh | PlanTreffProsent = 1.0 |
| 4 | `Compute_AlleTimerIToppKvartil_AndelTopp100` | Konstant Plan i 24 timer, alle Elhub-timer i top-6 etter spot | AndelProdIToppKvartil = 1.0, AndelTimerProdIToppKvartil = 1.0 |
| 5 | `Compute_DrifsLederScenario_LavOverlap` | 672 timer feb-26, 114 produksjons-timer fordelt slik at MWh-andel i top-168 = 14.7 % | AndelProdIToppKvartil ≈ 0.147; AndelTimerProdIToppKvartil måles og rapporteres |
| 6 | `Compute_KortPeriode_FireTimer_KvartilSize1` | 4 timer, ulike spot-priser | KvartilSize = 1 (Math.Max-grenen) |
| 7 | `Compute_NegativSpotpris_HandteresKorrekt` | 24 timer der noen spot < 0 | Sortering korrekt, hgMerverdi konsistent fortegn |
| 8 | `Compute_ManglendeSpot_TimerEkskluderes` | 24 timer hvor 12 mangler spot | Calculator bruker bare timer med spot for kvartil og merverdi; PlanTreff regnes med alle plan>0-timer |
| 9 | `Compute_FlatSpotpris_AlleTimerLikePris_MerverdiNull` | 24 timer med spot = 1000 | hgMerverdi ≈ 0, faktiskMerverdi ≈ 0 (ingen timing-effekt mulig) |
| 10 | `Compute_OptimalTiming_AllProdIHoyestePris_HgMerverdiPositiv` | All Plan-volum i topp-6 av 24 timer | hgMerverdi > 0, AndelProdIToppKvartil ≈ 1.0 |
| 11 | `Compute_DarligTiming_AllProdILavestePris_HgMerverdiNegativ` | All Plan-volum i bunn-6 av 24 timer | hgMerverdi < 0, AndelProdIBunnKvartil ≈ 1.0 |
| 12 | `Compute_KrysserManedsskifte_GirToMonthly` | 48 timer 28-29.feb 2026 | `Monthly.Count = 1` (begge i feb), `Monthly.Count = 2` hvis krysser ekte månedsskifte |
| 13 | `Compute_DrivdalFasit_Feb2026` *(integration-style)* | Last `UptimeReport`-blob for Drivdal feb-2026 | PlanTreff ≈ 0.658, AndelProdIToppKvartil ≈ 0.147, HgMerverdi ≈ -21697, FaktiskMerverdi ≈ -20249 (alle ±2 %) |

Test 13 låser fasit slik at fremtidige refaktoreringer ikke endrer tolkningen av live data uten advarsel. Krever testdata-fil eller mock som returnerer feb-2026 settlement-rader for Drivdal.

## Akseptansekriterier

1. `dotnet build` + `dotnet test` grønt — minst 12 nye tester passerer
2. Worktree `beautiful-elbakyan-dd576d` er fjernet, alle filer på main
3. `Produksjon.razor` viser ny subtext på begge KPI-kort (verifiseres manuelt)
4. `GET /api/v1/plants/drivdal/produksjon-analyse?from=2026-02-01&to=2026-03-01` returnerer:
   - `andelTimerProdIToppKvartil` som nytt felt
   - `andelTimerProdIBunnKvartil` som nytt felt
   - Eksisterende felt uendret
5. Drivdal feb-2026 etter fix: tallene som vises i UI er **identiske** med pre-fix-tallene (65,8 / 14,7 / -21 697 / -20 249). Kun tooltips og ny tilleggs-info endres.
6. Tooltipen på "Topp-prisperiode-utnyttelse" inneholder begge tall — MWh-andel og time-andel — slik at fremtidige drifts-ledere ikke gjør samme misforståelse.

## Verifikasjon

```powershell
# 1. Verifiser merge
Test-Path "C:\Morten\00 Oppetid\src\KraftverkUptime.Modules.Reporting\Produksjon\ProduksjonAnalyseCalculator.cs"
git -C "C:\Morten\00 Oppetid" worktree list
# Forventet: kun main-worktree

# 2. Kjør nye tester
cd C:\Morten\00 Oppetid
dotnet test tests\KraftverkUptime.Infrastructure.Tests --filter "FullyQualifiedName~ProduksjonAnalyseCalculator"
# Forventet: 12+ tester passerer

# 3. Sjekk API-respons
curl.exe "http://localhost:5080/api/v1/plants/drivdal/produksjon-analyse?from=2026-02-01T00:00:00Z&to=2026-03-01T00:00:00Z" | ConvertFrom-Json | Format-List andelProdIToppKvartil, andelTimerProdIToppKvartil, planTreffProsent, hydrogridMerverdiNok, faktiskMerverdiNok
# Forventet (Drivdal feb-2026):
# andelProdIToppKvartil      ≈ 0.147
# andelTimerProdIToppKvartil ≈ (verifiseres mot data, sannsynligvis lignende)
# planTreffProsent           ≈ 0.658
# hydrogridMerverdiNok       ≈ -21697
# faktiskMerverdiNok         ≈ -20249

# 4. UI-test
# Åpne http://localhost:5180/produksjon/drivdal med periode 2026-02
# Verifiser at tooltip på "Topp-prisperiode-utnyttelse" inneholder begge tall
```

## Antakelser

1. **Calculator-logikken endres ikke** — kun ekstra felt legges til. Eksisterende KPI-tall forblir identiske.
2. **Worktree merger uten konflikt.** Hvis det er hangle endringer på main siden worktree ble laget, må en branch-merge gjøres først. Dette er trivielt og kan håndteres i implementasjonen.
3. **Det finnes ingen produksjons-data tilgjengelig som fasit for testene utover Drivdal feb-2026.** Hvis fasit-data ikke kan reproduseres pålitelig, kan test 13 deferres til senere uten å blokkere de andre testene.

## Ut-av-scope

- Ekstra KPI-er utover de to nye time-andel-feltene (CapturePrice for plan, plan-justert capture rate, osv.) — egen oppfølging hvis ønsket
- Endring av kvartil-størrelse (25 % er konvensjonelt og skal beholdes)
- Visualisering av topp-/bunn-vinduer som markerte områder i Plan-vs-faktisk-grafen — egen feature hvis ønsket
- Kobling mot vakt-ROI-overløp — produksjon i bunn-kvartil med høy `AndelTimerProdIBunnKvartil` indikerer overløpsrisiko, men korreasjonen krever egen analyse

## Referanser

- Audit-konversasjon 2026-04-30 (Cowork)
- Worktree-fil: `.claude/worktrees/beautiful-elbakyan-dd576d/src/KraftverkUptime.Modules.Reporting/Produksjon/ProduksjonAnalyseCalculator.cs`
- UI-fil: `.claude/worktrees/beautiful-elbakyan-dd576d/src/KraftverkUptime.Web/Pages/Produksjon.razor`
- Drivdal feb-2026 KPI-screenshot (Cowork-samtale)
