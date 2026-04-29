namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Per-event-resultat fra Vakt-ROI-beregningen. Sammenligner faktisk
/// nedetids-varighet (med vakt) med en counterfactual der vi venter til
/// neste arbeidsdag-oppmøte (uten vakt).
///
/// Regnemodellen er bevisst enkel og transparent:
///   reddet_timer = max(0, counterfactual_end − faktisk_end)
///   reddet_mwh   = reddet_timer × snitt_kapasitetsfaktor × InstallertEffektMw
///   reddet_nok   = reddet_mwh × snitt_spotpris_for_perioden
///
/// "Faktisk_end" hentes fra eventets EndUtc (med vakt-respons allerede iberegnet
/// i settlement-data — vakt-tjenesten har gjort jobben).
/// "Counterfactual_end" beregnes fra <c>VaktTidsmodell.NesteArbeidsdagOppstart</c>.
/// </summary>
public sealed record VaktRoiResultat
{
    public required DowntimeEvent Event { get; init; }
    public required bool ErInnenforVakt { get; init; }
    public required bool ErReddbar { get; init; }
    public DateTimeOffset? CounterfactualEndUtc { get; init; }
    public required double EkstraTimerSpart { get; init; }
    public required double ReddetMwh { get; init; }
    public required double ReddetNok { get; init; }

    /// <summary>Forklaring for visning: hvorfor eventet ble (eller ikke ble) regnet.</summary>
    public required string Forklaring { get; init; }
}
