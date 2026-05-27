namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Manuell overstyring av om en vakt-hendelse skal regnes som en utrykning
/// — uavhengig av automatisk vurdering (operlog-match).
///
/// Spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md (2026-05-22): U2-PlanDeviation-
/// hendelser uten operlog-match teller ikke som vakt-utrykning i Auto-modus
/// (= operatør reagerte ikke). Drifts-leder kan overstyre per hendelse hvis den
/// automatiske vurderingen tar feil.
///
/// Default-verdi: <see cref="Auto"/>. Eksisterende rader uten override-rad
/// behandles også som Auto (= EffectiveGuardResponse-logikken).
/// </summary>
public enum GuardResponseOverride
{
    /// <summary>Bruk EffectiveGuardResponse-logikken: operlog-match for U2-PlanDeviation, true ellers.</summary>
    Auto = 0,

    /// <summary>Drifts-leder bekrefter at vakta rykket ut. Tving full ROI-medregning.</summary>
    Yes = 1,

    /// <summary>Drifts-leder bekrefter at vakta IKKE rykket ut. Filtrer hendelsen ut av ROI-summen.</summary>
    No = 2,
}
