using System.Text.RegularExpressions;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// Detekterer filtype + anlegg-tilhørighet for filer i hot-folder.
/// Strategi: filnavn-regex først (rask), content-sniff fallback for
/// SCADA-CSV-er som ikke har plant i navnet.
///
/// Filtype-mapping (drifts-leders 2026-05-03-bekreftelse):
///   .xlsx → settlement (KAIA-eksport)
///   .csv  → SCADA-trender (master) eller SCADA-alarmer (operlog)
///           — operlog detekteres via "operlog" / "alarm" i filnavn
///             eller "Tidsstempel"/"Hendelse" i header.
/// </summary>
public sealed class HotFolderDetector
{
    private readonly HotFolderOptions _options;

    // Regex som plukker plant-navn fra filnavn. Eksempler:
    //   "Avregning Drivdal April 2026.xlsx" → drivdal
    //   "drivdal_settlement_2026-04.xlsx" → drivdal
    //   "export-117-tags-...HONNE.csv" — content-sniff i stedet
    private static readonly Regex[] FilenamePlantPatterns =
    [
        new(@"^(?<plant>drivdal|lindland|haukland|honnefoss|liavatn|grodemfoss|ogreyfoss|orsdalen|vikesa|stolskraft|logjen)[_\-\.]",
            RegexOptions.IgnoreCase),
        new(@"[_\-](?<plant>drivdal|lindland|haukland|honnefoss|liavatn|grodemfoss|ogreyfoss|orsdalen|vikesa|stolskraft|logjen)[_\-\.]",
            RegexOptions.IgnoreCase),
        new(@"Avregning\s+(?<plant>\w+)\s",
            RegexOptions.IgnoreCase),
    ];

    public HotFolderDetector(HotFolderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public DetectionResult Detect(FileInfo file)
    {
        var ext = file.Extension.ToLowerInvariant();
        var name = file.Name;

        // 1. Filtype basert på extension + navn
        SourceType? sourceType = ext switch
        {
            ".xlsx" => SourceType.Settlement,
            ".xls" => SourceType.Settlement,
            ".csv" => DetectCsvType(name, file),
            _ => null,
        };

        if (sourceType is null)
        {
            return DetectionResult.Unknown(
                $"Ukjent filtype: {ext}. Forventer .xlsx (settlement) eller .csv (SCADA).");
        }

        // 2a. Settlement: sjekk om det er multi-plant (≥ 2 plant-faner i workbook)
        if (sourceType == SourceType.Settlement)
        {
            var sheetCount = TryCountPlantSheets(file);
            if (sheetCount >= 2)
            {
                // Multi-plant: ruter til /settlements/multi-plant — selve
                // splittingen per plant skjer i parsing-laget.
                return DetectionResult.Ok(plantId: "_multi_", SourceType.SettlementMultiPlant);
            }
        }

        // 2b. Plant — filnavn-regex først
        var plantId = DetectPlantFromFilename(name);

        // 3. Content-sniff fallback hvis filnavn ikke ga svar
        if (plantId is null)
        {
            if (sourceType == SourceType.Settlement)
            {
                // Single-plant settlement: les plant-navn fra fane-navn / R1 i xlsx
                plantId = DetectPlantFromXlsxContent(file);
            }
            else if (sourceType == SourceType.ScadaTrends)
            {
                // SCADA-trends: tag-prefiks (Cluster1.PREFIKS_) i header
                plantId = DetectPlantFromCsvContent(file);
            }
            else if (sourceType == SourceType.ScadaAlarms)
            {
                // Operlog: 'station'-kolonnen i hver rad — kan være multi-plant
                var stationResult = DetectPlantsFromOperlog(file);
                if (stationResult.IsMultiPlant)
                {
                    return DetectionResult.Ok("_multi_", SourceType.ScadaAlarmsMultiPlant);
                }
                plantId = stationResult.DominantPlant;
            }
        }

        if (plantId is null)
        {
            return DetectionResult.Unknown(
                $"Klarte ikke identifisere anlegg for fil '{name}'. " +
                $"Forventer plant-navn i filnavnet, fane-navn i workbook, eller SCADA-tag-prefiks i header.");
        }

        return DetectionResult.Ok(plantId, sourceType.Value);
    }

    /// <summary>
    /// For single-plant xlsx: les plant-navn fra første ikke-aggregat-fane.
    /// Format-konvensjonen i KAIA-eksporten har en fane per anlegg, og
    /// fane-navnet er ASCII-strippet plant-navn (eks. "Drivdal", "Logjen").
    /// Slugifiserer til kanonisk plant-id via samme tabell som filnavn-regex.
    /// </summary>
    private string? DetectPlantFromXlsxContent(FileInfo file)
    {
        try
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook(file.FullName);

            // Prøv hver synlig ikke-aggregat-fane
            foreach (var ws in workbook.Worksheets.Where(w =>
                w.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible
                && !IsAggregateSheetName(w.Name)))
            {
                // 1. Selve fane-navnet
                var fromSheetName = SlugifyPlantName(ws.Name);
                if (fromSheetName is not null) return fromSheetName;

                // 2. Cell A1 / B1 / C1 — KAIA-eksport har gjerne plant-navn med
                //    norske tegn i en av disse cellene
                for (var col = 1; col <= 5; col++)
                {
                    var raw = ws.Cell(1, col).GetString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    var slug = SlugifyPlantName(raw);
                    if (slug is not null) return slug;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Map plant-navn (med eller uten norske tegn) til kanonisk plant-id.
    /// </summary>
    private static string? SlugifyPlantName(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "drivdal" => "drivdal",
            "lindland" => "lindland",
            "haukland" => "haukland",
            "honnefoss" => "honnefoss",
            "liavatn" => "liavatn",
            "løgjen" or "logjen" => "logjen",
            "grødemfoss" or "grodemfoss" => "grodemfoss",
            "øgreyfoss" or "ogreyfoss" => "ogreyfoss",
            "ørsdalen" or "orsdalen" => "orsdalen",
            "vikeså" or "vikesa" => "vikesa",
            "stølskraft" or "stolskraft" => "stolskraft",
            _ => null,
        };
    }

    /// <summary>
    /// Operlog-CSV har 'station'-kolonnen som inneholder anleggsnavn per rad.
    /// Vi leser de første 200 radene og teller stasjoner. Hvis 2+ unike
    /// stasjoner med signifikant volum (≥ 10 % hver) → multi-plant.
    /// </summary>
    private OperlogPlantDetectResult DetectPlantsFromOperlog(FileInfo file)
    {
        try
        {
            using var reader = new StreamReader(file.FullName);
            var headerLine = reader.ReadLine();
            if (headerLine is null) return OperlogPlantDetectResult.None;

            // Finn station-kolonneindeks. Standard operlog-format:
            // timestamp;station;username;tag;text;value;operatorType;...
            var headers = headerLine.Split(';');
            var stationIdx = -1;
            for (var i = 0; i < headers.Length; i++)
            {
                if (headers[i].Trim().Equals("station", StringComparison.OrdinalIgnoreCase))
                {
                    stationIdx = i;
                    break;
                }
            }
            if (stationIdx < 0) return OperlogPlantDetectResult.None;

            var stationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < 500; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                var parts = line.Split(';');
                if (parts.Length <= stationIdx) continue;
                var station = parts[stationIdx].Trim();
                if (string.IsNullOrEmpty(station)) continue;
                stationCounts[station] = stationCounts.GetValueOrDefault(station) + 1;
            }
            if (stationCounts.Count == 0) return OperlogPlantDetectResult.None;

            // Map station-navn til plant-id via slug + PlantPrefixMap
            var totalRows = stationCounts.Values.Sum();
            var plantsBySlug = stationCounts
                .Select(kv => (Plant: SlugifyStation(kv.Key), Count: kv.Value))
                .Where(t => t.Plant is not null)
                .GroupBy(t => t.Plant!)
                .Select(g => (Plant: g.Key, Total: g.Sum(t => t.Count)))
                .ToList();

            if (plantsBySlug.Count == 0) return OperlogPlantDetectResult.None;
            if (plantsBySlug.Count == 1)
            {
                return new OperlogPlantDetectResult(
                    DominantPlant: plantsBySlug[0].Plant,
                    IsMultiPlant: false);
            }

            // Multi-plant hvis ≥ 2 plants har > 10 % av radene
            var significantPlants = plantsBySlug.Count(p => p.Total >= totalRows * 0.10);
            if (significantPlants >= 2)
            {
                return new OperlogPlantDetectResult(DominantPlant: null, IsMultiPlant: true);
            }

            // Bare én plant er signifikant — bruk den
            var top = plantsBySlug.OrderByDescending(p => p.Total).First();
            return new OperlogPlantDetectResult(DominantPlant: top.Plant, IsMultiPlant: false);
        }
        catch
        {
            return OperlogPlantDetectResult.None;
        }
    }

    /// <summary>
    /// Mapper station-navn fra operlog (eks. "Haukland", "Drivdal") til
    /// kanonisk plant-id. Bruker case-insensitive direkte-match først,
    /// så slug-konvertering for norske tegn.
    /// </summary>
    private string? SlugifyStation(string station)
    {
        var lower = station.Trim().ToLowerInvariant();
        var slug = lower switch
        {
            "drivdal" => "drivdal",
            "lindland" => "lindland",
            "haukland" => "haukland",
            "honnefoss" => "honnefoss",
            "liavatn" => "liavatn",
            "løgjen" or "logjen" => "logjen",
            "grødemfoss" or "grodemfoss" => "grodemfoss",
            "øgreyfoss" or "ogreyfoss" => "ogreyfoss",
            "ørsdalen" or "orsdalen" => "orsdalen",
            "vikeså" or "vikesa" => "vikesa",
            "stølskraft" or "stolskraft" => "stolskraft",
            _ => null,
        };
        if (slug is not null) return slug;

        // Fallback: prøv PlantPrefixMap (eks. "HONNE" → "honnefoss")
        return _options.PlantPrefixMap.TryGetValue(station.Trim().ToUpperInvariant(), out var mapped)
            ? mapped : null;
    }

    private sealed record OperlogPlantDetectResult(string? DominantPlant, bool IsMultiPlant)
    {
        public static OperlogPlantDetectResult None => new(null, false);
    }

    /// <summary>
    /// Forsøker å telle hvor mange plant-faner en xlsx har. Bruker minimal
    /// open av workbook-en (kun ZIP-direktoriet). Returner -1 ved feil.
    /// </summary>
    private static int TryCountPlantSheets(FileInfo file)
    {
        try
        {
            // ClosedXML er allerede en avhengighet via Settlement-modulen.
            // Vi vil ikke åpne hele workbooket — bruker minimal sheet-count.
            using var workbook = new ClosedXML.Excel.XLWorkbook(file.FullName);
            // Filter ut "Summering" / "Sammendrag" / "Info"-faner som ikke
            // er plant-data. Her: telle alle synlige faner som "tellbare"
            // og la parser-laget gjøre den endelige seleksjonen.
            var plantLikeSheets = workbook.Worksheets.Count(ws =>
                ws.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible
                && !IsAggregateSheetName(ws.Name));
            return plantLikeSheets;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsAggregateSheetName(string name) =>
        name.Equals("Summering", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sammendrag", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Info", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Forside", StringComparison.OrdinalIgnoreCase);

    private SourceType? DetectCsvType(string fileName, FileInfo file)
    {
        // Operlog: "operlog" / "alarm" / "alarms" i filnavn
        if (fileName.Contains("operlog", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("alarm", StringComparison.OrdinalIgnoreCase))
        {
            return SourceType.ScadaAlarms;
        }

        // Content-sniff første linje for å bekrefte
        try
        {
            using var reader = new StreamReader(file.FullName);
            var firstLine = reader.ReadLine() ?? "";
            if (firstLine.Contains("Tidsstempel", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Hendelse", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Event", StringComparison.OrdinalIgnoreCase))
            {
                return SourceType.ScadaAlarms;
            }
            // SCADA master: "DateTime" + "Cluster1." typisk
            if (firstLine.Contains("DateTime", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Cluster1.", StringComparison.OrdinalIgnoreCase))
            {
                return SourceType.ScadaTrends;
            }
        }
        catch
        {
            // Ignorer — fallback under
        }

        // Default for .csv: SCADA trender (master)
        return SourceType.ScadaTrends;
    }

    private string? DetectPlantFromFilename(string fileName)
    {
        foreach (var pattern in FilenamePlantPatterns)
        {
            var m = pattern.Match(fileName);
            if (m.Success)
            {
                var raw = m.Groups["plant"].Value.ToLowerInvariant();
                // Mapping for ASCII-stripet navn → kanonisk plant-id
                return raw switch
                {
                    "logjen" or "løgjen" => "logjen",
                    "grodemfoss" or "grødemfoss" => "grodemfoss",
                    "ogreyfoss" or "øgreyfoss" => "ogreyfoss",
                    "orsdalen" or "ørsdalen" => "orsdalen",
                    "vikesa" or "vikeså" => "vikesa",
                    _ => raw,
                };
            }
        }
        return null;
    }

    private string? DetectPlantFromCsvContent(FileInfo file)
    {
        try
        {
            using var reader = new StreamReader(file.FullName);
            var firstLine = reader.ReadLine() ?? "";

            // Tagger har typisk form "Cluster1.HONNE_SOMETHING" — tell prefikser
            var prefixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(firstLine, @"Cluster1\.([A-Z]+)_", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var prefix = m.Groups[1].Value.ToUpperInvariant();
                prefixCounts[prefix] = prefixCounts.GetValueOrDefault(prefix) + 1;
            }
            if (prefixCounts.Count == 0) return null;

            // Plukk dominerende prefiks
            var dominant = prefixCounts.OrderByDescending(kv => kv.Value).First();
            return _options.PlantPrefixMap.TryGetValue(dominant.Key, out var plant) ? plant : null;
        }
        catch
        {
            return null;
        }
    }
}

public enum SourceType
{
    Settlement,
    SettlementMultiPlant,    // én xlsx med flere plant-faner — rutes til /settlements/multi-plant
    ScadaTrends,             // master-CSV (tidsserier)
    ScadaAlarms,             // operlog (events) for ett anlegg
    ScadaAlarmsMultiPlant,   // operlog med events fra flere stations — rutes til /operlog/multi-plant
}

public static class SourceTypeExtensions
{
    public static string ToSourceTypeKey(this SourceType type) => type switch
    {
        SourceType.Settlement => "settlement",
        SourceType.SettlementMultiPlant => "settlement",
        SourceType.ScadaTrends => "scada",
        SourceType.ScadaAlarms => "operlog",
        SourceType.ScadaAlarmsMultiPlant => "operlog",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed record DetectionResult(
    bool Success,
    string? PlantId,
    SourceType? SourceType,
    string? ErrorMessage)
{
    public static DetectionResult Ok(string plantId, SourceType type) =>
        new(true, plantId, type, null);

    public static DetectionResult Unknown(string error) =>
        new(false, null, null, error);
}
