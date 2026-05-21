namespace KraftverkUptime.Modules.Reporting.Effektivitet;

/// <summary>
/// Avviks- og episode-analyse (Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3).
///
/// For hvert produksjons-intervall (Genuine, 15-min) regner vi Δη mot baseline-
/// virkningsgraden for den effekt-bin'en intervallet faller i. Intervaller med
/// <c>Δη ≤ −2,0 pp</c> (justerbart) flagges som underytende. Sammenhengende
/// underytende intervaller (med inntil 1 normalt intervall mellom — også
/// justerbart) slås sammen til <see cref="UnderytendeEpisode"/>.
///
/// Tapt energi per intervall ≈ <c>faktisk_produksjon × (η_baseline − η_faktisk) / η_faktisk</c>.
/// Tolkning: «hvis vi hadde kjørt på baseline-virkningsgrad, hadde vi produsert
/// så mye MER». Tapt verdi = tapt MWh × spotpris i samme time (NOK).
///
/// Ren funksjon — tester bygges på stub-data uten DB-avhengighet.
/// </summary>
public interface IEffektivitetEpisodeService
{
    EpisodeAnalysisResult Analyse(
        EffektivitetResponse effektivitet,
        IReadOnlyDictionary<DateTimeOffset, double>? spotPrisNokMwhPerTime,
        EpisodeAnalyseOpsjoner? opsjoner = null);
}

/// <summary>
/// Justerbare terskler for episode-deteksjon. Default-ene matcher anbefalingene
/// i Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3.2.
/// </summary>
public sealed record EpisodeAnalyseOpsjoner(
    double DeltaEtaTerskelPp = -2.0,
    int TillattGapIntervaller = 1,
    int MinSamplesPerBaselineBin = 3);

/// <summary>
/// Resultat fra <see cref="IEffektivitetEpisodeService.Analyse"/>. Returnerer
/// både episode-listen og aggregat-statistikker for UI-en + grupperings-views.
/// </summary>
public sealed record EpisodeAnalysisResult(
    IReadOnlyList<UnderytendeEpisode> Episoder,
    IReadOnlyList<EffektBaandAggregat> AggregatPerEffektBaand,
    double TotalTaptMwh,
    double TotalTaptNok,
    int AntallGenuineIntervaller,
    int AntallUnderytendeIntervaller,
    bool ManglerSpotpriser);

/// <summary>
/// Én episode = sammenhengende underytende intervaller (kan ha ett normalt
/// intervall midt i hvis <see cref="EpisodeAnalyseOpsjoner.TillattGapIntervaller"/>
/// tillater det). Sorteres typisk synkende på <see cref="TaptNok"/>.
/// </summary>
public sealed record UnderytendeEpisode(
    DateTimeOffset StartUtc,
    DateTimeOffset SluttUtc,
    int AntallIntervaller,
    double VarighetTimer,
    double SnittDeltaEtaPp,
    double EffektMinKw,
    double EffektMaksKw,
    double FaktiskProduksjonMwh,
    double TaptMwh,
    double TaptNok,
    bool TaptNokErEstimat);

/// <summary>
/// Aggregat per effekt-bånd (samme bin som <see cref="EffektivitetBin"/>) —
/// brukes til «gjentakende mønster»-tabellen som lar drifts-leder se hvilke
/// effekt-bånd som har mest å hente.
/// </summary>
public sealed record EffektBaandAggregat(
    double EffektKwStart,
    double EffektKwSlutt,
    int AntallEpisoder,
    double TotalVarighetTimer,
    double SnittDeltaEtaPp,
    double TaptMwh,
    double TaptNok);
