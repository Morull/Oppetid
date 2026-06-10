using System.Globalization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Ren logikk for å regne ut periode-vinduer for global periode-velger.
/// Statisk + kun avhengig av <see cref="DateTime"/>-aritmetikk slik at
/// vi senere kan flytte til delt prosjekt og enhets-teste.
///
/// Konvensjon: Til-dato er EKSKLUSIV (typisk første dag i neste måned).
/// Dette matcher eksisterende DateRangeFilter / PeriodPresets-konvensjon.
/// </summary>
public static class PeriodCalculator
{
    /// <summary>
    /// Default-perioden vi viser til en ny bruker eller etter localStorage-clear:
    /// "forrige måned" relativt til <paramref name="today"/>.
    /// </summary>
    public static (DateTime From, DateTime To) ForrigeManed(DateTime today)
    {
        var firstThisMonth = new DateTime(today.Year, today.Month, 1);
        return (firstThisMonth.AddMonths(-1), firstThisMonth);
    }

    /// <summary>
    /// Periode for en gitt granularitet, relativt til <paramref name="today"/>.
    /// Brukes når brukeren trykker en hurtigknapp (M / Q / Å / YTD).
    /// </summary>
    public static (DateTime From, DateTime To) ForGranularity(PeriodGranularity g, DateTime today) =>
        g switch
        {
            PeriodGranularity.Maned => ForrigeManed(today),
            PeriodGranularity.Kvartal => ForrigeKvartal(today),
            PeriodGranularity.Ar => ForrigeAr(today),
            PeriodGranularity.HittilIAr => HittilIAr(today),
            PeriodGranularity.Siste7Dager => Siste7Dager(today),
            PeriodGranularity.IGar => IGar(today),
            // Egendefinert: vi har ingen mening — beholder eksisterende periode.
            // Caller må sørge for at de faktisk har et range satt.
            _ => ForrigeManed(today),
        };

    /// <summary>
    /// Siste 7 hele døgn fram til (men ikke med) i dag. Til-dato er eksklusiv.
    /// 10.6 → 3.6..10.6 (3.–9. juni inklusive). For drifts-leders ukessjekk.
    /// </summary>
    public static (DateTime From, DateTime To) Siste7Dager(DateTime today)
    {
        var start = today.Date;
        return (start.AddDays(-7), start);
    }

    /// <summary>I går — ett døgn. Til-dato er eksklusiv (i dag). 10.6 → 9.6..10.6.</summary>
    public static (DateTime From, DateTime To) IGar(DateTime today)
    {
        var start = today.Date;
        return (start.AddDays(-1), start);
    }

    /// <summary>Forrige hele kalenderkvartal. Mai 2026 → Q1 2026 (jan-mar).</summary>
    public static (DateTime From, DateTime To) ForrigeKvartal(DateTime today)
    {
        // Quarter 1 = Jan-Mar (start måned 1), Quarter 2 = Apr-Jun (start 4), osv.
        var currentQuarterStartMonth = ((today.Month - 1) / 3) * 3 + 1;
        var firstThisQuarter = new DateTime(today.Year, currentQuarterStartMonth, 1);
        return (firstThisQuarter.AddMonths(-3), firstThisQuarter);
    }

    /// <summary>Forrige hele kalenderår. 2026-05-05 → 2025-01-01..2026-01-01.</summary>
    public static (DateTime From, DateTime To) ForrigeAr(DateTime today)
    {
        var firstThisYear = new DateTime(today.Year, 1, 1);
        return (firstThisYear.AddYears(-1), firstThisYear);
    }

    /// <summary>Hittil-i-år: Jan 1 i dag. Til-dato er eksklusiv (i morgen).</summary>
    public static (DateTime From, DateTime To) HittilIAr(DateTime today)
    {
        var firstThisYear = new DateTime(today.Year, 1, 1);
        return (firstThisYear, today.AddDays(1));
    }

    /// <summary>
    /// Flytt periode-vinduet ETT trinn bakover av aktuell granularitet.
    /// Egendefinert + HittilIAr returnerer uendret — caller bør disable knappen.
    /// </summary>
    public static (DateTime From, DateTime To) MoveBack(
        DateTime from, DateTime to, PeriodGranularity g) =>
        g switch
        {
            PeriodGranularity.Maned => (from.AddMonths(-1), to.AddMonths(-1)),
            PeriodGranularity.Kvartal => (from.AddMonths(-3), to.AddMonths(-3)),
            PeriodGranularity.Ar => (from.AddYears(-1), to.AddYears(-1)),
            _ => (from, to),
        };

    /// <summary>
    /// Flytt periode-vinduet ETT trinn forover av aktuell granularitet.
    /// Egendefinert + HittilIAr returnerer uendret — caller bør disable knappen.
    /// </summary>
    public static (DateTime From, DateTime To) MoveForward(
        DateTime from, DateTime to, PeriodGranularity g) =>
        g switch
        {
            PeriodGranularity.Maned => (from.AddMonths(1), to.AddMonths(1)),
            PeriodGranularity.Kvartal => (from.AddMonths(3), to.AddMonths(3)),
            PeriodGranularity.Ar => (from.AddYears(1), to.AddYears(1)),
            _ => (from, to),
        };

    /// <summary>
    /// Tekst som vises i AppBar: "April 2026", "Q1 2026", "2026", "Hittil i år",
    /// "1.4–4.5.2026" (egendefinert).
    /// </summary>
    public static string Format(DateTime from, DateTime to, PeriodGranularity g)
    {
        return g switch
        {
            PeriodGranularity.Maned => FormatMonth(from),
            PeriodGranularity.Kvartal => FormatQuarter(from),
            PeriodGranularity.Ar => from.Year.ToString(CultureInfo.InvariantCulture),
            PeriodGranularity.HittilIAr => $"Hittil i år ({from.Year})",
            PeriodGranularity.Siste7Dager => "Siste 7 dager",
            PeriodGranularity.IGar => "I går",
            _ => FormatRange(from, to),
        };
    }

    private static string FormatMonth(DateTime from)
    {
        // Norsk månedsnavn — Blazor WASM kjører InvariantGlobalization=true,
        // så vi har egen oppslags-tabell heller enn DateTimeFormatInfo.
        var name = from.Month switch
        {
            1 => "Januar", 2 => "Februar", 3 => "Mars", 4 => "April",
            5 => "Mai", 6 => "Juni", 7 => "Juli", 8 => "August",
            9 => "September", 10 => "Oktober", 11 => "November", 12 => "Desember",
            _ => from.Month.ToString(CultureInfo.InvariantCulture),
        };
        return $"{name} {from.Year}";
    }

    private static string FormatQuarter(DateTime from)
    {
        var quarter = (from.Month - 1) / 3 + 1;
        return $"Q{quarter} {from.Year}";
    }

    private static string FormatRange(DateTime from, DateTime to)
    {
        // Til-dato er eksklusiv; vi viser den siste INKLUDERTE dagen i UIet
        // for at "1.5–31.5" er mer intuitivt enn "1.5–1.6"
        var lastIncluded = to.AddDays(-1);
        return $"{from.Day}.{from.Month}–{lastIncluded.Day}.{lastIncluded.Month}.{lastIncluded.Year}";
    }

}
