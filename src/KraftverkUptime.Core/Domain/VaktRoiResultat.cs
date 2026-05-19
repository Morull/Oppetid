namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Per-event-resultat fra Vakt-ROI-beregningen. Sammenligner faktisk
/// nedetids-varighet (med vakt) med en counterfactual der vi venter til
/// neste arbeidsdag-oppmøte (uten vakt).
///
/// Regnemodellen er bevisst enkel og transparent:
///   reddet_timer = max(0, counterfactual_end − faktisk_end)
///   reddet_mwh   = sum(ProduksjonplanMwh for timer i counterfactual som har
///                       overløp OG ikke er dekket av outage)
///   reddet_nok   = reddet_mwh × snitt_spotpris_for_perioden
///
/// "Faktisk_end" hentes fra eventets EndUtc (med vakt-respons allerede iberegnet
/// i settlement-data — vakt-tjenesten har gjort jobben).
/// "Counterfactual_end" beregnes fra <c>VaktTidsmodell.NesteArbeidsdagOppstart</c>.
///
/// Plan-data (<c>ProduksjonplanMwh</c> fra Hydrogrid-plan) brukes som basis
/// for reddet produksjon istedenfor flat <c>installertEffektMw × kapasitetsfaktor</c>
/// — det fanger årstid/vannføring direkte siden plan reflekterer faktisk
/// vannmengde producer hadde forventet å kjøre på.
/// </summary>
public sealed record VaktRoiResultat
{
    public required DowntimeEvent Event { get; init; }
    public required bool ErInnenforVakt { get; init; }
    public required bool ErReddbar { get; init; }
    public DateTimeOffset? CounterfactualEndUtc { get; init; }
    public required double EkstraTimerSpart { get; init; }
    public required double ReddetMwh { get; init; }

    /// <summary>
    /// Total reddet beløp = <see cref="ReddetProduksjon_NOK"/> + <see cref="ReddetUbalanse_NOK"/>.
    /// Bevart for bakoverkompabilitet med UI som ikke bruker komponentene direkte.
    /// </summary>
    public required double ReddetNok { get; init; }

    /// <summary>
    /// Verdien av selve produksjons-tapet vakten reddet. Gjelder kun timer
    /// med overløp i magasinet (ellers er vannet bare utsatt, ikke tapt).
    /// </summary>
    public required double ReddetProduksjon_NOK { get; init; }

    /// <summary>
    /// Verdien av ubalanse-gebyret vakten reddet. Gjelder ALLE counterfactual-
    /// timer (uavhengig av magasinstand) fordi Spotbud-forpliktelsen står
    /// uansett. Beregnes som sum(ProduksjonplanMwh for timer ikke dekket av
    /// outage) × snittUbalansetillegg, der snittUbalansetillegg =
    /// max(0, avg(RkPris − Spot)) over perioden.
    /// </summary>
    public required double ReddetUbalanse_NOK { get; init; }

    /// <summary>
    /// Antall timer i counterfactual-perioden der det var overløp i magasinet.
    /// Det er disse timene vakt-tjenesten faktisk reddet produksjon for —
    /// ellers ville vannet rent forbi turbinen og kunne ikke vært utnyttet
    /// uavhengig av om turbinen kjørte.
    /// </summary>
    public required int OverflowTimerInCounterfactual { get; init; }

    /// <summary>True hvis SCADA-data manglet for hele eller deler av counterfactual-perioden.</summary>
    public required bool OverflowDataMissing { get; init; }

    /// <summary>
    /// True hvis plan-data (<c>ProduksjonplanMwh</c>) manglet for én eller flere
    /// counterfactual-timer og proxy-fallback (samme ukedag/time bakover) ble
    /// brukt. UI bør markere eventet slik at drifts-leder vet at tallene er
    /// estimerte for de manglende timene. Default false.
    /// </summary>
    public bool PlanDataPartial { get; init; }

    /// <summary>Forklaring for visning: hvorfor eventet ble (eller ikke ble) regnet.</summary>
    public required string Forklaring { get; init; }
}
