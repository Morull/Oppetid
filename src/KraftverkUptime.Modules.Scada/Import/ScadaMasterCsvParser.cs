using System.Globalization;
using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Import;

/// <summary>
/// Parser for KraftScada master-CSV (time-aggregert tidsserie-eksport).
/// Anlegg-uavhengig — kolonne-prefikset (Cluster1.NAME) brukes til å
/// identifisere signal_id og er agnostisk for plantnavn.
///
/// Format (verifisert mot eksport-22-tags-avg-hour-...csv fra Drivdal):
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
        // StartsWith, ikke Equals: nyere eksport bruker "DateTime (UTC)" / "DateTime (Local)".
        if (headerCols.Length < 3 || !headerCols[0].Trim().StartsWith("DateTime", StringComparison.OrdinalIgnoreCase))
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
    /// Parser en multi-anlegg master-CSV der hver signal-kolonne mappes til
    /// en plant_id via <paramref name="signalToPlantId"/>. Brukes for filer
    /// som inneholder tags fra flere anlegg (eks. samlet eksport av Vikeså,
    /// Stølskraft, Ørsdalen, Øgreyfoss og Løgjen i ett dokument).
    ///
    /// Signaler som mapper til <c>null</c> (ukjent prefix) hoppes over og
    /// telles i <see cref="MultiPlantScadaParseResult.UnmappedSignals"/>.
    /// Hver sample får <c>asset_id</c> satt til plant_id-en som signal-prefiks
    /// mappet til, slik at OverflowQueryService og signal_map-oppslag fungerer
    /// likt som ved single-plant import.
    /// </summary>
    public MultiPlantScadaParseResult ParseMultiPlant(
        TextReader reader,
        TimeZoneInfo plantTimeZone,
        Func<string, string?> signalToPlantId)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(plantTimeZone);
        ArgumentNullException.ThrowIfNull(signalToPlantId);

        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Tom CSV — manglende header-rad.");
        var headerCols = headerLine.Split(';');
        // StartsWith, ikke Equals: nyere eksport bruker "DateTime (UTC)" / "DateTime (Local)".
        if (headerCols.Length < 3 || !headerCols[0].Trim().StartsWith("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Forventet header som starter med 'DateTime;Value (Cluster1.NAME1);Unit (...)'.");
        }

        // Bygg liste (col-index, signal-id, plant_id, unit-col-index). Ukjente
        // signal-prefikser (plantId=null) lagres i unmapped-lista for diagnose.
        var mappedSignals = new List<(int ValueIdx, string SignalId, string PlantId, int UnitIdx)>();
        var unmappedSignals = new List<string>();
        for (var i = 1; i < headerCols.Length; i++)
        {
            var col = headerCols[i].Trim();
            var match = SignalNameRegex.Match(col);
            if (!match.Success)
            {
                continue;
            }
            var signalId = match.Groups["name"].Value;
            var plantId = signalToPlantId(signalId);
            var unitIdx = i + 1;
            if (plantId is null)
            {
                unmappedSignals.Add(signalId);
                continue;
            }
            mappedSignals.Add((i, signalId, plantId, unitIdx));
        }

        if (mappedSignals.Count == 0)
        {
            throw new InvalidDataException(
                "Ingen signaler i CSV-en mapper til kjente plant-prefikser. " +
                $"Sett opp prefiks-mapping for: {string.Join(", ", unmappedSignals.Take(5))}");
        }

        var samplesByPlant = new Dictionary<string, List<ScadaSample>>(StringComparer.Ordinal);
        var signalCountByPlant = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unitsBySignal = new Dictionary<string, string>(StringComparer.Ordinal);
        var rowsParsed = 0;
        var rowsSkipped = 0;

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

            foreach (var (valueIdx, signalId, plantId, unitIdx) in mappedSignals)
            {
                var rawValue = cols[valueIdx];
                var rawUnit = unitIdx < cols.Length ? cols[unitIdx] : "";

                if (!string.IsNullOrEmpty(rawUnit) && !unitsBySignal.ContainsKey(signalId))
                {
                    unitsBySignal[signalId] = rawUnit.Trim();
                }

                var (value, quality) = ParseValue(rawValue);

                if (!samplesByPlant.TryGetValue(plantId, out var plantSamples))
                {
                    plantSamples = new List<ScadaSample>();
                    samplesByPlant[plantId] = plantSamples;
                }
                plantSamples.Add(new ScadaSample(plantId, signalId, timestampUtc, value, quality));

                if (!signalCountByPlant.TryGetValue(plantId, out var signalSet))
                {
                    signalSet = new HashSet<string>(StringComparer.Ordinal);
                    signalCountByPlant[plantId] = signalSet;
                }
                signalSet.Add(signalId);
            }
            rowsParsed++;
        }

        var perPlant = samplesByPlant
            .Select(kv => new PlantScadaParseResult(
                PlantId: kv.Key,
                SignalCount: signalCountByPlant[kv.Key].Count,
                Samples: kv.Value))
            .OrderBy(p => p.PlantId, StringComparer.Ordinal)
            .ToList();

        return new MultiPlantScadaParseResult(
            RowsParsed: rowsParsed,
            RowsSkipped: rowsSkipped,
            UnmappedSignals: unmappedSignals,
            UnitsBySignal: unitsBySignal,
            PerPlant: perPlant);
    }

    /// <summary>
    /// Parser tidspunkt fra master-CSV til UTC. Støtter to formater:
    /// <list type="bullet">
    ///   <item>ISO-8601 med eksplisitt sone (<c>"2026-05-01T00:00:00.000Z"</c> eller
    ///   <c>…±hh:mm</c>) — allerede zonet, parses direkte uten tz-konvertering.</item>
    ///   <item>Sone-løst lokaltids-format (<c>"2026-02-01 10:00:00.000"</c>) — antas å
    ///   være anleggets konfigurerte tidssone (typisk Europe/Oslo), konverteres til UTC.</item>
    /// </list>
    /// </summary>
    private static bool TryParseTimestamp(string raw, TimeZoneInfo tz, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();

        // Nytt eksportformat: ISO-8601 med eksplisitt tidssone (…T…Z eller …±hh:mm).
        // Strengen er allerede zonet → parse direkte, IKKE plant-tz-konverter.
        if (s.Contains('T') &&
            (s[^1] is 'Z' or 'z' || HasExplicitOffset(s)))
        {
            if (DateTimeOffset.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
            {
                utc = dto.ToUniversalTime();
                return true;
            }
            return false;
        }

        if (DateTime.TryParseExact(
                s,
                ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            // Mark as Unspecified, then attach plant tz, then convert to UTC.
            var unspec = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            // DST-spring: 02:00-02:59 eksisterer ikke i Europe/Oslo den siste
            // søndagen i mars. Skip dem framfor å mappe til samme UTC som 03:00.
            if (tz.IsInvalidTime(unspec))
            {
                return false;
            }

            try
            {
                utc = new DateTimeOffset(unspec, tz.GetUtcOffset(unspec)).ToUniversalTime();
                return true;
            }
            catch (ArgumentException)
            {
                // Tvetydig DST-tid (oktober) — bruk DST-offset (sommertid, første forekomst)
                if (tz.IsAmbiguousTime(unspec))
                {
                    var offsets = tz.GetAmbiguousTimeOffsets(unspec);
                    var pick = offsets[0];
                    foreach (var o in offsets) { if (o > pick) pick = o; }
                    utc = new DateTimeOffset(unspec, pick).ToUniversalTime();
                    return true;
                }
                utc = new DateTimeOffset(unspec, tz.BaseUtcOffset).ToUniversalTime();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sann hvis tids-delen (etter <c>T</c>) bærer en eksplisitt UTC-offset
    /// (<c>+hh:mm</c> eller <c>-hh:mm</c>). Ser bort fra bindestreker i dato-delen.
    /// </summary>
    private static bool HasExplicitOffset(string s)
    {
        var t = s.IndexOf('T');
        if (t < 0) return false;
        var tail = s.AsSpan(t);
        return tail.Contains('+') || tail.LastIndexOf('-') > 0; // '-' i tids-delen = offset
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

/// <summary>
/// Resultat av en multi-anlegg master-CSV-parse. Samples er splittet pr.
/// <c>asset_id</c>, slik at hver <see cref="PlantScadaParseResult"/> kan
/// batch-skrives uavhengig og logges som egen <c>data_imports</c>-rad.
/// </summary>
public sealed record MultiPlantScadaParseResult(
    int RowsParsed,
    int RowsSkipped,
    IReadOnlyList<string> UnmappedSignals,
    IReadOnlyDictionary<string, string> UnitsBySignal,
    IReadOnlyList<PlantScadaParseResult> PerPlant);

public sealed record PlantScadaParseResult(
    string PlantId,
    int SignalCount,
    IReadOnlyList<ScadaSample> Samples);
