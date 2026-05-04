using System.Text;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Settlement.Quality;

/// <summary>
/// Bygger menneske-lesbart notes-felt for <c>data_imports.notes</c> ut fra
/// settlement-import-resultatet. Inkluderer:
///   - Hydrogrid-plan-status (antall timer med produksjonplan)
///   - Per-issue-detaljer gruppert på Code (DST_NONEXISTENT, ELHUB_ESETT_MISMATCH osv.)
///   - Severity-prefiks så drifts-leder kan filtrere på alvorlighet
///
/// Sentralisert her slik at single-plant og multi-plant import-stier produserer
/// identisk formattering. Truncates til <see cref="MaxNotesLength"/> for å unngå
/// at en "støyende" import (mange duplikate avvik) sprenger notes-feltet.
/// </summary>
public static class SettlementImportNotesBuilder
{
    /// <summary>Maks lengde på notes-feltet før truncation. Unngår DB-overload.</summary>
    public const int MaxNotesLength = 2000;

    /// <summary>
    /// Lager notes-tekst for en parset settlement. Returnerer null hvis det
    /// ikke er noe meningsfullt å rapportere.
    /// </summary>
    public static string? Build(ParsedSettlement parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // 1) Hydrogrid-plan-status (KAIA inkluderer Produksjonplan-kolonnen
        // i samme fila — vi sporer det her for synlighet i historikken).
        var planRows = parsed.Hourly.Count(r => r.ProduksjonplanMwh.HasValue);
        if (planRows > 0)
        {
            sb.Append("Hydrogrid-plan: ").Append(planRows)
              .Append('/').Append(parsed.Hourly.Count).Append(" timer.");
        }

        // 2) Issues — gruppert på Code så duplikate koder kollapses.
        if (parsed.Issues.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(parsed.Issues.Count).Append(" avvik:");

            var groups = parsed.Issues
                .GroupBy(i => (i.Severity, i.Code))
                .OrderByDescending(g => g.Key.Severity)
                .ThenByDescending(g => g.Count());

            foreach (var group in groups)
            {
                sb.AppendLine();
                sb.Append("  • ");
                sb.Append('[').Append(SeverityShort(group.Key.Severity)).Append("] ");
                sb.Append(group.Key.Code);
                if (group.Count() > 1)
                {
                    sb.Append(" (").Append(group.Count()).Append("×)");
                }
                // Vis første message — duplikate avvik har gjerne samme tekst
                var first = group.First();
                if (!string.IsNullOrEmpty(first.Message))
                {
                    sb.Append(": ").Append(Truncate(first.Message, 120));
                }
                if (first.AffectedRows is { } rows && rows > 1)
                {
                    sb.Append(" — ").Append(rows).Append(" rader påvirket");
                }
            }
        }

        if (sb.Length == 0) return null;
        return Truncate(sb.ToString(), MaxNotesLength);
    }

    private static string SeverityShort(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "ERR",
        IssueSeverity.Warning => "WARN",
        IssueSeverity.Info => "INFO",
        _ => "?",
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        // Sett av plass til "…" innenfor max-grensen så total lengde aldri overskrider max.
        return string.Concat(s.AsSpan(0, Math.Max(0, max - 1)), "…");
    }
}
