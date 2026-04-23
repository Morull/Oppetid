using System.Globalization;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Settlement.Quality;

/// <summary>
/// Bygger <see cref="DataQualityReport"/> for en parsed settlement og beriker
/// samtidig timeradene med <c>DqState</c>.
///
/// Ansvaret er delt i fire trinn:
///   1. Beregn forventet antall timer i perioden (bruker lokaltid og
///      DST-korrekt range; februar har alltid 28×24 = 672).
///   2. Finn manglende timer og sett dem inn som InformationUnavailable.
///   3. Kalkulér per-kolonne-statistikk (null-count, min, max, sum, mean).
///   4. Utled DqState per rad: mangler MwhElhub → InformationUnavailable,
///      negativ MwhElhub → Uncertain (kan være regulerkraft-kjøp eller måleavvik).
///
/// Returnerer et par (rapport, oppdaterte timerader) slik at kallsteder ikke
/// trenger å vedlikeholde to lister som må holdes synkronisert.
/// </summary>
public sealed class DataQualityReportBuilder
{
    public (DataQualityReport report, IReadOnlyList<SettlementHourlyRow> hourly) Build(ParsedSettlement parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var issues = parsed.Issues.ToList();
        var hourly = parsed.Hourly.ToList();

        // --- 1. Forventet antall timer (DST-sensitivt) ---
        int hoursExpected;
        IReadOnlyList<SettlementHourlyRow> enrichedHourly;
        if (hourly.Count == 0)
        {
            hoursExpected = 0;
            enrichedHourly = Array.Empty<SettlementHourlyRow>();
        }
        else
        {
            var (localStart, localEnd) = DetermineLocalBounds(hourly);
            hoursExpected = CountHoursInLocalRange(localStart, localEnd);
            enrichedHourly = InsertMissingHours(hourly, localStart, localEnd, issues);
        }

        var hoursReceived = hourly.Count;

        // --- 3. Per-kolonne-statistikk ---
        var columnStats = BuildColumnStats(enrichedHourly);

        // --- 4. DqState per rad ---
        var statefulHourly = ApplyDqState(enrichedHourly, issues);

        var hoursAccepted = statefulHourly.Count(r => r.DqState == DataQualityState.Good);
        var hoursFlagged = statefulHourly.Count(r =>
            r.DqState is DataQualityState.Uncertain
                or DataQualityState.Substituted
                or DataQualityState.InformationUnavailable);
        var hoursRejected = statefulHourly.Count(r =>
            r.DqState is DataQualityState.Quarantined
                or DataQualityState.Rejected);

        var report = new DataQualityReport
        {
            PlantName = parsed.PlantName,
            HoursExpected = hoursExpected,
            HoursReceived = hoursReceived,
            HoursAccepted = hoursAccepted,
            HoursFlagged = hoursFlagged,
            HoursRejected = hoursRejected,
            ColumnStats = columnStats,
            Issues = issues,
        };

        return (report, statefulHourly);
    }

    private static (DateTimeOffset start, DateTimeOffset end) DetermineLocalBounds(
        IReadOnlyList<SettlementHourlyRow> hourly)
    {
        var minLocal = hourly.Min(h => h.TimeLocal);
        var maxLocal = hourly.Max(h => h.TimeLocal);
        // Slutt er eksklusivt-bound – siste time + 1h
        return (minLocal, maxLocal.AddHours(1));
    }

    private static int CountHoursInLocalRange(DateTimeOffset localStart, DateTimeOffset localEnd)
    {
        // Bruk UTC-avstand + juster for DST. For februar blir dette alltid 672.
        var utcStart = localStart.ToUniversalTime();
        var utcEnd = localEnd.ToUniversalTime();
        return (int)Math.Round((utcEnd - utcStart).TotalHours);
    }

    private static IReadOnlyList<SettlementHourlyRow> InsertMissingHours(
        IReadOnlyList<SettlementHourlyRow> received,
        DateTimeOffset localStart,
        DateTimeOffset localEnd,
        List<ValidationIssue> issues)
    {
        var tz = TimeZones.Norway;
        var receivedUtc = received.Select(h => h.TimeUtc).ToHashSet();

        // Gå gjennom alle klokketimer mellom localStart og localEnd (lokaltid).
        var missing = new List<SettlementHourlyRow>();
        var cursor = localStart;
        while (cursor < localEnd)
        {
            var utc = cursor.ToUniversalTime();
            if (!receivedUtc.Contains(utc))
            {
                missing.Add(new SettlementHourlyRow
                {
                    TimeUtc = utc,
                    TimeLocal = cursor,
                    DqState = DataQualityState.InformationUnavailable,
                });
            }

            // Gå én time fram i lokaltid. AddHours på DateTimeOffset respekterer ikke
            // DST automatisk – vi må konvertere via TimeZoneInfo.
            cursor = AddOneHourInLocalTime(cursor, tz);
        }

        if (missing.Count > 0)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "HOURLY_MISSING_HOURS",
                $"{missing.Count} manglende timer i sekvensen – satt til InformationUnavailable",
                AffectedRows: missing.Count));
        }

        if (missing.Count == 0)
        {
            return received;
        }

        var combined = new List<SettlementHourlyRow>(received.Count + missing.Count);
        combined.AddRange(received);
        combined.AddRange(missing);
        combined.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return combined;
    }

    private static DateTimeOffset AddOneHourInLocalTime(DateTimeOffset currentLocal, TimeZoneInfo tz)
    {
        // Hopp én time fram i UTC (som alltid er +3600s), så oversett tilbake til lokaltid.
        // Dette håndterer DST korrekt: lokaltid kan hoppe 2→3 i mars, eller 3→2 i oktober.
        var nextUtc = currentLocal.ToUniversalTime().AddHours(1);
        var nextLocalNaive = TimeZoneInfo.ConvertTimeFromUtc(nextUtc.UtcDateTime, tz);
        var offset = tz.GetUtcOffset(nextUtc);
        return new DateTimeOffset(nextLocalNaive, offset);
    }

    private static IReadOnlyDictionary<string, ColumnStatistics> BuildColumnStats(
        IReadOnlyList<SettlementHourlyRow> hourly)
    {
        var stats = new Dictionary<string, ColumnStatistics>(StringComparer.Ordinal);
        AddStat(stats, "mwh_elhub", hourly, r => r.MwhElhub);
        AddStat(stats, "mwh_esett", hourly, r => r.MwhESett);
        AddStat(stats, "spotbud_mwh", hourly, r => r.SpotbudMwh);
        AddStat(stats, "spotpris_nok_mwh", hourly, r => r.SpotprisNokMwh);
        AddStat(stats, "ubalanse_mwh", hourly, r => r.UbalanseMwh);
        AddStat(stats, "rk_pris_nok_mwh", hourly, r => r.RkPrisNokMwh);
        AddStat(stats, "oppgjor_nok", hourly, r => r.OppgjorNok);
        AddStat(stats, "produksjonplan_mwh", hourly, r => r.ProduksjonplanMwh);
        AddStat(stats, "abs_ubalansevolum_mwh", hourly, r => r.AbsUbalansevolumMwh);
        AddStat(stats, "ubalanse_resultat_nok", hourly, r => r.UbalanseResultatNok);
        return stats;
    }

    private static void AddStat(
        Dictionary<string, ColumnStatistics> into,
        string columnName,
        IReadOnlyList<SettlementHourlyRow> hourly,
        Func<SettlementHourlyRow, double?> selector)
    {
        var values = hourly.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var nullCount = hourly.Count - values.Count;

        into[columnName] = new ColumnStatistics(
            ColumnName: columnName,
            DataType: "double?",
            NullCount: nullCount,
            NonNullCount: values.Count,
            Min: values.Count == 0 ? null : values.Min(),
            Max: values.Count == 0 ? null : values.Max(),
            Mean: values.Count == 0 ? null : values.Average(),
            Sum: values.Count == 0 ? null : values.Sum());
    }

    private static IReadOnlyList<SettlementHourlyRow> ApplyDqState(
        IReadOnlyList<SettlementHourlyRow> hourly,
        List<ValidationIssue> issues)
    {
        var negativeCount = 0;
        var result = new List<SettlementHourlyRow>(hourly.Count);

        foreach (var row in hourly)
        {
            // Manglende-timer har allerede fått InformationUnavailable i InsertMissingHours.
            if (row.DqState == DataQualityState.InformationUnavailable)
            {
                result.Add(row);
                continue;
            }

            DataQualityState state;
            if (row.MwhElhub is null)
            {
                state = DataQualityState.InformationUnavailable;
            }
            else if (row.MwhElhub < 0)
            {
                state = DataQualityState.Uncertain;
                negativeCount++;
            }
            else
            {
                state = DataQualityState.Good;
            }

            result.Add(row with { DqState = state });
        }

        if (negativeCount > 0)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "HOURLY_NEGATIVE_ELHUB",
                $"{negativeCount} timer med negativ MWh-Elhub – kan være regulerkraft-kjøp eller måleavvik",
                AffectedRows: negativeCount));
        }

        return result;
    }
}
