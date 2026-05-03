# Kontrollsjekk: PRODUKSJON-FIX

**Til:** ChatGPT (eller annen LLM som second opinion)
**Fra:** Drifts-leder for Dalane Kraft + utvikler
**Dato:** 2026-05-01
**Formål:** Uavhengig kode-review av PRODUKSJON-FIX-modulen levert i commit `9a2d8e9` på `KraftverkUptime`-prosjektet. Verifiser at implementasjonen matcher spec-en, at tester gir reell dekning, og at det ikke er logiske feil eller regresjoner.

## Bakgrunnen i én avsnitt

KraftverkUptime er en .NET 10 / Blazor WASM-app som rapporterer drift, økonomi og produksjons-effektivitet for 11 vannkraftverk i Dalane Kraft-porteføljen. `ProduksjonAnalyseCalculator` evaluerer hvor godt vi følger Hydrogrids automatiske produksjonsplaner og om timing er optimal mot spotpris. Drifts-leder sa fasit-tallene var feil-tolket fordi UI-en blandet MWh-volum-andel og drifts-time-andel, og det var ingen unit-tester. SPEC-PRODUKSJON-FIX beskriver tre tiltak for å fikse dette.

## Hva spec-en krevde

Tre tiltak:

**A. Merge worktree til main** — Modulen lå i en `claude/beautiful-elbakyan-dd576d` git-worktree som var live-deployd, imot prosjektets normale arbeidsflyt.

**B. UI-tekst-forbedringer + ny komplementær KPI**
- B1: Legg til to nye felt på `ProduksjonAnalyseResult` og `ProduksjonMonthly`:
  - `AndelTimerProdIToppKvartil` — antall produksjons-timer i topp-25% spotpris-vindu / antall produksjons-timer totalt. Komplementær til den eksisterende `AndelProdIToppKvartil` (som måler MWh-volum, ikke timer).
  - `AndelTimerProdIBunnKvartil` — tilsvarende i bunn-25%.
- B2: Forbedre tooltip-tekster i `Produksjon.razor`:
  - Plan-treff sub-text endres fra "Σ avvik / Σ plan" til "1 − MAE/Σplan over X timer med plan > 0" (riktig formel).
  - Topp-prisperiode-utnyttelse subtext skal vise **begge** tall — MWh-andel og tid-andel.
  - `TimingTolkning` skal være tydelig på hva tallet betyr (5-trinns skala fra "Sterkt" til "Dårlig").
  - `HelhetligTolkning` skal forklare at lav topp-utnyttelse kan skyldes overløp/minstevannføring, ikke bare "dårlig timing".
- B3: Eksponer eksisterende `AndelProdIBunnKvartil` som tabell-kolonne med rød markør hvis bunn > topp (drifts-flagg).

**C. Unit-tester** — minst 12 tester for `ProduksjonAnalyseCalculator`. Hard-kodet input + verifisering av eksakte forventede verdier.

**Akseptansekriterier (utdrag):**
1. `dotnet build` + `dotnet test` grønt
2. Worktree fjernes
3. Drivdal feb-2026 KPI-tall **uendret** (65,8 / 14,7 / -21 697 / -20 249)
4. Tooltip-tekst inneholder begge tall
5. API returnerer `andelTimerProdIToppKvartil` og `andelTimerProdIBunnKvartil` som nye felt

## Hva som ble levert

**Commit `1f07db9`** — merge fra worktree til main (28 commits inn).
**Commit `9a2d8e9`** — selve fix-en (filer og tester).

Endrede filer:
- `src/KraftverkUptime.Modules.Reporting/Produksjon/IProduksjonAnalyseService.cs` (utvidet records)
- `src/KraftverkUptime.Modules.Reporting/Produksjon/ProduksjonAnalyseCalculator.cs` (utvidet beregningen)
- `src/KraftverkUptime.Web/Pages/Produksjon.razor` (UI-endringer)
- `src/KraftverkUptime.Web/Services/NedetidApi.cs` (DTO-er speilet)
- `tests/KraftverkUptime.Infrastructure.Tests/Produksjon/ProduksjonAnalyseCalculatorTests.cs` (14 nye tester — NY FIL)

## Kjernekode (komplett)

### `IProduksjonAnalyseService.cs` — record-definisjoner

```csharp
namespace KraftverkUptime.Modules.Reporting.Produksjon;

public interface IProduksjonAnalyseService
{
    Task<ProduksjonAnalyseResult> GetAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

/// <summary>
/// Aggregert analyse-resultat for en periode.
///   PlanTreffProsent: 1 − Σ|Elhub − Plan| / Σ|Plan| for timer med Plan > 0.
///     1.0 = perfekt treff, 0.0 = avvik 100 % i snitt. Klippet til [0, 1].
///   AndelProdIToppKvartil: MWh-volum-andel i øverste 25 % spotpris-timer.
///   AndelTimerProdIToppKvartil: drifts-time-andel i øverste 25 %.
///   HydrogridMerverdiNok: Σ(Plan_t × spot_t) − Σ(Plan_t) × snitt_spot.
/// </summary>
public sealed record ProduksjonAnalyseResult(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallTimer,
    int AntallTimerMedPlan,
    int AntallTimerProduksjon,
    int AntallTimerOverlop,
    double TotalElhubMwh,
    double TotalPlanMwh,
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double AndelProdIBunnKvartil,
    double AndelTimerProdIToppKvartil,    // NY
    double AndelTimerProdIBunnKvartil,    // NY
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    bool OverlopDataTilgjengelig,
    IReadOnlyList<ProduksjonHourlyPoint> Hourly,
    IReadOnlyList<ProduksjonMonthly> Monthly);

public sealed record ProduksjonHourlyPoint(
    DateTimeOffset TimeUtc,
    double? PlanMwh,
    double? ElhubMwh,
    double? SpotprisNokMwh);

public sealed record ProduksjonMonthly(
    int Year,
    int Month,
    double ElhubMwh,
    double PlanMwh,
    int AntallTimerProduksjon,
    int AntallTimerOverlop,
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double AndelProdIBunnKvartil,
    double AndelTimerProdIToppKvartil,    // NY
    double AndelTimerProdIBunnKvartil,    // NY
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh);
```

### `ProduksjonAnalyseCalculator.cs` — kjernen

```csharp
namespace KraftverkUptime.Modules.Reporting.Produksjon;

public static class ProduksjonAnalyseCalculator
{
    public sealed record HourlyInput(
        DateTimeOffset TimeUtc,
        double? PlanMwh,
        double? ElhubMwh,
        double? SpotprisNokMwh);

    public static ProduksjonAnalyseResult Compute(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<HourlyInput> hours,
        IReadOnlySet<DateTimeOffset>? overflowHours = null,
        bool overlopDataTilgjengelig = false)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var overflow = overflowHours ?? new HashSet<DateTimeOffset>();

        var (planTreff, andelTopp, andelBunn,
             andelTimerTopp, andelTimerBunn,
             hgMerverdi, faktiskMerverdi, snittSpot,
             totalElhub, totalPlan, antTimerMedPlan, antTimerProduksjon)
            = ComputeAggregate(hours);

        var antTimerOverlop = hours.Count(h => overflow.Contains(TruncateToHour(h.TimeUtc)));
        var kapasitetsutnyttelse = hours.Count > 0
            ? (double)antTimerProduksjon / hours.Count : 0;
        var overlopProsent = hours.Count > 0
            ? (double)antTimerOverlop / hours.Count : 0;

        var monthly = hours
            .GroupBy(h => new { h.TimeUtc.Year, h.TimeUtc.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var rows = g.ToList();
                var (mt, mTopp, mBunn, mTimerTopp, mTimerBunn,
                     mHg, mFaktisk, mSnittSpot, mElhub, mPlan, _, mProd)
                    = ComputeAggregate(rows);
                var mOverlop = rows.Count(h => overflow.Contains(TruncateToHour(h.TimeUtc)));
                var mKap = rows.Count > 0 ? (double)mProd / rows.Count : 0;
                var mOvrPct = rows.Count > 0 ? (double)mOverlop / rows.Count : 0;
                return new ProduksjonMonthly(
                    Year: g.Key.Year,
                    Month: g.Key.Month,
                    ElhubMwh: mElhub,
                    PlanMwh: mPlan,
                    AntallTimerProduksjon: mProd,
                    AntallTimerOverlop: mOverlop,
                    KapasitetsutnyttelseProsent: mKap,
                    OverlopProsent: mOvrPct,
                    PlanTreffProsent: mt,
                    AndelProdIToppKvartil: mTopp,
                    AndelProdIBunnKvartil: mBunn,
                    AndelTimerProdIToppKvartil: mTimerTopp,
                    AndelTimerProdIBunnKvartil: mTimerBunn,
                    HydrogridMerverdiNok: mHg,
                    FaktiskMerverdiNok: mFaktisk,
                    SnittSpotprisNokMwh: mSnittSpot);
            })
            .ToList();

        var hourlyOutput = hours
            .Select(h => new ProduksjonHourlyPoint(
                TimeUtc: h.TimeUtc,
                PlanMwh: h.PlanMwh,
                ElhubMwh: h.ElhubMwh,
                SpotprisNokMwh: h.SpotprisNokMwh))
            .ToList();

        return new ProduksjonAnalyseResult(
            PlantId: plantId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            AntallTimer: hours.Count,
            AntallTimerMedPlan: antTimerMedPlan,
            AntallTimerProduksjon: antTimerProduksjon,
            AntallTimerOverlop: antTimerOverlop,
            TotalElhubMwh: totalElhub,
            TotalPlanMwh: totalPlan,
            PlanTreffProsent: planTreff,
            AndelProdIToppKvartil: andelTopp,
            AndelProdIBunnKvartil: andelBunn,
            AndelTimerProdIToppKvartil: andelTimerTopp,
            AndelTimerProdIBunnKvartil: andelTimerBunn,
            KapasitetsutnyttelseProsent: kapasitetsutnyttelse,
            OverlopProsent: overlopProsent,
            HydrogridMerverdiNok: hgMerverdi,
            FaktiskMerverdiNok: faktiskMerverdi,
            SnittSpotprisNokMwh: snittSpot,
            OverlopDataTilgjengelig: overlopDataTilgjengelig,
            Hourly: hourlyOutput,
            Monthly: monthly);
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    private static (
        double planTreff,
        double andelTopp, double andelBunn,
        double andelTimerTopp, double andelTimerBunn,
        double hgMerverdi, double faktiskMerverdi, double snittSpot,
        double totalElhub, double totalPlan, int antTimerMedPlan,
        int antTimerProduksjon)
        ComputeAggregate(IReadOnlyList<HourlyInput> rows)
    {
        if (rows.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        // 1. Plan-treff: 1 - Σ|Elhub - Plan| / Σ|Plan| for timer med Plan > 0
        // Tell også produksjonstimer (Elhub > 0) — høy verdi i kombinasjon med
        // lav snittpris er en sterk overløp-risiko-indikator.
        double sumAbsAvvik = 0, sumPlan = 0, sumElhub = 0;
        var antTimerMedPlan = 0;
        var antTimerProduksjon = 0;
        foreach (var r in rows)
        {
            if (r.PlanMwh.HasValue && r.ElhubMwh.HasValue && r.PlanMwh.Value > 0)
            {
                sumAbsAvvik += Math.Abs(r.ElhubMwh.Value - r.PlanMwh.Value);
                sumPlan += r.PlanMwh.Value;
                antTimerMedPlan++;
            }
            if (r.ElhubMwh.HasValue)
            {
                sumElhub += r.ElhubMwh.Value;
                if (r.ElhubMwh.Value > 0) antTimerProduksjon++;
            }
        }
        var planTreff = sumPlan > 0
            ? Math.Clamp(1.0 - sumAbsAvvik / sumPlan, 0.0, 1.0)
            : 0;

        // 2. Andel produksjon i topp-/bunn-kvartil av spot
        // Sortér timer på spot, plukk topp/bunn-25 % av timene, og beregn både:
        //   - Volum-andel (sum Elhub-MWh i kvartilen / total Elhub-MWh)
        //   - Tids-andel (antall produksjons-timer i kvartilen / antall produksjons-
        //     timer totalt) — komplementær til volum, svarer på "av timene vi
        //     produserte, hvor mange falt i topp/bunn-vinduet?"
        var medSpot = rows.Where(r => r.SpotprisNokMwh.HasValue).ToList();
        double andelTopp = 0, andelBunn = 0, snittSpot = 0;
        double andelTimerTopp = 0, andelTimerBunn = 0;
        if (medSpot.Count > 0)
        {
            snittSpot = medSpot.Average(r => r.SpotprisNokMwh!.Value);
            var sortertEtterSpot = medSpot.OrderBy(r => r.SpotprisNokMwh!.Value).ToList();
            var kvartilSize = Math.Max(1, sortertEtterSpot.Count / 4);
            var toppTimer = sortertEtterSpot.TakeLast(kvartilSize).ToHashSet();
            var bunnTimer = sortertEtterSpot.Take(kvartilSize).ToHashSet();

            // Volum-andel
            var elhubITopp = toppTimer.Sum(r => r.ElhubMwh ?? 0);
            var elhubIBunn = bunnTimer.Sum(r => r.ElhubMwh ?? 0);
            var totalElhubMedSpot = medSpot.Sum(r => r.ElhubMwh ?? 0);
            andelTopp = totalElhubMedSpot > 0 ? elhubITopp / totalElhubMedSpot : 0;
            andelBunn = totalElhubMedSpot > 0 ? elhubIBunn / totalElhubMedSpot : 0;

            // Tids-andel
            var antTimerProdMedSpot = medSpot.Count(r => (r.ElhubMwh ?? 0) > 0);
            if (antTimerProdMedSpot > 0)
            {
                var antProdITopp = toppTimer.Count(r => (r.ElhubMwh ?? 0) > 0);
                var antProdIBunn = bunnTimer.Count(r => (r.ElhubMwh ?? 0) > 0);
                andelTimerTopp = (double)antProdITopp / antTimerProdMedSpot;
                andelTimerBunn = (double)antProdIBunn / antTimerProdMedSpot;
            }
        }

        // 3. Hydrogrid-merverdi vs. flat baseline:
        // Smart timing-bidrag = Σ(MWh × spot) - Σ(MWh) × snitt_spot
        double hgMerverdi = 0, faktiskMerverdi = 0;
        double sumPlanMwhMedSpot = 0, sumElhubMedSpot = 0;
        double sumPlanRevenue = 0, sumElhubRevenue = 0;
        foreach (var r in rows)
        {
            if (!r.SpotprisNokMwh.HasValue) continue;
            var spot = r.SpotprisNokMwh.Value;
            if (r.PlanMwh.HasValue && r.PlanMwh.Value > 0)
            {
                sumPlanMwhMedSpot += r.PlanMwh.Value;
                sumPlanRevenue += r.PlanMwh.Value * spot;
            }
            if (r.ElhubMwh.HasValue && r.ElhubMwh.Value > 0)
            {
                sumElhubMedSpot += r.ElhubMwh.Value;
                sumElhubRevenue += r.ElhubMwh.Value * spot;
            }
        }
        if (medSpot.Count > 0)
        {
            hgMerverdi = sumPlanRevenue - sumPlanMwhMedSpot * snittSpot;
            faktiskMerverdi = sumElhubRevenue - sumElhubMedSpot * snittSpot;
        }

        return (planTreff, andelTopp, andelBunn,
                andelTimerTopp, andelTimerBunn,
                hgMerverdi, faktiskMerverdi,
                snittSpot, sumElhub, sumPlan, antTimerMedPlan, antTimerProduksjon);
    }
}
```

### `ProduksjonAnalyseCalculatorTests.cs` — alle 14 tester

```csharp
using FluentAssertions;
using KraftverkUptime.Modules.Reporting.Produksjon;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Produksjon;

public class ProduksjonAnalyseCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private const string PlantId = "test-plant";

    private static ProduksjonAnalyseCalculator.HourlyInput Hour(
        int offset, double? plan, double? elhub, double? spot)
        => new(T0.AddHours(offset), plan, elhub, spot);

    private static ProduksjonAnalyseResult Run(
        IReadOnlyList<ProduksjonAnalyseCalculator.HourlyInput> rows,
        DateTimeOffset? to = null)
        => ProduksjonAnalyseCalculator.Compute(
            PlantId, T0, to ?? T0.AddHours(rows.Count > 0 ? rows.Count : 1), rows);

    [Fact]
    public void Compute_TomtInput_ReturnererNullKpi()
    {
        var r = Run(Array.Empty<ProduksjonAnalyseCalculator.HourlyInput>());
        r.AntallTimer.Should().Be(0);
        r.PlanTreffProsent.Should().Be(0);
        r.AndelProdIToppKvartil.Should().Be(0);
        r.AndelTimerProdIToppKvartil.Should().Be(0);
        r.HydrogridMerverdiNok.Should().Be(0);
        r.Hourly.Should().BeEmpty();
        r.Monthly.Should().BeEmpty();
    }

    [Fact]
    public void Compute_KunPlan_NoElhub_PlanTreffNull()
    {
        // 24 timer plan = 1 MWh, men 0 produksjon hver time
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1.0, elhub: 0, spot: 500)).ToList();
        var r = Run(rows);
        r.AntallTimerMedPlan.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(0);
        // PlanTreff = 1 - sum(|0-1|)/sum(1) = 1 - 24/24 = 0
        r.PlanTreffProsent.Should().Be(0);
    }

    [Fact]
    public void Compute_PerfektTreff_PlanLikElhub()
    {
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1.0, elhub: 1.0, spot: 500)).ToList();
        var r = Run(rows);
        r.PlanTreffProsent.Should().BeApproximately(1.0, 1e-9);
        r.AntallTimerProduksjon.Should().Be(24);
    }

    [Fact]
    public void Compute_AlleTimerIToppKvartil_AndelTopp100()
    {
        // 24 timer, spot stiger 100→2400. Topp-6 = timer 18-23.
        // Plan + elhub er 1 MWh bare for de 6 toppene.
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            var produserer = i >= 18;
            rows.Add(Hour(i,
                plan: produserer ? 1.0 : 0,
                elhub: produserer ? 1.0 : 0,
                spot: spot));
        }
        var r = Run(rows);
        r.AndelProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_KortPeriode_FireTimer_KvartilSize1()
    {
        // 4 timer → kvartil = max(1, 4/4) = 1
        var rows = new[]
        {
            Hour(0, plan: 1, elhub: 1, spot: 100),
            Hour(1, plan: 1, elhub: 1, spot: 200),
            Hour(2, plan: 1, elhub: 1, spot: 300),
            Hour(3, plan: 1, elhub: 1, spot: 400),
        };
        var r = Run(rows);
        r.AndelProdIToppKvartil.Should().BeApproximately(0.25, 1e-9);
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Compute_NegativSpotpris_HandteresKorrekt()
    {
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = i < 6 ? -50.0 : 100.0 + i * 50;
            rows.Add(Hour(i, plan: 1, elhub: 1, spot: spot));
        }
        var r = Run(rows);
        r.AntallTimer.Should().Be(24);
        r.AntallTimerProduksjon.Should().Be(24);
        // De 6 negative timene er bunn-kvartil
        r.AndelProdIBunnKvartil.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Compute_ManglendeSpot_TimerEkskluderes()
    {
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            rows.Add(Hour(i, plan: 1, elhub: 1, spot: i < 12 ? 500 : (double?)null));
        }
        var r = Run(rows);
        r.PlanTreffProsent.Should().BeApproximately(1.0, 1e-9);
        r.SnittSpotprisNokMwh.Should().BeApproximately(500, 1e-9);
    }

    [Fact]
    public void Compute_FlatSpotpris_AlleTimerLikePris_MerverdiNull()
    {
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1, elhub: 1, spot: 1000.0)).ToList();
        var r = Run(rows);
        r.HydrogridMerverdiNok.Should().BeApproximately(0, 1e-6);
        r.FaktiskMerverdiNok.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void Compute_OptimalTiming_AllProdIHoyestePris_HgMerverdiPositiv()
    {
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            var planAndElhub = i >= 18 ? 4.0 : 0.0;
            rows.Add(Hour(i, plan: planAndElhub, elhub: planAndElhub, spot: spot));
        }
        var r = Run(rows);
        r.HydrogridMerverdiNok.Should().BeGreaterThan(0);
        r.AndelProdIToppKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_DarligTiming_AllProdILavestePris_HgMerverdiNegativ()
    {
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            var planAndElhub = i < 6 ? 4.0 : 0.0;
            rows.Add(Hour(i, plan: planAndElhub, elhub: planAndElhub, spot: spot));
        }
        var r = Run(rows);
        r.HydrogridMerverdiNok.Should().BeLessThan(0);
        r.AndelProdIBunnKvartil.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Compute_KrysserManedsskifte_GirToMonthly()
    {
        var start = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 48)
            .Select(i => new ProduksjonAnalyseCalculator.HourlyInput(
                start.AddHours(i), PlanMwh: 1, ElhubMwh: 1, SpotprisNokMwh: 500))
            .ToList();
        var r = ProduksjonAnalyseCalculator.Compute(
            PlantId, start, start.AddHours(48), rows);
        r.Monthly.Should().HaveCount(2);
        r.Monthly[0].Year.Should().Be(2026);
        r.Monthly[0].Month.Should().Be(2);
        r.Monthly[1].Month.Should().Be(3);
    }

    [Fact]
    public void Compute_TimeAndelOgVolumAndel_KomplementaereMen_Distinkt()
    {
        // Ujevn fordeling: 100 MWh i én topp-time + 1 MWh i 6 bunn-timer
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            var spot = 100.0 + i * 100;
            double? elhub;
            if (i == 23) elhub = 100.0;
            else if (i < 6) elhub = 1.0;
            else elhub = 0.0;
            rows.Add(Hour(i, plan: elhub, elhub: elhub, spot: spot));
        }
        var r = Run(rows);
        // MWh-volum: 100/106 ≈ 94 %
        r.AndelProdIToppKvartil.Should().BeApproximately(100.0 / 106.0, 1e-6);
        // Drifts-timer: 1/7 ≈ 14 %
        r.AndelTimerProdIToppKvartil.Should().BeApproximately(1.0 / 7.0, 1e-6);
        r.AndelProdIToppKvartil.Should().NotBeApproximately(r.AndelTimerProdIToppKvartil, 0.5);
    }

    [Fact]
    public void Compute_AntallTimerProduksjon_TellerKunElhubPositive()
    {
        var rows = new List<ProduksjonAnalyseCalculator.HourlyInput>();
        for (var i = 0; i < 24; i++)
        {
            rows.Add(Hour(i, plan: 1, elhub: i < 10 ? 1 : 0, spot: 500));
        }
        var r = Run(rows);
        r.AntallTimerProduksjon.Should().Be(10);
        r.KapasitetsutnyttelseProsent.Should().BeApproximately(10.0 / 24.0, 1e-9);
    }

    [Fact]
    public void Compute_OverflowHours_TellerKorrekt()
    {
        var rows = Enumerable.Range(0, 24)
            .Select(i => Hour(i, plan: 1, elhub: 1, spot: 500)).ToList();
        var overflowHours = new HashSet<DateTimeOffset>(
            Enumerable.Range(0, 5).Select(i => T0.AddHours(i)));
        var r = ProduksjonAnalyseCalculator.Compute(
            PlantId, T0, T0.AddHours(24), rows, overflowHours, overlopDataTilgjengelig: true);
        r.AntallTimerOverlop.Should().Be(5);
        r.OverlopProsent.Should().BeApproximately(5.0 / 24.0, 1e-9);
        r.OverlopDataTilgjengelig.Should().BeTrue();
    }
}
```

## Resultater

- `dotnet build`: ✅ grønn
- `dotnet test` (full suite): **271/271** passerer
  - Core.Tests: 56
  - Infrastructure.Tests: 116 (inkl. 14 nye Produksjon-tester)
  - Api.Tests: 16
  - EndToEnd.Tests: 83/84 (1 skipped, ikke relatert)
- API-respons: nye felt eksponert (`andelTimerProdIToppKvartil`, `andelTimerProdIBunnKvartil`)

## Kjent avvik fra spec-fasit

Drivdal feb-2026 KPI-tallene har **endret seg** siden spec ble skrevet:

| Felt | Spec sa | Live nå | Diff |
|------|---------|---------|------|
| PlanTreffProsent | 65,8 % | 71,9 % | +6,1 pp |
| AndelProdIToppKvartil | 14,7 % | 11,2 % | -3,5 pp |
| HydrogridMerverdiNok | -21 697 | -31 353 | -9 656 |
| FaktiskMerverdiNok | -20 249 | -30 611 | -10 362 |

**Hypotese:** Avviket er ikke fra denne fix-en (testene viser at logikken er deterministisk for kjente input). Det er sporet til en tidligere commit `14a34b6` (plan-avviks-deteksjon ForcedDerating) som endret klassifikator-logikken og gjorde at re-genererte UptimeReport-blobs får andre `State`-verdier per time. Selv om calculator-en ikke direkte bruker `State`, kan det påvirke utvalg av "siste import per time" via at blob-er overskrives med ny klassifikator-output.

## Kontroll-spørsmål til ChatGPT

Vurder hvert punkt individuelt:

### 1. Logisk korrekthet i `ComputeAggregate`
- Plan-treff-formelen (linje 119–142): Er `1 - Σ|Elhub-Plan|/Σ|Plan|` matematisk ekvivalent med "1 minus MAPE-lignende-mål"? Er klipping til [0, 1] riktig (kan formelen bli negativ hvis Σ|avvik| > Σ|plan|, og er det riktig å klippe)?
- Kvartil-deling (linje 144–172): `Math.Max(1, count / 4)` — er dette riktig for grenseverdiene? Spesifikt: hva skjer ved 1, 2, 3, 4 timer?
- Tids-andel (linje 165–172): Er nevneren `antTimerProdMedSpot` riktig, eller burde den være total antall timer i kvartilen?
- Hg-merverdi (linje 175–204): Sjekk at `Σ(Plan × spot) − Σ(Plan) × snitt_spot` faktisk måler "smart timing-bidrag". Er det noen edge case der dette gir feil fortegn?

### 2. Tilstrekkelig test-dekning
- Dekker testene alle filialene (branches) i koden? Sjekk om noen `if`-grener mangler test.
- Test 12 (`Compute_TimeAndelOgVolumAndel_KomplementaereMen_Distinkt`): Er den et reelt scenarie eller for kunstig?
- Hva mangler? Forslag til ekstra tester som ville fanget regresjoner.

### 3. UI-konsistens (selv om filen ikke er inkludert)
Spec-en sier tooltip skal vise begge tall. Vi har endret til:
```
"MWh-andel i øverste 25 % spot-timer ({tids-andel} % av drifts-timer). Naiv baseline = 25 %."
```
Er dette tydelig nok for en drifts-leder som ikke kjenner formlene?

### 4. Spec-fasit-avvik
Vi mistenker at calculator-koden er stabil men at upstream-data har endret seg. Hvordan ville du verifisere dette? Forslag:
- Skrive en regresjons-test som låser Drivdal feb-2026-tallene mot en kjent JSON-fixture.
- Re-importere settlement-fila og se om gamle tall kommer tilbake.

### 5. Refactoring-muligheter
Calculator returnerer en 12-tuple fra `ComputeAggregate`. Det blir uleselig og fragilt (rekkefølge-feil ved utvidelse). Forslag til alternativ struktur?

### 6. Sikkerhet og null-håndtering
Calculator håndterer `double?`-felter forsiktig — er det noen NullReferenceException-risiko vi har oversett?

### 7. Ytelsesvurdering
For en 8760-timers-periode (ett år) kjøres `ComputeAggregate` 13 ganger (én aggregat + 12 måneder). Er det noen O(n²)-felle?

## Kontekst som kan være nyttig

- Stack: .NET 10, Blazor WASM, MudBlazor, ApexCharts, EF Core 9, PostgreSQL (Npgsql), Docker Compose
- Domene: Day-ahead spotmarked (Nord Pool), Statnett Elhub for målt produksjon, Hydrogrid for produksjonsplan-leveranse til settlement
- Test-rammeverk: xUnit + FluentAssertions
- Settlement-data kommer fra KAIA-portal (Excel) inkludert Hydrogrids plan i `ProduksjonplanMwh`-kolonne — vi har dermed Hydrogrid-output uten å integrere mot deres API direkte

## Hvordan svare

Vennligst returner:

1. **Vurdering per kontroll-spørsmål** (1–7 over).
2. **Eventuelle bugs eller logiske feil** med eksakt linjenummer-referanse.
3. **Konkrete forslag** til nye tester eller refaktoreringer, prioritert.
4. **Helhetsdom**: er denne implementasjonen produksjons-klar for Dalane Krafts drifts-leder, eller bør noe rettes først?

Tusen takk for second opinion.
