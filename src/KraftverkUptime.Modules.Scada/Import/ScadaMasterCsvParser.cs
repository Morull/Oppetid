using System.Globalization;
using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Import;

/// <summary>
/// Parser for Drivdal-format SCADA master-CSV.
///
/// Format (verifisert mot eksport-22-tags-avg-hour-...csv):
/// <code>
///   DateTime;Value (Cluster1.NAME1);Unit (Cluster1.NAME1);Value (Cluster1.NAME2);...
///   2026-02-01 10:00:00.000;151.0027;moh;0.084;m3/s;...
/// </code>
///
/// – BOM (UTF-8): håndtert via UTF8 reader-encoding
/// – Desimaltegn: komma (parses med <see cref="CultureInfo.InvariantCulture"/> etter erstatting)
/// – Skille mellom kolonner: semicolon (;)
/// – Tomme/NaN-verdier: blir <c>null</c>
/// – Tidssone: tidspunkter er lokal anlegg-tid (Europe/Oslo). Konverteres til UTC her.
///
/// Returnerer en stream av <see cref="ScadaSample"/> som kan batch-skrives via
/// <see cref="Repositories.IScadaSampleRepository.BulkInsertAsync"/>.
/// </summary>
public sealed class ScadaMasterCsvParser
{
    private static readonly Regex SignalNameRegex =
        new(@"^Value \(Cluster1\.(?<name>.+)\)$", RegexOptions.Compiled);

    /// <summary>
    /// Parser hele strømmen synkront og returnerer parsed-resultatet med samples
    /// gruppert per signal_id. Kasserer hele filen om første rad (header) er ugyldig.
    /// </summary>
    public ScadaParseResult Parse(string assetId, TextReader reader, TimeZoneInfo plantTimeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(plantTimeZone);

        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Tom CSV — manglende header-rad.");
        var headerCols = headerLine.Split(';');
        if (headerCols.Length < 3 || !headerCols[0].Trim().Equals("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Forventet header som starter med 'DateTime;Value (Cluster1.NAME1);Unit (...)'.");
        }

        // Bygg liste av (col-index, signal-id, unit-col-index)
        var signals = new List<(int ValueIdx, string SignalId, int UnitIdx)>();
        for (var i = 1; i < headerCols.Length; i++)
        {
            var col = headerCols[i].Trim();
            var match = SignalNameRegex.Match(col);
            if (!match.Success)
            {
                continue; // hopper over Unit-kolonner og ukjent format
            }
            var signalId = match.Groups["name"].Value;
            var unitIdx = i + 1; // Unit-kolonnen ligger rett etter
            signals.Add((i, signalId, unitIdx));
        }

        if (signals.Count == 0)
        {
            throw new InvalidDataException(
                "Ingen 'Value (Cluster1.NAME)'-kolonner funnet i header.");
        }

        var samples = new List<ScadaSample>();
        var rowsParsed = 0;
        var rowsSkipped = 0;
        var unitsBySignal = new Dictionary<string, string>(StringComparer.Ordinal);

        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(';');
            if (cols.Length < headerCols.Length)
            {
                rowsSkipped++;
                continue;
            }

            if (!TryParseTimestamp(cols[0], plantTimeZone, out var timestampUtc))
            {
                rowsSkipped++;
                continue;
            }

            foreach (var (valueIdx, signalId, unitIdx) in signals)
            {
                var rawValue = cols[valueIdx];
                var rawUnit = unitIdx < cols.Length ? cols[unitIdx] : "";

                if (!string.IsNullOrEmpty(rawUnit) && !unitsBySignal.ContainsKey(signalId))
                {
                    unitsBySignal[signalId] = rawUnit.Trim();
                }

                var (value, quality) = ParseValue(rawValue);
                samples.Add(new ScadaSample(assetId, signalId, timestampUtc, value, quality));
            }
            rowsParsed++;
        }

        return new ScadaParseResult(
            AssetId: assetId,
            SignalCount: signals.Count,
            RowsParsed: rowsParsed,
            RowsSkipped: rowsSkipped,
            Samples: samples,
            UnitsBySignal: unitsBySignal);
    }

    /// <summary>
    /// Parser tidspunkt i format <c>"2026-02-01 10:00:00.000"</c> som lokal anlegg-tid
    /// og konverterer til UTC. CSV-en har ikke tidssone-info — vi antar at eksport-en
    /// bruker anleggets konfigurerte tidssone (typisk Europe/Oslo).
    /// </summary>
    private static bool TryParseTimestamp(string raw, TimeZoneInfo tz, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (DateTime.TryParseExact(
                raw.Trim(),
                ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            // Mark as Unspecified, then attach plant tz, then convert to UTC.
            var unspec = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            try
            {
                utc = new DateTimeOffset(unspec, tz.GetUtcOffset(unspec)).ToUniversalTime();
                return true;
            }
            catch (ArgumentException)
            {
                // Tvetydig DST-tid — bruk standard offset
                utc = new DateTimeOffset(unspec, tz.BaseUtcOffset).ToUniversalTime();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parser en verdi-celle. Tom eller "NaN" → null + quality=2 (bad).
    /// Norsk komma byttes til punktum før parse. Quality=0 (good) for vellykket parse.
    /// </summary>
    private static (double? Value, short Quality) ParseValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, 2);
        var trimmed = raw.Trim();
        if (trimmed.Equals("NaN", StringComparison.OrdinalIgnoreCase)) return (null, 2);

        var normalized = trimmed.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return (v, 0);
        }
        return (null, 1); // uncertain — uventet format
    }
}

/// <summary>Resultat av en master-CSV-parse.</summary>
public sealed record ScadaParseResult(
    string AssetId,
    int SignalCount,
    int RowsParsed,
    int RowsSkipped,
    IReadOnlyList<ScadaSample> Samples,
    IReadOnlyDictionary<string, string> UnitsBySignal);
