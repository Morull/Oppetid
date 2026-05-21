namespace KraftverkUptime.Modules.Reporting.Effektivitet;

/// <summary>
/// Henter effektivitets-data for et anlegg fra SCADA-time-aggregat:
///   – Produksjon (kW), virkningsgrad (%) og vannføring (m³/s) per time
///   – Snitt-KPI-er for perioden
///   – Sweet-spot-effekt (algoritmisk: høyeste-η-effekt-bin)
///
/// Anlegg-uavhengig: krever kun at plantet har de tre SCADA-rollene
/// <c>GeneratorActivePower</c>, <c>TurbineEfficiency</c> og
/// <c>TurbineWaterFlow</c> mappet i <c>core.signal_map</c>. Returnerer
/// tom data hvis tags mangler eller ingen produksjons-timer i perioden.
/// </summary>
public interface IEffectivityQueryService
{
    Task<EffektivitetResponse> GetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <summary>
/// Aggregert respons for /effektivitet-endepunktet.
/// <see cref="ProduksjonsTimer"/> er antall timer med P over terskel.
/// <see cref="SnittEtaPct"/> er snitt-virkningsgrad i prosent.
/// <see cref="SweetSpotEffektKw"/> er effekt-bin'en (kW) med høyest η — 0 hvis for lite data.
/// <see cref="SnittSpesifiktVannforbrukM3PerKwh"/> = sum(Q × 3600) / sum(P_kWh).
/// <see cref="DataMissing"/> = true hvis SCADA-tags mangler eller ingen samples dekker perioden.
/// </summary>
public sealed record EffektivitetResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int ProduksjonsTimer,
    double SnittEtaPct,
    double SweetSpotEffektKw,
    double SweetSpotEtaPct,
    double SnittSpesifiktVannforbrukM3PerKwh,
    double TotalProduksjonKwh,
    bool DataMissing,
    IReadOnlyList<EffektivitetPunkt> Punkter,
    IReadOnlyList<EffektivitetBin> Bins);

public sealed record EffektivitetPunkt(
    DateTimeOffset TimeUtc,
    double EffektKw,
    double EtaPct,
    double VannforingM3PerS,
    PunktKlassifisering Klassifisering);

/// <summary>
/// Klassifisering av et 15-min-intervall basert på (P, η)-mønsteret. Spec
/// NESTE-CHAT-EFFEKTIVITET-15MIN.md: vi vil skille mellom faktisk drift og
/// start/stopp-ramper, fordi ramper ellers drar snitt-η ned med 5-10 prosent-
/// poeng på et anlegg som ellers ligger stabilt på 90 % η.
///
///   <see cref="Genuine"/>   — produserende intervall der η er over gulvet.
///                             Inngår i alle KPI-aggregat (snitt, sweet-spot,
///                             SVF, bin-histogram).
///   <see cref="Transition"/> — produserende intervall (P ≥ terskel) men der
///                             η ligger UNDER <c>GenuineEtaFloorPct</c>. Tolkes
///                             som ramp-up/ramp-down — synlig i Punkter med
///                             egen farge, men IKKE i snitt-aggregat.
/// </summary>
public enum PunktKlassifisering
{
    Genuine = 0,
    Transition = 1,
}

/// <summary>
/// Bin i η(P)-histogrammet — én rad per <see cref="EffektivitetQueryService.PowerBinKw"/>-bredde
/// effekt-bin der minst én produksjons-time falt. <see cref="EffektKwStart"/> er nedre grense (kW),
/// <see cref="EffektKwMid"/> er midtpunkt, <see cref="Antall"/> er antall timer, og
/// <see cref="SnittEtaPct"/> er snitt-virkningsgrad i prosent for bin'en.
/// </summary>
public sealed record EffektivitetBin(
    double EffektKwStart,
    double EffektKwMid,
    int Antall,
    double SnittEtaPct);
