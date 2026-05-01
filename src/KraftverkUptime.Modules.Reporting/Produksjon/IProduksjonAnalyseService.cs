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
///     1.0 = perfekt treff, 0.0 = avvik 100 % i snitt. Klippet til [0, 1].
///   AndelProdIToppKvartil: andel av Elhub-MWh i øverste 25 % spotpris-timer.
///     0.25 = "ingen timing-effekt", &gt; 0.25 = god timing.
///   AndelProdIBunnKvartil: andel i bunn-25 % av prisen. Lavere = bedre.
///   HydrogridMerverdiNok: Σ(Plan_t × spot_t) − Σ(Plan_t) × snitt_spot.
///     Positiv = Hydrogrid flyttet produksjon til høypristimer.
///   FaktiskMerverdiNok: tilsvarende for Elhub. Sammenlign mot
///     HydrogridMerverdiNok for å se om vi tjente mer/mindre enn planen.
/// </summary>
public sealed record ProduksjonAnalyseResult(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallTimer,
    int AntallTimerMedPlan,
    int AntallTimerProduksjon,
    double TotalElhubMwh,
    double TotalPlanMwh,
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double AndelProdIBunnKvartil,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    IReadOnlyList<ProduksjonHourlyPoint> Hourly,
    IReadOnlyList<ProduksjonMonthly> Monthly);

/// <summary>Én time — rå data for graf-visning (Plan vs Elhub vs spot).</summary>
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
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh);
