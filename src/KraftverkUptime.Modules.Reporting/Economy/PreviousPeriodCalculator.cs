namespace KraftverkUptime.Modules.Reporting.Economy;

/// <summary>
/// Klassifisering av perioden brukeren har valgt — styrer hvordan
/// "forrige periode" beregnes for trend-piler i Økonomi-fanen.
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22).
/// </summary>
public enum PeriodKind
{
    /// <summary>Måned: forrige kalendermåned. April 2026 → Mars 2026.</summary>
    Month = 0,

    /// <summary>Kvartal: forrige kvartal. Q2 2026 → Q1 2026.</summary>
    Quarter = 1,

    /// <summary>År: forrige år. 2026 → 2025.</summary>
    Year = 2,

    /// <summary>
    /// Hittil i år (YTD): samme dato-spenn forrige år.
    /// 1.1–22.5.2026 → 1.1–22.5.2025.
    /// </summary>
    YearToDate = 3,

    /// <summary>
    /// Egendefinert periode: samme lengde umiddelbart før valgt periode.
    /// </summary>
    Custom = 4,
}

/// <summary>
/// Ren funksjon som beregner "forrige periode" for trend-sammenligning
/// i Økonomi-fanen. Beslutninger og semantikk er definert i
/// NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22, del "Forrige periode").
///
/// Funksjonen er hold-bevisst pure (ingen DI, ingen tid-i-nået-avhengighet)
/// slik at den kan enhetstestes uten mocking og slik at samme input alltid
/// gir samme output.
/// </summary>
public static class PreviousPeriodCalculator
{
    /// <summary>
    /// Beregner forrige periodes [from, to) basert på <paramref name="kind"/>.
    /// Periode-intervaller behandles som [start, end) — slutten er eksklusiv,
    /// så månedsperioden April 2026 representeres som
    /// (from=2026-04-01, to=2026-05-01).
    /// </summary>
    /// <exception cref="ArgumentException">Hvis <paramref name="to"/> ikke er strengt etter <paramref name="from"/>.</exception>
    public static (DateTimeOffset PrevFrom, DateTimeOffset PrevTo) Calculate(
        DateTimeOffset from, DateTimeOffset to, PeriodKind kind)
    {
        if (to <= from)
        {
            throw new ArgumentException(
                "to må være strengt etter from for å beregne forrige periode.", nameof(to));
        }

        return kind switch
        {
            // Måned/kvartal: skift hele intervallet bakover i kalenderen.
            // DateTimeOffset.AddMonths håndterer kort-måned-tilbakefall
            // (eks. 31.mars - 1mnd = 28/29.feb) automatisk.
            PeriodKind.Month => (from.AddMonths(-1), to.AddMonths(-1)),
            PeriodKind.Quarter => (from.AddMonths(-3), to.AddMonths(-3)),

            // År og hittil-i-år: identisk math, bare ulik intensjon.
            // YearToDate respekterer naturlig at to kan være midt i året.
            PeriodKind.Year => (from.AddYears(-1), to.AddYears(-1)),
            PeriodKind.YearToDate => (from.AddYears(-1), to.AddYears(-1)),

            // Egendefinert: samme lengde umiddelbart før valgt periode.
            // prevTo = from (ender presis der nåværende periode begynner).
            PeriodKind.Custom => (from - (to - from), from),

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Ukjent PeriodKind."),
        };
    }
}
