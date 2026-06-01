namespace KraftverkUptime.Modules.Reporting.StartStopp;

/// <summary>
/// Teller start/stopp-sykler (cold starts) fra time-oppløst produksjons-data.
/// En syklus = én transisjon fra "under terskel" til "over terskel" i
/// time-serien. Det fanger den fysisk mest skadelige driftsendringen for
/// turbiner (low-cycle fatigue på løpehjuls-bladene).
///
/// Tolerance-terskelen er en andel av installert effekt (default 0,5 %)
/// slik at hvilemodus/standby-belastning ikke teller. Spec
/// NESTE-CHAT-START-STOPP-KPI.md (2026-05-22).
///
/// Pure funksjon — ingen DI, ingen state. Enhetstestbar uten mocking.
/// </summary>
public static class StartStoppCalculator
{
    /// <summary>Default tolerance som andel av installert effekt (0,5 %).</summary>
    public const double DefaultTolerancePct = 0.005;

    /// <summary>
    /// Teller hvor mange ganger anlegget gikk fra hvilemodus til drift i
    /// time-serien. Time som er første i sekvensen og over terskel teller
    /// som én start. Påfølgende over-terskel-timer regnes som samme syklus
    /// (drift). Når serien faller under terskel, "resettes" tilstanden, og
    /// neste over-terskel-time teller som ny syklus.
    /// </summary>
    /// <param name="hours">Time-oppløst produksjon, helst sortert kronologisk.</param>
    /// <param name="installedMw">Installert effekt for anlegget (MW). Brukes
    /// til å beregne terskelen relativt til anleggsstørrelsen.</param>
    /// <param name="tolerancePct">Andel av <paramref name="installedMw"/> som
    /// regnes som "drift" (default 0,5 %).</param>
    /// <returns>Antall start-transisjoner. 0 hvis serien er tom eller aldri
    /// passerer terskelen.</returns>
    public static int CountStarts(
        IEnumerable<HourlyProduction> hours,
        double installedMw,
        double tolerancePct = DefaultTolerancePct)
    {
        ArgumentNullException.ThrowIfNull(hours);
        if (installedMw <= 0) return 0;

        var threshold = installedMw * tolerancePct;
        var starts = 0;
        var wasRunning = false;
        foreach (var h in hours.OrderBy(x => x.HourUtc))
        {
            var production = h.MwhElhub ?? 0;
            var isRunning = production > threshold;
            if (isRunning && !wasRunning) starts++;
            wasRunning = isRunning;
        }
        return starts;
    }
}

/// <summary>
/// Én time med produksjon — minimal input til
/// <see cref="StartStoppCalculator.CountStarts"/>. Holdes generisk slik at
/// kalkulatoren ikke tar avhengighet til Settlement-DTO-ene.
/// </summary>
/// <param name="HourUtc">Start på timen (UTC).</param>
/// <param name="MwhElhub">Levert MWh til Elhub i timen. Null = ukjent
/// (behandles som 0, dvs. ikke i drift).</param>
public sealed record HourlyProduction(DateTimeOffset HourUtc, double? MwhElhub);
