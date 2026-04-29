using System.Collections.Concurrent;

namespace KraftverkUptime.Core.Time;

/// <summary>
/// Beregner og slår opp norske offentlige helligdager (røde dager) for et gitt år.
///
/// Faste datoer:
///   1. januar (nyttårsdag), 1. mai (off. høytidsdag), 17. mai (grunnlovsdag),
///   25. desember (1. juledag), 26. desember (2. juledag).
///
/// Bevegelige datoer (relativ til påskedag):
///   – Skjærtorsdag = påskedag − 3
///   – Langfredag   = påskedag − 2
///   – 2. påskedag  = påskedag + 1
///   – Kr.himmelfartsdag = påskedag + 39
///   – 1. pinsedag  = påskedag + 49
///   – 2. pinsedag  = påskedag + 50
///
/// Påskedag beregnes med Anonymous Gregorian Easter (Meeus/Jones/Butcher).
/// 1. nyttårsdag og 1. juledag overlapper med faste datoer; det håndteres
/// ved at vi returnerer en HashSet som naturlig deduperer.
/// </summary>
public static class NorgesHelligdager
{
    private static readonly ConcurrentDictionary<int, IReadOnlySet<DateOnly>> Cache = new();

    /// <summary>
    /// Returnerer alle norske offentlige helligdager for ett år.
    /// Resultatet er cached per år (immutable HashSet).
    /// </summary>
    public static IReadOnlySet<DateOnly> ForYear(int year)
    {
        return Cache.GetOrAdd(year, BuildSet);
    }

    /// <summary>
    /// Sjekker om gitt dato (lokal-tid) er en norsk offentlig helligdag.
    /// Søndager regnes IKKE som helligdag her — bruk
    /// <see cref="ErHelligdagEllerSondag(DateOnly)"/> hvis du vil inkludere søndager.
    /// </summary>
    public static bool ErHelligdag(DateOnly dato)
    {
        return ForYear(dato.Year).Contains(dato);
    }

    /// <summary>
    /// Helligdag eller søndag. Brukes til vakt-tids-modellen der hele helger
    /// + helligdager er innenfor vakt-vindu.
    /// </summary>
    public static bool ErHelligdagEllerSondag(DateOnly dato)
    {
        if (dato.DayOfWeek == DayOfWeek.Sunday) return true;
        return ErHelligdag(dato);
    }

    /// <summary>
    /// Påskedag (Easter Sunday) for gitt år, etter Anonymous Gregorian-algoritmen.
    /// Validert mot kjent fasit:
    ///   2025: 20. april, 2026: 5. april, 2027: 28. mars.
    /// </summary>
    public static DateOnly Paskedag(int year)
    {
        // Anonymous Gregorian (Meeus/Jones/Butcher)
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static IReadOnlySet<DateOnly> BuildSet(int year)
    {
        var paske = Paskedag(year);

        var dager = new HashSet<DateOnly>
        {
            // Faste
            new(year, 1, 1),    // 1. nyttårsdag
            new(year, 5, 1),    // off. høytidsdag (arbeidernes dag)
            new(year, 5, 17),   // grunnlovsdag
            new(year, 12, 25),  // 1. juledag
            new(year, 12, 26),  // 2. juledag

            // Bevegelige (relativt til påskedag)
            paske.AddDays(-3),  // skjærtorsdag
            paske.AddDays(-2),  // langfredag
            paske,              // 1. påskedag
            paske.AddDays(1),   // 2. påskedag
            paske.AddDays(39),  // Kristi himmelfartsdag
            paske.AddDays(49),  // 1. pinsedag
            paske.AddDays(50),  // 2. pinsedag
        };

        return dager;
    }
}
