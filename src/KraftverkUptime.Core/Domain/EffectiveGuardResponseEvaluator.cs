namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md (2026-05-22): U2-PlanDeviation-
/// hendelser uten operlog-match teller IKKE som vakt-utrykning i Auto-modus
/// — operatøren reagerte ikke på en alarm. Drifts-leder kan overstyre per
/// hendelse via <see cref="GuardResponseOverride"/>.
///
/// Logikken sitter HER (utenfor <c>VaktRoiCalculator</c>) for at kalkulatoren
/// skal forbli en ren funksjon av events + plan-data. Endepunktene
/// (NedetidEndpoints og PortfolioVaktRoiQueryService) bygger settet av events
/// hvor <see cref="ShouldCount"/> = false og sender det videre som
/// <c>excludeFromReddbar</c> til kalkulatoren.
///
/// Logikk-tabell:
/// <code>
/// causeCode      | override | harOperlogMatch | EffectiveGuardResponse
/// ---------------+----------+-----------------+-----------------------
/// U2-PlanDeviation| Auto    | false           | false
/// U2-PlanDeviation| Auto    | true            | true
/// U2-PlanDeviation| Yes     | *               | true
/// U2-PlanDeviation| No      | *               | false
/// alt annet      | *        | *               | true (uendret)
/// </code>
/// </summary>
public static class EffectiveGuardResponseEvaluator
{
    /// <summary>
    /// CauseCode-verdien som trigger U2-PlanDeviation-filteret. Lagret som
    /// streng-konstant for å unngå spredte literale forekomster.
    /// </summary>
    public const string PlanDeviationCauseCode = "U2-PlanDeviation";

    /// <summary>
    /// Avgjør om en nedetidshendelse skal regnes som vakt-utrykning. Resultat
    /// = true betyr at hendelsen kvalifiserer som "vakta rykket ut" og kan
    /// bidra til Vakt-ROI som vanlig. Resultat = false betyr at hendelsen
    /// skal filtreres ut av ROI-summen (men vises fortsatt i UI).
    /// </summary>
    /// <param name="event">Aggregert nedetidshendelse.</param>
    /// <param name="overrideValue">
    /// Eksplisitt overstyring fra drifts-leder, eller null hvis ingen
    /// override-rad eksisterer for denne hendelsen (behandles som
    /// <see cref="GuardResponseOverride.Auto"/>).
    /// </param>
    public static bool ShouldCount(DowntimeEvent @event, GuardResponseOverride? overrideValue)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Filteret gjelder KUN U2-PlanDeviation. Andre årsakskoder (U1-UnplannedStop,
        // PlanlagtVedlikehold, TettInntaksrist osv.) er upåvirket og teller alltid
        // som vakt-utrykning hvis de ellers er reddbare.
        if (!string.Equals(@event.CauseCode, PlanDeviationCauseCode, StringComparison.Ordinal))
        {
            return true;
        }

        // Eksplisitt overstyring fra drifts-leder vinner over auto-vurderingen.
        var effective = overrideValue ?? GuardResponseOverride.Auto;
        return effective switch
        {
            GuardResponseOverride.Yes => true,
            GuardResponseOverride.No => false,
            // Auto: operlog-match er proxy for "alarm kom inn og operatør reagerte".
            _ => @event.HarOperlogMatch,
        };
    }
}
