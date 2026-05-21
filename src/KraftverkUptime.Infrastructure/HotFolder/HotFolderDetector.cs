using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;

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
///
/// Plant-mapping bruker <see cref="PlantSlug.ToSlug"/> + en kanonisk
/// liste (<see cref="KnownPlantSlugs"/>) for validering. KAIA-eksporten
/// stripper norske tegn fra fane-navn ("1 Lgjen", "7 greyfoss", "1 Vikes")
/// som ikke kan rekonstrueres til plant-id, så detektoren leser kanonisk
/// navn fra cell A1 ("Vikeså 01.04.2026 - 30.04.2026") når mulig.
/// </summary>
public sealed class HotFolderDetector
{
    private readonly HotFolderOptions _options;

    /// <summary>
    /// Kanoniske plant-id-er fra <c>PlantPortfolioSeeder.Portfolio</c>. Brukes
    /// til å validere at en utledet slug er et reelt anlegg (ikke garbage).
    /// Holdt synkronisert med seederen — endring her må også gjøres der.
    /// </summary>
    private static readonly HashSet<string> KnownPlantSlugs = new(StringComparer.Ordinal)
    {
        "drivdal", "lindland", "haukland", "honnefoss", "liavatn",
        "logjen", "grodemfoss", "ogreyfoss", "orsdalen", "vikesa", "stolskraft",
    };

    /// <summary>
    /// Regex for kanonisk plant-tittel i R1: "Navn dd.MM.yyyy[ - dd.MM.yyyy]".
    /// Speiler <c>ExcelSettlementParser.PlantTitleRegex</c>.
    /// </summary>
    private static readonly Regex PlantTitleRegex = new(
        @"^(?<name>.+?)\s+\d{1,2}\.\d{1,2}\.\d{4}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

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

    /// <summary>
    /// Detekterer filtype + plant-id og returnerer et resultat med vedlagt
    /// <see cref="DetectionDiagnostics"/>. Diagnostics inneholder per-steg-
    /// logg slik at karantene-modal i UI kan vise nøyaktig hva ble forsøkt.
    /// </summary>
    public DetectionResult Detect(FileInfo file)
    {
        var diag = new DetectionDiagnostics
        {
            FileName = file.Name,
            FileSizeBytes = file.Exists ? file.Length : 0,
            Extension = file.Extension.ToLowerInvariant(),
        };
        return DetectInternal(file, diag);
    }

    private DetectionResult DetectInternal(FileInfo file, DetectionDiagnostics diag)
    {
        var ext = diag.Extension;
        var name = diag.FileName;

        // 1. Filtype basert på extension + navn
        SourceType? sourceType = ext switch
        {
            ".xlsx" => SourceType.Settlement,
            ".xls" => SourceType.Settlement,
            ".csv" => DetectCsvType(name, file, diag),
            _ => null,
        };

        if (sourceType is null)
        {
            diag.Attempts.Add($"Ukjent filtype '{ext}' — forventer .xlsx eller .csv.");
            return DetectionResult.Unknown(
                $"Ukjent filtype: {ext}. Forventer .xlsx (settlement) eller .csv (SCADA).",
                diag);
        }
        diag.DetectedSourceType = sourceType.Value.ToString();
        diag.Attempts.Add($"Filtype detektert: {sourceType.Value}");

        // 2a. Settlement: sjekk om det er multi-plant (≥ 2 plant-faner i workbook)
        if (sourceType == SourceType.Settlement)
        {
            var sheetCount = TryCountPlantSheets(file, diag);
            if (sheetCount >= 2)
            {
                diag.Attempts.Add($"≥ 2 plant-faner ({sheetCount}) → multi-plant settlement.");
                return DetectionResult.Ok(plantId: "_multi_", SourceType.SettlementMultiPlant, diag);
            }
        }

        // 2b. Pre-flight multi-plant-deteksjon for CSV-er FØR filnavn-regex.
        // Filer som har vært gjennom done/-mappa har plant-navn i filnavnet
        // (eks. "vikesa_scada_TIMESTAMP_orig.csv"), som ellers ville fått
        // filnavn-regexen til å treffe vikesa selv om innholdet er multi-plant.
        // Innhold har alltid forrang for ruting-beslutninger.
        if (sourceType == SourceType.ScadaTrends)
        {
            var (_, isMulti) = AnalyzeCsvContent(file, diag);
            if (isMulti)
            {
                diag.Attempts.Add("Multi-plant SCADA-trends (innhold) — ruter til /scada/multi-plant.");
                return DetectionResult.Ok("_multi_", SourceType.ScadaTrendsMultiPlant, diag);
            }
        }
        else if (sourceType == SourceType.ScadaTrendsFine)
        {
            // 15-min har samme multi-plant-mulighet (Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md).
            // Bruker samme analyse-logikk men ruter til en egen kategori slik at
            // importøren skriver til sample_facts_fine i stedet for sample_facts.
            var (_, isMulti) = AnalyzeCsvContent(file, diag);
            if (isMulti)
            {
                diag.Attempts.Add("Multi-plant SCADA-fine (innhold) — ruter til /scada/multi-plant-fine.");
                return DetectionResult.Ok("_multi_", SourceType.ScadaTrendsFineMultiPlant, diag);
            }
        }
        else if (sourceType == SourceType.ScadaAlarms)
        {
            var stationResult = DetectPlantsFromOperlog(file, diag);
            if (stationResult.IsMultiPlant)
            {
                diag.Attempts.Add("Multi-plant operlog (innhold) — ruter til /operlog/multi-plant.");
                return DetectionResult.Ok("_multi_", SourceType.ScadaAlarmsMultiPlant, diag);
            }
        }

        // 2c. Single-plant — filnavn-regex først
        var plantId = DetectPlantFromFilename(name, diag);

        // 3. Content-sniff fallback hvis filnavn ikke ga svar
        if (plantId is null)
        {
            if (sourceType == SourceType.Settlement)
            {
                plantId = DetectPlantFromXlsxContent(file, diag);
            }
            else if (sourceType == SourceType.ScadaTrends)
            {
                var (csvPlant, _) = AnalyzeCsvContent(file, diag);
                plantId = csvPlant;
            }
            else if (sourceType == SourceType.ScadaAlarms)
            {
                var stationResult = DetectPlantsFromOperlog(file, diag);
                plantId = stationResult.DominantPlant;
            }
        }

        if (plantId is null)
        {
            return DetectionResult.Unknown(
                $"Klarte ikke identifisere anlegg for fil '{name}'. " +
                $"Forventer plant-navn i filnavnet, fane-navn i workbook, eller SCADA-tag-prefiks i header.",
                diag);
        }

        diag.ResolvedPlantId = plantId;
        return DetectionResult.Ok(plantId, sourceType.Value, diag);
    }

    /// <summary>
    /// For single-plant xlsx: les plant-navn fra cell A1 i første ikke-aggregat-
    /// fane. KAIA-eksporten har formatet "Vikeså 01.04.2026 - 30.04.2026" i A1
    /// med fulle norske tegn — sheet-navnet er strippet ("1 Vikes") og kan ikke
    /// rekonstrueres, men A1 er kanonisk. Vi henter ut navn-delen via
    /// <see cref="PlantTitleRegex"/> og slugifiserer.
    /// </summary>
    private string? DetectPlantFromXlsxContent(FileInfo file, DetectionDiagnostics diag)
    {
        try
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook(file.FullName);

            foreach (var ws in workbook.Worksheets.Where(w =>
                w.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible
                && !IsAggregateSheetName(w.Name)))
            {
                var a1 = ws.Cell(1, 1).GetString();
                diag.SheetTitleCells[ws.Name] = a1;

                // 1. Cell A1 — KAIA-konvensjon: "Vikeså 01.04.2026 - 30.04.2026"
                var titleSlug = TryExtractSlugFromTitleCell(a1);
                if (titleSlug is not null)
                {
                    diag.Attempts.Add($"Sheet '{ws.Name}' A1='{Truncate(a1, 60)}' → slug '{titleSlug}'.");
                    return titleSlug;
                }

                // 2. Fallback: B1..E1 (eldre eksport-versjoner kan ha tittelen forskjøvet)
                for (var col = 2; col <= 5; col++)
                {
                    var raw = ws.Cell(1, col).GetString();
                    var slug = TryExtractSlugFromTitleCell(raw);
                    if (slug is not null)
                    {
                        diag.Attempts.Add($"Sheet '{ws.Name}' col {col}='{Truncate(raw, 60)}' → slug '{slug}'.");
                        return slug;
                    }
                }

                // 3. Siste fallback: selve fane-navnet — fungerer kun for filer
                //    med ren navn ("drivdal.xlsx"), ikke KAIA-stripping ("1 Vikes").
                var fromSheet = TryNormalizePlantName(ws.Name);
                if (fromSheet is not null)
                {
                    diag.Attempts.Add($"Sheet-navn '{ws.Name}' → slug '{fromSheet}'.");
                    return fromSheet;
                }
                diag.Attempts.Add(
                    $"Sheet '{ws.Name}': A1='{Truncate(a1, 60)}' matcher ikke tittel-mønster, " +
                    $"og fane-navn slug-er ikke til kjent anlegg.");
            }
            return null;
        }
        catch (Exception ex)
        {
            diag.Attempts.Add($"Klarte ikke åpne workbook: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…"));

    /// <summary>
    /// Forsøker å hente kanonisk plant-slug fra en R1-tittel-celle. Format:
    /// "Vikeså 01.04.2026" eller "Stølskraft 01.04.2026 - 30.04.2026".
    /// Returnerer null hvis innholdet ikke matcher tittel-mønsteret eller
    /// resulterende slug ikke er et kjent anlegg.
    /// </summary>
    private static string? TryExtractSlugFromTitleCell(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var match = PlantTitleRegex.Match(trimmed);
        if (!match.Success) return null;
        return TryNormalizePlantName(match.Groups["name"].Value);
    }

    /// <summary>
    /// Slugifiserer et råstring (kan ha norske tegn, prefiks-tall, mellomrom)
    /// og validerer mot <see cref="KnownPlantSlugs"/>. Returnerer slug ved
    /// match, ellers null. Eksempler:
    ///   "Vikeså"  → "vikesa"
    ///   "Drivdal" → "drivdal"
    ///   "1 Vikes" → null (norsk tegn er strippet, kan ikke rekonstrueres)
    ///   "Stølskraft" → "stolskraft"
    /// </summary>
    private static string? TryNormalizePlantName(string raw)
    {
        var slug = PlantSlug.ToSlug(raw);
        if (string.IsNullOrEmpty(slug)) return null;
        // Strip leading digits ("1vikesa" fra "1 Vikeså" — sjelden, men trygt)
        var stripped = slug.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (KnownPlantSlugs.Contains(stripped)) return stripped;
        if (KnownPlantSlugs.Contains(slug)) return slug;
        return null;
    }

    /// <summary>
    /// Operlog-CSV har 'station'-kolonnen som inneholder anleggsnavn per rad.
    /// Vi leser de første 200 radene og teller stasjoner. Hvis 2+ unike
    /// stasjoner med signifikant volum (≥ 10 % hver) → multi-plant.
    /// </summary>
    private OperlogPlantDetectResult DetectPlantsFromOperlog(FileInfo file, DetectionDiagnostics diag)
    {
        try
        {
            using var reader = new StreamReader(file.FullName);
            var headerLine = reader.ReadLine();
            if (headerLine is null)
            {
                diag.Attempts.Add("Operlog: tom fil (ingen header-linje).");
                return OperlogPlantDetectResult.None;
            }
            diag.HeaderLine = Truncate(headerLine, 200);

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
            if (stationIdx < 0)
            {
                diag.Attempts.Add($"Operlog: fant ikke 'station'-kolonne i header. Kolonner: {string.Join(", ", headers.Take(10))}");
                return OperlogPlantDetectResult.None;
            }

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
            if (stationCounts.Count == 0)
            {
                diag.Attempts.Add("Operlog: ingen stations funnet i de første 500 radene.");
                return OperlogPlantDetectResult.None;
            }
            diag.OperlogStations = stationCounts.OrderByDescending(kv => kv.Value)
                .Take(10).ToDictionary(kv => kv.Key, kv => kv.Value);

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
            diag.Attempts.Add($"Operlog: dominant plant = {top.Plant} ({top.Total} rader).");
            return new OperlogPlantDetectResult(DominantPlant: top.Plant, IsMultiPlant: false);
        }
        catch (Exception ex)
        {
            diag.Attempts.Add($"Operlog-detect feilet: {ex.GetType().Name}: {ex.Message}");
            return OperlogPlantDetectResult.None;
        }
    }

    /// <summary>
    /// Mapper station-navn fra operlog (eks. "Haukland", "Drivdal", "Stølskraft")
    /// til kanonisk plant-id. Bruker først <see cref="PlantSlug.ToSlug"/> +
    /// validering, så <c>PlantPrefixMap</c> som fallback for SCADA-tag-prefikser.
    /// </summary>
    private string? SlugifyStation(string station)
    {
        var slug = TryNormalizePlantName(station);
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
    private static int TryCountPlantSheets(FileInfo file, DetectionDiagnostics diag)
    {
        try
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook(file.FullName);
            diag.SheetNames = new Collection<string>(workbook.Worksheets.Select(ws => ws.Name).ToList());
            var plantLikeSheets = workbook.Worksheets.Count(ws =>
                ws.Visibility == ClosedXML.Excel.XLWorksheetVisibility.Visible
                && !IsAggregateSheetName(ws.Name));
            diag.Attempts.Add($"Workbook åpnet: {diag.SheetNames.Count} faner totalt, {plantLikeSheets} plant-lignende.");
            return plantLikeSheets;
        }
        catch (Exception ex)
        {
            diag.Attempts.Add($"Klarte ikke åpne workbook for å telle faner: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    private static bool IsAggregateSheetName(string name) =>
        name.Equals("Summering", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sammendrag", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Info", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Forside", StringComparison.OrdinalIgnoreCase);

    private SourceType? DetectCsvType(string fileName, FileInfo file, DetectionDiagnostics diag)
    {
        if (fileName.Contains("operlog", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("alarm", StringComparison.OrdinalIgnoreCase))
        {
            diag.Attempts.Add("CSV-filnavn inneholder 'operlog'/'alarm' → ScadaAlarms.");
            return SourceType.ScadaAlarms;
        }

        // 15-min-eksport: filnavn-markører "15min", "avg-15min" eller "fine".
        // Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md — separat tabell hindrer
        // overskriving av hourly på :00-tidsstempler.
        var isFineByName = fileName.Contains("15min", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("avg-15min", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("_fine", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var reader = new StreamReader(file.FullName);
            var firstLine = reader.ReadLine() ?? "";
            diag.HeaderLine = Truncate(firstLine, 200);
            if (firstLine.Contains("Tidsstempel", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Hendelse", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Event", StringComparison.OrdinalIgnoreCase))
            {
                diag.Attempts.Add("CSV header inneholder 'Tidsstempel'/'Hendelse'/'Event' → ScadaAlarms.");
                return SourceType.ScadaAlarms;
            }
            if (firstLine.Contains("DateTime", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("Cluster1.", StringComparison.OrdinalIgnoreCase))
            {
                if (isFineByName)
                {
                    diag.Attempts.Add("CSV header er DateTime/Cluster1 og filnavn markerer 15-min → ScadaTrendsFine.");
                    return SourceType.ScadaTrendsFine;
                }
                diag.Attempts.Add("CSV header inneholder 'DateTime'/'Cluster1.' → ScadaTrends.");
                return SourceType.ScadaTrends;
            }
        }
        catch (Exception ex)
        {
            diag.Attempts.Add($"Klarte ikke lese CSV header: {ex.GetType().Name}: {ex.Message}");
        }

        if (isFineByName)
        {
            diag.Attempts.Add("CSV header matchet ikke noe kjent, men filnavn markerer 15-min → ScadaTrendsFine.");
            return SourceType.ScadaTrendsFine;
        }
        diag.Attempts.Add("CSV header matchet ingen kjente mønstre → default ScadaTrends.");
        return SourceType.ScadaTrends;
    }

    private string? DetectPlantFromFilename(string fileName, DetectionDiagnostics diag)
    {
        foreach (var pattern in FilenamePlantPatterns)
        {
            var m = pattern.Match(fileName);
            if (m.Success)
            {
                var raw = m.Groups["plant"].Value.ToLowerInvariant();
                var slug = raw switch
                {
                    "logjen" or "løgjen" => "logjen",
                    "grodemfoss" or "grødemfoss" => "grodemfoss",
                    "ogreyfoss" or "øgreyfoss" => "ogreyfoss",
                    "orsdalen" or "ørsdalen" => "orsdalen",
                    "vikesa" or "vikeså" => "vikesa",
                    _ => raw,
                };
                diag.Attempts.Add($"Filnavn-regex matchet '{raw}' → slug '{slug}'.");
                return slug;
            }
        }
        diag.Attempts.Add("Filnavn-regex matchet ikke noe kjent plant-navn.");
        return null;
    }

    private string? DetectPlantFromCsvContent(FileInfo file, DetectionDiagnostics diag)
    {
        var (plantId, _) = AnalyzeCsvContent(file, diag);
        return plantId;
    }

    /// <summary>
    /// Telmer alle plant-prefikser i CSV-headeren og avgjør om fila er
    /// single-plant (én dominant prefix), multi-plant (≥ 2 prefikser som
    /// hver har ≥ 2 tags) eller ukjent. Returnerer en (plantId, isMulti)-
    /// tupel: <c>plantId</c> er navnet på det entydige anlegget for single-
    /// plant, og <c>"_multi_"</c> for multi-plant. Begge null hvis ukjent.
    /// </summary>
    internal (string? PlantId, bool IsMultiPlant) AnalyzeCsvContent(FileInfo file, DetectionDiagnostics diag)
    {
        try
        {
            using var reader = new StreamReader(file.FullName);
            var firstLine = reader.ReadLine() ?? "";
            if (string.IsNullOrEmpty(diag.HeaderLine))
            {
                diag.HeaderLine = Truncate(firstLine, 200);
            }

            var prefixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(firstLine, @"Cluster1\.([A-Z0-9]+)_", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var prefix = m.Groups[1].Value.ToUpperInvariant();
                prefixCounts[prefix] = prefixCounts.GetValueOrDefault(prefix) + 1;
            }
            if (prefixCounts.Count == 0)
            {
                diag.Attempts.Add("CSV header har ingen 'Cluster1.PREFIKS_'-tags.");
                return (null, false);
            }
            diag.ScadaPrefixCounts = prefixCounts.OrderByDescending(kv => kv.Value)
                .Take(10).ToDictionary(kv => kv.Key, kv => kv.Value);

            // Mapper prefikser til plant-id'er. Tell tags per UNIK plant — to
            // prefikser som mapper til samme plant (eks. OGREY1 + OGREY2 →
            // ogreyfoss) regnes som ett anlegg.
            var tagsByPlant = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (prefix, count) in prefixCounts)
            {
                if (_options.PlantPrefixMap.TryGetValue(prefix, out var plant))
                {
                    tagsByPlant[plant] = tagsByPlant.GetValueOrDefault(plant) + count;
                }
            }
            if (tagsByPlant.Count == 0)
            {
                diag.Attempts.Add(
                    $"Ingen SCADA prefikser i {prefixCounts.Count} stk matchet PlantPrefixMap.");
                return (null, false);
            }

            // Multi-plant: minst 2 ulike plant-id'er har ≥ 2 tags hver.
            // Terskelen på 2 unngår at en enkelt feil-mappet tag i en ellers
            // single-plant fil utløser multi-plant-flow.
            var multiPlantCount = tagsByPlant.Count(kv => kv.Value >= 2);
            if (multiPlantCount >= 2)
            {
                diag.Attempts.Add(
                    $"Multi-plant SCADA: {tagsByPlant.Count} anlegg matchet ({string.Join(", ", tagsByPlant.Select(kv => $"{kv.Key}={kv.Value}"))}).");
                return ("_multi_", true);
            }

            // Single-plant: anlegget med flest tags.
            var dominant = tagsByPlant.OrderByDescending(kv => kv.Value).First();
            diag.Attempts.Add(
                $"Single-plant SCADA: {dominant.Value} tags → plant '{dominant.Key}'.");
            return (dominant.Key, false);
        }
        catch (Exception ex)
        {
            diag.Attempts.Add($"CSV-content-detect feilet: {ex.GetType().Name}: {ex.Message}");
            return (null, false);
        }
    }
}

public enum SourceType
{
    Settlement,
    SettlementMultiPlant,    // én xlsx med flere plant-faner — rutes til /settlements/multi-plant
    ScadaTrends,             // master-CSV (tidsserier, hourly)
    ScadaTrendsMultiPlant,   // master-CSV med tags fra flere anlegg — rutes til /scada/multi-plant
    ScadaTrendsFine,         // 15-min master-CSV — rutes til sample_facts_fine. Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
    ScadaTrendsFineMultiPlant, // 15-min master-CSV med tags fra flere anlegg.
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
        SourceType.ScadaTrendsMultiPlant => "scada",
        SourceType.ScadaTrendsFine => "scada-fine",
        SourceType.ScadaTrendsFineMultiPlant => "scada-fine",
        SourceType.ScadaAlarms => "operlog",
        SourceType.ScadaAlarmsMultiPlant => "operlog",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed record DetectionResult(
    bool Success,
    string? PlantId,
    SourceType? SourceType,
    string? ErrorMessage,
    DetectionDiagnostics? Diagnostics = null)
{
    public static DetectionResult Ok(string plantId, SourceType type, DetectionDiagnostics? diag = null) =>
        new(true, plantId, type, null, diag);

    public static DetectionResult Unknown(string error, DetectionDiagnostics? diag = null) =>
        new(false, null, null, error, diag);
}

/// <summary>
/// Per-fil-trase fra detektoren — hvilke steg ble forsøkt og hva returnerte de.
/// Brukes av karantene-flyten for å skrive en .diag.json-fil ved siden av
/// .error.txt slik at UI-en kan vise "hvorfor havnet denne i karantene".
///
/// Settes alltid (også på suksess) for at vi skal ha lik telemetri på begge stier;
/// kostnaden er noen kB minne som forkastes når <see cref="HotFolderDetector.Detect"/>
/// returnerer på suksess-stien.
/// </summary>
public sealed class DetectionDiagnostics
{
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Extension { get; set; } = "";
    public string? DetectedSourceType { get; set; }
    public string? ResolvedPlantId { get; set; }

    /// <summary>Steg-for-steg-logg av hva detektoren forsøkte. Vises som liste i UI.</summary>
    public Collection<string> Attempts { get; } = new();

    /// <summary>For xlsx: alle fane-navn (inkludert aggregat-faner som ble filtrert).</summary>
    public Collection<string>? SheetNames { get; set; }

    /// <summary>For xlsx: A1-celle-innhold per ikke-aggregat-fane (truncated).</summary>
    public Dictionary<string, string> SheetTitleCells { get; } = new();

    /// <summary>For csv: første linje truncated til 200 tegn.</summary>
    public string? HeaderLine { get; set; }

    /// <summary>For SCADA-trends: tag-prefiks-tall (f.eks. "HONNE": 87, "DRIV": 14).</summary>
    public Dictionary<string, int>? ScadaPrefixCounts { get; set; }

    /// <summary>For operlog: station-navn med antall events (topp 10).</summary>
    public Dictionary<string, int>? OperlogStations { get; set; }
}
