using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Classification.Classification;

/// <summary>
/// Smelter sammen settlement-klassifisert data med SCADA-klassifisering og
/// operlog-events til én forfinet ClassifiedHourlyRow per time. Pure funksjon —
/// caller eier henting av input-data.
///
/// Konfliktløsning (fra VEIKART-AUTONOM.md Steg 5):
///   1. Annoteringer overstyrer alt — skjer separat via AnnotationOverlayService.
///   2. Settlement og SCADA enige om state → behold settlement (med litt høyere
///      confidence siden to kilder bekrefter).
///   3. Uenige → SCADA trumfer for drifts-tilstand (SCADA ser rotasjon/effekt
///      direkte), settlement beholdes for økonomisk Row-data.
///   4. Operlog beriker CauseCode + Rationale uten å overstyre time-state.
///
/// Når SCADA-data mangler for en time, returneres settlement-raden uendret
/// (men rationale kan beriket av operlog hvis det fantes).
/// </summary>
public static class FusionClassifier
{
    /// <summary>
    /// Smelter sammen settlement-klassifiserte rader med SCADA + operlog.
    /// Beholder rekkefølgen fra <paramref name="settlement"/>.
    /// </summary>
    public static IReadOnlyList<ClassifiedHourlyRow> Fuse(
        IReadOnlyList<ClassifiedHourlyRow> settlement,
        IReadOnlyDictionary<DateTimeOffset, ScadaFusionInput>? scadaByHour = null,
        IReadOnlyList<ClassifiedEvent>? operlog = null)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (settlement.Count == 0) return settlement;

        var scada = scadaByHour ?? new Dictionary<DateTimeOffset, ScadaFusionInput>();
        var operlogList = operlog ?? Array.Empty<ClassifiedEvent>();

        // Indekser operlog i tids-sortert liste for raskere oppslag per time.
        var operlogSorted = operlogList.OrderBy(o => o.StartUtc).ToList();

        var result = new List<ClassifiedHourlyRow>(settlement.Count);
        foreach (var row in settlement)
        {
            result.Add(FuseOne(row, scada, operlogSorted));
        }
        return result;
    }

    private static ClassifiedHourlyRow FuseOne(
        ClassifiedHourlyRow settlement,
        IReadOnlyDictionary<DateTimeOffset, ScadaFusionInput> scada,
        IReadOnlyList<ClassifiedEvent> operlog)
    {
        // 1) Oppdrag operlog-event for denne timen (start innenfor [time, time+1t))
        var operlogMatch = FindOperlogMatch(settlement.TimeUtc, operlog);
        var operlogTag = operlogMatch is null
            ? null
            : $"operlog:{operlogMatch.CauseCode ?? "event"}";
        var operlogRationale = operlogMatch is null
            ? null
            : $"operlog: {operlogMatch.CauseCode ?? "event"} @ {operlogMatch.StartUtc:HH:mm:ss}";

        // 2) SCADA-state for denne timen
        if (!scada.TryGetValue(settlement.TimeUtc, out var scadaState))
        {
            // Ingen SCADA-data → returner settlement med eventuelle operlog-berikelse
            return AddOperlogEnrichment(settlement, operlogTag, operlogRationale);
        }

        // 3) Settlement og SCADA enige
        if (settlement.State == scadaState.State)
        {
            return settlement with
            {
                Confidence = Math.Min(1.0, Math.Max(settlement.Confidence, scadaState.Confidence) + 0.05),
                CauseCode = operlogTag ?? settlement.CauseCode,
                Rationale = settlement.Rationale + " · scada-bekreftet"
                    + (operlogRationale is null ? "" : " · " + operlogRationale),
            };
        }

        // 4) Uenige → SCADA vinner for state, settlement beholder Row-data
        return new ClassifiedHourlyRow
        {
            Row = settlement.Row,
            State = scadaState.State,
            CauseCode = operlogTag ?? scadaState.CauseCode,
            Confidence = scadaState.Confidence,
            Rationale = $"settlement={settlement.State} ↔ scada={scadaState.State} (SCADA vant). "
                + scadaState.Rationale
                + (operlogRationale is null ? "" : " · " + operlogRationale),
        };
    }

    private static ClassifiedHourlyRow AddOperlogEnrichment(
        ClassifiedHourlyRow row, string? operlogTag, string? operlogRationale)
    {
        if (operlogTag is null && operlogRationale is null) return row;
        return row with
        {
            CauseCode = operlogTag ?? row.CauseCode,
            Rationale = row.Rationale + (operlogRationale is null ? "" : " · " + operlogRationale),
        };
    }

    private static ClassifiedEvent? FindOperlogMatch(
        DateTimeOffset hourUtc, IReadOnlyList<ClassifiedEvent> sorted)
    {
        var hourEnd = hourUtc.AddHours(1);
        // Lineær scan — for små perioder er det greit. Kan bli interval-tree senere.
        foreach (var e in sorted)
        {
            if (e.StartUtc < hourUtc) continue;
            if (e.StartUtc >= hourEnd) break;
            return e;
        }
        return null;
    }
}

/// <summary>
/// Input fra SCADA-klassifikator inn i fusion. Holder bare state + metadata —
/// rådataene (P, RPM, Q) er ikke nødvendige for fusjon.
/// </summary>
public sealed record ScadaFusionInput(
    UnitState State,
    double Confidence,
    string CauseCode,
    string Rationale);
