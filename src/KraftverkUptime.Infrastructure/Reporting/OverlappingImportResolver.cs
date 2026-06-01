namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Kollapser settlement-importer som overlapper i tid, slik at hver logiske
/// periode telles nøyaktig én gang ved aggregering.
///
/// Bakgrunn: tidligere dedup grupperte kun på EKSAKT lik
/// <c>(PeriodStart, PeriodEnd)</c>. Når samme måned ble re-importert med litt
/// ulik datospenn — f.eks. «mai 1–17» og senere «mai 1–20» — havnet de i hver
/// sin gruppe og BEGGE ble summert. Den korte importen er fullstendig inneholdt
/// i den lange, så spotomsetning/oppgjør/KAIA for de overlappende dagene ble
/// dobbelttalt (bekreftet live 2026-05-30: Vikeså mai talt to ganger).
///
/// Denne resolveren sorterer importene på startdato og slår sammen alle som
/// overlapper. For hver overlapps-klynge beholdes ÉN representant: den med
/// størst dekning (lengst varighet), og ved lik varighet den nyeste importen
/// (<c>ImportedAtUtc</c>) — samme «reimport vinner»-semantikk som før, men nå
/// også for ikke-identiske, overlappende spenn.
///
/// Forutsetning: hver import representerer hele sin egen periode som ett tall
/// (v1: én import = én måned, evt. en voksende inneværende måned). For ekte
/// delvis-overlapp mellom to ulike datasett trengs time-nivå-deduplisering;
/// det er utenfor v1.
/// </summary>
public static class OverlappingImportResolver
{
    /// <summary>
    /// Returnerer en delmengde av <paramref name="imports"/> uten overlappende
    /// perioder. Rekkefølgen er etter startdato.
    /// </summary>
    /// <typeparam name="T">Import-typen (entitet eller record).</typeparam>
    /// <param name="imports">Importer for ETT anlegg. Kall per anlegg når flere er involvert.</param>
    /// <param name="start">Velger periodens start (UTC).</param>
    /// <param name="end">Velger periodens slutt (UTC).</param>
    /// <param name="importedAt">Velger import-tidspunkt (UTC), brukt som tie-break.</param>
    public static IReadOnlyList<T> ResolveNonOverlapping<T>(
        IEnumerable<T> imports,
        Func<T, DateTimeOffset> start,
        Func<T, DateTimeOffset> end,
        Func<T, DateTimeOffset> importedAt)
    {
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(importedAt);

        // Sorter på start stigende, deretter slutt synkende slik at den bredeste
        // importen i en klynge møtes først.
        var ordered = imports
            .OrderBy(start)
            .ThenByDescending(end)
            .ToList();

        var result = new List<T>(ordered.Count);
        DateTimeOffset clusterEnd = default;

        foreach (var item in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(item);
                clusterEnd = end(item);
                continue;
            }

            // Overlapp mot gjeldende klynge når neste start kommer før klyngens slutt.
            if (start(item) < clusterEnd)
            {
                var current = result[^1];
                result[^1] = PickRepresentative(current, item, start, end, importedAt);
                if (end(item) > clusterEnd)
                {
                    clusterEnd = end(item);
                }
            }
            else
            {
                result.Add(item);
                clusterEnd = end(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Velg representanten for en overlapps-klynge: størst dekning vinner,
    /// deretter nyeste import.
    /// </summary>
    private static T PickRepresentative<T>(
        T a, T b,
        Func<T, DateTimeOffset> start,
        Func<T, DateTimeOffset> end,
        Func<T, DateTimeOffset> importedAt)
    {
        var durA = end(a) - start(a);
        var durB = end(b) - start(b);
        if (durB > durA) return b;
        if (durA > durB) return a;
        return importedAt(b) >= importedAt(a) ? b : a;
    }
}
