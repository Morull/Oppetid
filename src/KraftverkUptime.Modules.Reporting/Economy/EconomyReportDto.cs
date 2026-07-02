namespace KraftverkUptime.Modules.Reporting.Economy;

/// <summary>
/// Aggregert økonomi-rapport for valgt periode + utvalg av anlegg.
/// Brukes som backing-data for Økonomi-fanen på Portefølje-siden og som
/// kilde for PDF-eksport. Spec NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22).
///
/// Trend-piler beregnes mot <see cref="PrevFrom"/> / <see cref="PrevTo"/>,
/// som er avledet av <see cref="PreviousPeriodCalculator"/>.
/// </summary>
public sealed record EconomyReportDto(
    string[] PlantIds,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset PrevFrom,
    DateTimeOffset PrevTo,
    EconomyKpiGroupDto Inntekter,
    EconomyKpiGroupDto Kostnader,
    EconomyKpiGroupDto Resultat,
    PerPlantEconomyDto[] PerPlant);

/// <summary>
/// Én KPI-gruppe i Økonomi-fanen (Inntekter / Kostnader / Resultat).
/// Tittelen brukes både i UI og som overskrift i PDF-rapporten.
/// </summary>
public sealed record EconomyKpiGroupDto(
    string Title,
    EconomyKpiDto[] Kpis);

/// <summary>
/// Én KPI med verdi, sammenligning mot forrige periode og hvilken retning som
/// er "god". <see cref="GoodDirection"/> brukes til å fargelegge trend-pilen
/// per KPI (eks. økt Ubalansekost er rød ▲ selv om endringsprosenten er
/// positiv).
/// </summary>
/// <param name="Key">Stabil maskinell nøkkel (eks. "spotomsetning"). Brukes for klikk-bare drill-down-ruter senere.</param>
/// <param name="Label">Visningsnavn på norsk (eks. "Spotomsetning").</param>
/// <param name="Verdi">Verdien for valgt periode.</param>
/// <param name="Enhet">"NOK", "ratio" eller "MWh". Styrer formatering i UI/PDF.</param>
/// <param name="VerdiForrige">Verdien for sammenligningsperioden, eller null hvis ikke beregnelig.</param>
/// <param name="EndringProsent">Relativ endring fra forrige (1.0 = +100 %), eller null hvis VerdiForrige er null/0.</param>
/// <param name="GoodDirection">"up" hvis økning er positivt for drifts-leder, "down" hvis det motsatte.</param>
public sealed record EconomyKpiDto(
    string Key,
    string Label,
    double Verdi,
    string Enhet,
    double? VerdiForrige,
    double? EndringProsent,
    string GoodDirection);

/// <summary>
/// Én rad i per-anleggs-tabellen som vises når Økonomi-fanen viser flere
/// anlegg samtidig. Inneholder både økonomi-feltene som vises i
/// Økonomi-fanen og drift-/datakvalitet-feltene som Portefølje-Sammendrag
/// trenger for sin tabell. Spec MASTERPLAN-CODE-2026-05-22 § «Sammendrag
/// reuser /economy» (2026-05-22) — målet er 1 aggregert kall som dekker
/// både underfaner istedenfor 22+ per-anleggs-kall.
/// </summary>
public sealed record PerPlantEconomyDto(
    string PlantId,
    string PlantName,
    double OppgjorNok,
    double SpotomsetningNok,
    double UbalansekostNok,
    double KaiaKostnadNok,
    double CaptureRate,
    // Drift- og produksjons-felt for Sammendrag-tabellen.
    double InstalledCapacityMw,
    double TotalProductionMwh,
    double MerverdiNok,
    double AvailabilityFactor,
    double AvailabilityFactorIeee,
    double ForcedOutageRate,
    // Nedetid + Vakt-ROI per anlegg for Sammendrag-kortene.
    double NedetidTimer,
    double NedetidstapNok,
    double ReddetAvVaktNok,
    int AntallEvents,
    int AntallReddbareEvents,
    // Datakvalitet og normal-produksjon for Sammendrag-tabellen.
    double? NormalAarsproduksjonGwh,
    // Månedsfordeling av normalåret (12 %-verdier, jan først) for månedsvektet
    // normalår-sammenligning i UI. Null = flat pro-rata-fallback.
    // SPEC-MAANEDSPROFIL-NORMALAAR.
    double[]? MaanedsprofilProsent,
    double GoodHoursPct,
    int ManglerImportHours);

/// <summary>
/// Stabile maskin-nøkler for KPI-ene. Brukes som <see cref="EconomyKpiDto.Key"/>
/// og garanterer at klient-koden (UI + PDF) ikke avhenger av norske label-
/// strenger som kan endres uten varsel.
/// </summary>
public static class EconomyKpiKeys
{
    // Inntekter
    public const string Spotomsetning = "spotomsetning";
    public const string CaptureRate = "captureRate";
    public const string MerverdiVsSpot = "merverdiVsSpot";

    // Kostnader
    public const string Ubalansekost = "ubalansekost";
    public const string KaiaKostnad = "kaiaKostnad";
    public const string VaktKostAndel = "vaktKostAndel";

    // Resultat / drift
    public const string Oppgjor = "oppgjor";
    public const string Nedetidstap = "nedetidstap";
    public const string ReddetAvVakt = "reddetAvVakt";
}

/// <summary>
/// Konstante verdier for <see cref="EconomyKpiDto.GoodDirection"/>. Lagret som
/// strenger (ikke enum) for at JSON-kontrakten skal være selvforklarende på
/// klient-siden uten ekstra konverter.
/// </summary>
public static class EconomyKpiDirection
{
    public const string Up = "up";
    public const string Down = "down";
}

/// <summary>
/// Konstante verdier for <see cref="EconomyKpiDto.Enhet"/>. Klient-koden
/// formaterer ulikt basert på dette: NOK med tusenskille, MWh med én desimal,
/// ratio som prosent.
/// </summary>
public static class EconomyKpiUnit
{
    public const string Nok = "NOK";
    public const string Ratio = "ratio";
    public const string Mwh = "MWh";
}
