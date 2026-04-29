using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Quality;

namespace KraftverkUptime.Modules.Settlement.Parsing;

/// <summary>
/// Detekterer og aggregerer 15-min portaleksporter til 60-min-rader.
///
/// Bakgrunn: Fra og med 2026 leverer enkelte portaler 15-min-data
/// (fire rader per time). Nedstrøms-pipelinen (klassifisering, KPI-er,
/// rapporter) er bygget rundt time-aggregat, så aggregeringen skjer her i
/// parser-laget for å holde det øvrige systemet uberørt.
///
/// Granularitets-detektering: kikker på differansene mellom de første tre
/// tids-radene. Alle 15-min → 15-min-modus; alle 60-min → 60-min-modus.
/// Annet behandles konservativt som 60-min med en advarsel.
///
/// Aggregeringsregler (per spec 2026-04-29):
///   – Energi-/markeds-volumer (MWh, NOK):       <b>sum</b> over de fire kvartal-radene
///   – Pris-felt (NOK/MWh):                      <b>volum-vektet snitt</b>
///                                              (Spotpris vektes mot Spotbud,
///                                               RkPris mot |Ubalanse|)
///   – Effektavlesninger (MW, momentanverdi):    <b>aritmetisk snitt</b>
///   – Tidsstempel:                              <b>første kvartals start-time</b>
///                                              (UTC-trunkert til hel klokketime)
///   – DqState:                                  beste (Good > Estimated > Bad)
///
/// Resultatet er identisk i form med dagens 60-min-output. Alle senere
/// validatorer (Elhub == eSett, summary-cross-check) opererer derfor uendret
/// på det aggregerte resultatet.
/// </summary>
public static class HourlyAggregator
{
    /// <summary>Forventet differanse for 15-min eksporter.</summary>
    public static readonly TimeSpan QuarterHour = TimeSpan.FromMinutes(15);

    /// <summary>Forventet differanse for 60-min eksporter.</summary>
    public static readonly TimeSpan FullHour = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Detekterer granularitet og aggregerer ved behov. Hvis input allerede
    /// er hourly returneres listen uendret (samme referanser).
    /// </summary>
    public static IReadOnlyList<SettlementHourlyRow> Process(
        IReadOnlyList<SettlementHourlyRow> rows,
        ICollection<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(issues);

        if (rows.Count == 0)
        {
            return rows;
        }

        var granularity = DetectGranularity(rows);
        switch (granularity)
        {
            case GranularityMinutes.Hourly:
                return rows;

            case GranularityMinutes.QuarterHourly:
                return AggregateQuarterToHourly(rows, issues);

            default:
                // Ukjent — logg en advarsel og send rader videre uendret.
                // Klassifikatoren har allerede toleranse for hull/ujevn data.
                issues.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    "GRANULARITY_UNKNOWN",
                    "Kunne ikke fastslå tids-granularitet (15- eller 60-min). " +
                    "Forutsetter hourly og leverer rader uendret."));
                return rows;
        }
    }

    public enum GranularityMinutes
    {
        Unknown = 0,
        QuarterHourly = 15,
        Hourly = 60,
    }

    /// <summary>
    /// Detekterer granularitet ut fra de første tre tids-radene. Krever at
    /// alle observerte differanser er identiske og lik 15 eller 60 min.
    /// </summary>
    public static GranularityMinutes DetectGranularity(IReadOnlyList<SettlementHourlyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count < 2)
        {
            // Med kun én rad finnes ingen differanse — fall tilbake til hourly
            // som er det historiske formatet og det safest fall-back.
            return GranularityMinutes.Hourly;
        }

        var sample = Math.Min(3, rows.Count);
        var diffs = new List<TimeSpan>(sample - 1);
        for (var i = 1; i < sample; i++)
        {
            diffs.Add(rows[i].TimeUtc - rows[i - 1].TimeUtc);
        }

        if (diffs.TrueForAll(d => d == QuarterHour))
        {
            return GranularityMinutes.QuarterHourly;
        }
        if (diffs.TrueForAll(d => d == FullHour))
        {
            return GranularityMinutes.Hourly;
        }
        return GranularityMinutes.Unknown;
    }

    private static IReadOnlyList<SettlementHourlyRow> AggregateQuarterToHourly(
        IReadOnlyList<SettlementHourlyRow> rows,
        ICollection<ValidationIssue> issues)
    {
        // Gruppér på UTC-time slik at DST-overgangen i oktober (samme lokal-
        // klokkeslett to ganger) fortsatt gir to forskjellige hour-bucketer.
        var groups = new Dictionary<DateTimeOffset, List<SettlementHourlyRow>>();
        foreach (var r in rows)
        {
            var hourKey = TruncateToHour(r.TimeUtc);
            if (!groups.TryGetValue(hourKey, out var bucket))
            {
                bucket = new List<SettlementHourlyRow>(4);
                groups[hourKey] = bucket;
            }
            bucket.Add(r);
        }

        var partialHours = 0;
        var aggregated = new List<SettlementHourlyRow>(groups.Count);
        foreach (var (hour, bucket) in groups.OrderBy(kv => kv.Key))
        {
            // Forventer 4 kvartal per time. Hvis færre, aggreger likevel og
            // flagg en advarsel slik at brukeren ser at perioden var ujevn.
            if (bucket.Count != 4)
            {
                partialHours++;
            }
            aggregated.Add(AggregateBucket(hour, bucket));
        }

        if (partialHours > 0)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "QUARTERLY_INCOMPLETE_HOUR",
                $"{partialHours} time(r) hadde færre enn 4 kvartal-rader i 15-min-eksporten — aggregert med tilgjengelige rader.",
                AffectedRows: partialHours));
        }

        return aggregated;
    }

    private static SettlementHourlyRow AggregateBucket(
        DateTimeOffset hourKey,
        IReadOnlyList<SettlementHourlyRow> bucket)
    {
        // Bevar lokal offset fra første kvartal — den er allerede DST-tolket.
        var firstLocal = bucket[0].TimeLocal;
        var localHour = TruncateToHour(firstLocal);

        return new SettlementHourlyRow
        {
            TimeUtc = hourKey,
            TimeLocal = localHour,

            // Energi: sum
            MwhElhub = SumNullable(bucket, r => r.MwhElhub),
            MwhESett = SumNullable(bucket, r => r.MwhESett),

            // Marked: volum-sum, pris vektet på Spotbud (volumet det er handlet)
            SpotbudMwh = SumNullable(bucket, r => r.SpotbudMwh),
            SpotprisNokMwh = WeightedAverage(bucket, r => r.SpotprisNokMwh, r => r.SpotbudMwh),
            SpotomsetningNok = SumNullable(bucket, r => r.SpotomsetningNok),

            // Ubalanse: volum-sum, pris vektet på |Ubalanse|
            UbalanseMwh = SumNullable(bucket, r => r.UbalanseMwh),
            RkPrisNokMwh = WeightedAverage(bucket, r => r.RkPrisNokMwh, r => AbsOrNull(r.UbalanseMwh)),
            RkKjopNok = SumNullable(bucket, r => r.RkKjopNok),
            RkSalgNok = SumNullable(bucket, r => r.RkSalgNok),

            // Gebyrer og oppgjør: sum
            NordPoolGebyrNok = SumNullable(bucket, r => r.NordPoolGebyrNok),
            ESettVolumgebyrNok = SumNullable(bucket, r => r.ESettVolumgebyrNok),
            ESettUbalansegebyrNok = SumNullable(bucket, r => r.ESettUbalansegebyrNok),
            SumSalgNok = SumNullable(bucket, r => r.SumSalgNok),
            MeglerprovisjonNok = SumNullable(bucket, r => r.MeglerprovisjonNok),
            OppgjorNok = SumNullable(bucket, r => r.OppgjorNok),

            // Utvidede kolonner
            BruttoOmsetningNok = SumNullable(bucket, r => r.BruttoOmsetningNok),
            ProduksjonplanMwh = SumNullable(bucket, r => r.ProduksjonplanMwh),
            // Effektavlesninger er momentanverdi (MW) → snitt, ikke sum
            EffektavlesningerMw = SimpleAverage(bucket, r => r.EffektavlesningerMw),
            AbsUbalansevolumMwh = SumNullable(bucket, r => r.AbsUbalansevolumMwh),
            UbalanseResultatNok = SumNullable(bucket, r => r.UbalanseResultatNok),

            // DqState: beste verdi vinner (Good > Estimated > Bad)
            DqState = bucket.Min(r => r.DqState),
        };
    }

    private static double? SumNullable(
        IReadOnlyList<SettlementHourlyRow> bucket,
        Func<SettlementHourlyRow, double?> selector)
    {
        double sum = 0;
        var hasAny = false;
        foreach (var r in bucket)
        {
            var v = selector(r);
            if (v.HasValue)
            {
                sum += v.Value;
                hasAny = true;
            }
        }
        return hasAny ? sum : null;
    }

    private static double? SimpleAverage(
        IReadOnlyList<SettlementHourlyRow> bucket,
        Func<SettlementHourlyRow, double?> selector)
    {
        double sum = 0;
        var count = 0;
        foreach (var r in bucket)
        {
            var v = selector(r);
            if (v.HasValue)
            {
                sum += v.Value;
                count++;
            }
        }
        return count == 0 ? null : sum / count;
    }

    private static double? WeightedAverage(
        IReadOnlyList<SettlementHourlyRow> bucket,
        Func<SettlementHourlyRow, double?> valueSelector,
        Func<SettlementHourlyRow, double?> weightSelector)
    {
        double weightedSum = 0;
        double totalWeight = 0;
        var hasValue = false;

        foreach (var r in bucket)
        {
            var v = valueSelector(r);
            if (!v.HasValue) continue;
            hasValue = true;

            var w = weightSelector(r);
            if (!w.HasValue || w.Value == 0)
            {
                continue; // hopp over kvartal med null/0 vekt — bidrar ikke
            }

            weightedSum += v.Value * w.Value;
            totalWeight += w.Value;
        }

        if (!hasValue) return null;
        if (totalWeight == 0)
        {
            // Verdier finnes, men ingen vekt — fall tilbake til simple average
            return SimpleAverage(bucket, valueSelector);
        }
        return weightedSum / totalWeight;
    }

    private static double? AbsOrNull(double? v) => v.HasValue ? Math.Abs(v.Value) : null;

    private static DateTimeOffset TruncateToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        var trunc = new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
        return new DateTimeOffset(trunc, TimeSpan.Zero).ToOffset(t.Offset);
    }
}
