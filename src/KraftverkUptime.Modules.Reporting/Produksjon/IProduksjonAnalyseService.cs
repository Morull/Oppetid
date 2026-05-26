namespace KraftverkUptime.Modules.Reporting.Produksjon;

/// <summary>
/// Aggregert produksjons-analyse som svarer på "hvor god er Hydrogrids
/// produksjonsplan, og blir vi bedre over tid?". Bruker eksisterende
/// settlement-data uten direkte Hydrogrid-API-integrasjon — ProduksjonplanMwh-
/// kolonnen i KAIA-eksporten er Hydrogrids plan som ble lastet inn der.
///
/// Metrikker som svarer på drifts-leders spørsmål:
///   - <see cref="ProduksjonAnalyseResult.PlanTreffProsent"/>: følger vi planen?
///   - <see cref="ProduksjonAnalyseResult.AndelProdIToppKvartil"/>: utnytter vi
///     de beste prisperiodene?
///   - <see cref="ProduksjonAnalyseResult.HydrogridMerverdiNok"/>: hvor mye
///     bedre er smart timing vs. å spre produksjon jevnt?
///   - Måneds-trend slik at man ser om Hydrogrid blir bedre eller verre.
/// </summary>
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
///   PlanTreffProsent: 1 − Σ|Elhub − Plan| / Σ|Plan| for timer med Plan &gt; 0.
///   AndelProdIToppKvartil: MWh-volum-andel i øverste 25 % spotpris-timer.
///   HydrogridMerverdiNok: Σ(Plan_t × spot_t) − Σ(Plan_t) × snitt_spot.
///     Plan-kvalitetsmål — viser hvor mye Hydrogrids PLAN flyttet til høypristimer.
///   FaktiskMerverdiNok: tilsvarende for Elhub — Timing-merverdi for faktisk produksjon.
///
/// Spec ANBEFALING-TAPSREGNSKAP.md punkt 1 («Netto mot plan»):
///   TimingGapNok       = FaktiskMerverdiNok − HydrogridMerverdiNok
///     Positiv = vi timet BEDRE enn planen; negativ = dårligere.
///   UbalanseKostTotalNok = Σ UbalanseKostNok over alle timer.
///     Alltid ≥ 0 — ubalanse er aldri en gevinst i dagens (enveis) formel.
///   NettoMotPlanNok    = TimingGapNok − UbalanseKostTotalNok
///     Positiv = avviket fra planen var lønnsomt totalt sett.
///     Negativ = avviket KOSTET oss penger.
///     Null = vi fulgte planen perfekt (Realisert = Planens, ingen ubalanse).
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
    double AndelTimerProdIToppKvartil,
    double AndelTimerProdIBunnKvartil,
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    bool OverlopDataTilgjengelig,
    IReadOnlyList<ProduksjonHourlyPoint> Hourly,
    IReadOnlyList<ProduksjonMonthly> Monthly,
    double SpotbudTreffProsent = 0,
    int AntallTimerMedSpotbud = 0,
    // Tapsregnskap punkt 1 (Spec ANBEFALING-TAPSREGNSKAP.md). Default 0 så
    // eldre call-sites kompilerer.
    double UbalanseKostTotalNok = 0,
    double TimingGapNok = 0,
    double NettoMotPlanNok = 0);

/// <summary>
/// Én time — rå data for graf-visning og driftslinje. Inkluderer:
///   - PlanMwh / ElhubMwh / SpotprisNokMwh: standard plan-vs-faktisk
///   - SpotbudMwh: faktisk meldt spotbud til NordPool (= produsentens
///     forpliktelse). Kan avvike fra Hydrogrid-plan hvis bud er korrigert
///     manuelt. Brukes som primær basis for ubalanse-kost-formelen.
///   - RkPrisNokMwh: regulerkraft-pris (for ubalanse-kost-beregning)
///   - HarOverlop: terminal-dam hadde overløp i denne timen
///   - UbalanseKostNok: estimert ubalanse-kost =
///     max(0, RK - Spot) × max(0, max(Spotbud, Plan) - Elhub)
///     Bruker Spotbud som forpliktelse hvis tilgjengelig, ellers Plan.
/// </summary>
public sealed record ProduksjonHourlyPoint(
    DateTimeOffset TimeUtc,
    double? PlanMwh,
    double? ElhubMwh,
    double? SpotprisNokMwh,
    double? RkPrisNokMwh = null,
    bool HarOverlop = false,
    double UbalanseKostNok = 0,
    double? SpotbudMwh = null);

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
    double AndelTimerProdIToppKvartil,
    double AndelTimerProdIBunnKvartil,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    // Tapsregnskap punkt 1 — per måned. Default 0 for bakover-kompatibilitet.
    double UbalanseKostTotalNok = 0,
    double TimingGapNok = 0,
    double NettoMotPlanNok = 0);
