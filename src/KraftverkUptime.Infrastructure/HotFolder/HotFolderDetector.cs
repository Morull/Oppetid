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

        // 3. Hvis filnavn ikke ga svar: content-sniff for SCADA
        if (plantId is null && sourceType == SourceType.ScadaTrends)
        {
            plantId = DetectPlantFromCsvContent(file);
        }

        if (plantId is null)
        {
            return DetectionResult.Unknown(
                $"Klarte ikke identifisere anlegg for fil '{name}'. " +
                $"Forventer plant-navn i filnavnet eller SCADA-tag-prefiks i header.");
        }

        return DetectionResult.Ok(plantId, sourceType.Value);
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
    SettlementMultiPlant,  // én xlsx med flere plant-faner — rutes til /settlements/multi-plant
    ScadaTrends,           // master-CSV (tidsserier)
    ScadaAlarms,           // operlog (events)
}

public static class SourceTypeExtensions
{
    public static string ToSourceTypeKey(this SourceType type) => type switch
    {
        SourceType.Settlement => "settlement",
        SourceType.SettlementMultiPlant => "settlement",
        SourceType.ScadaTrends => "scada",
        SourceType.ScadaAlarms => "operlog",
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
