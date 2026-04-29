using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Settlement.Parsing;

/// <summary>
/// Konkret parser for månedlig portaleksport-Excel.
///
/// Strukturen i filen (per v1-skjema):
///  – Første fane = Summering (aggregater for hele perioden).
///  – Andre fane = én verk-fane per anlegg (f.eks. "1 Drivdal").
///  – I verk-fanen: rad 1 = kolonnenavn, rad 2 = enheter (ignoreres),
///    rad 3+ = data. Kolonne 17 er tom spacer og ignoreres.
///
/// Tidssone-håndtering: Time-kolonnen er naiv lokaltid (Europe/Oslo) med
/// format "dd.MM.yyyy HH:mm". Parseren konverterer til UTC internt. DST-
/// tvetydighet i oktober håndteres med "earliest offset wins" (klokken 02:00
/// som forekommer to ganger representeres som den første).
/// DST-ikke-eksisterende timer i mars (02:00–02:59) blir forkastet med
/// <c>DST_NONEXISTENT</c>-avvik.
///
/// MWh-Elhub == MWh-eSett sammenlignes med toleranse ε = 0.001 MWh. Avvik
/// over dette flagges som <c>ELHUB_ESETT_MISMATCH</c>. Dette er i tråd med
/// Python-referanse-implementasjonen.
/// </summary>
public sealed class ExcelSettlementParser : ISettlementParser
{
    private const double ElhubESettToleranceMwh = 0.001;
    private const double SummaryMismatchRelTolerance = 0.001;
    private const double SummaryMismatchAbsToleranceMinimum = 0.01;

    private readonly ISettlementSchemaRegistry _schemaRegistry;
    private readonly ILogger<ExcelSettlementParser> _logger;

    public ExcelSettlementParser(
        ISettlementSchemaRegistry schemaRegistry,
        ILogger<ExcelSettlementParser> logger)
    {
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "settlement.portal";
    public string Version => "v1";

    /// <summary>
    /// Regex som matcher kanoniske anleggs-titler i R1: navn + " dd.MM.yyyy".
    /// Brukes til å plukke ut KANONISK plant-navn (med norske tegn) fra R1
    /// selv når fane-navnet er ASCII-stripet ("1 Lgjen", "7 greyfoss").
    /// </summary>
    private static readonly Regex PlantTitleRegex = new(
        @"^(?<name>.+?)\s+\d{1,2}\.\d{1,2}\.\d{4}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Regex for fane-navn som "1 Lgjen", "7 greyfoss" — sifre, mellomrom, navn.
    /// Brukes til å gjenkjenne anleggs-faner i multi-plant-filer.
    /// </summary>
    private static readonly Regex PlantSheetRegex = new(
        @"^\d+\s+\S+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    public Task<ParsedSettlement> ParseAsync(Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var all = ParseAllCore(content);
        return Task.FromResult(all[0]);
    }

    public Task<IReadOnlyList<ParsedSettlement>> ParseAllAsync(
        Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var all = ParseAllCore(content);
        return Task.FromResult<IReadOnlyList<ParsedSettlement>>(all);
    }

    private List<ParsedSettlement> ParseAllCore(Stream content)
    {
        using var workbook = new XLWorkbook(content);

        if (workbook.Worksheets.Count < 2)
        {
            throw new InvalidOperationException(
                $"Forventet minst 2 faner (Summering + verk), fant {workbook.Worksheets.Count}.");
        }

        // Multi-plant-deteksjon: hvis det finnes ≥2 ark som matcher "n Navn"-mønsteret
        // OG en "Summering"-fane, så er fila multi-plant. Ellers: enkelt-plant.
        var summarySheet = workbook.Worksheets.FirstOrDefault(s =>
            s.Name.Equals("Summering", StringComparison.OrdinalIgnoreCase));
        var plantSheets = workbook.Worksheets
            .Where(s => PlantSheetRegex.IsMatch(s.Name))
            .ToList();

        if (summarySheet is not null && plantSheets.Count >= 2)
        {
            return ParseMultiPlantWorkbook(workbook, summarySheet, plantSheets);
        }

        // Enkelt-plant fallback (gammelt format): første ark = Summering,
        // andre ark = verk-fane. PlantId settes ikke (caller bruker URL).
        return new List<ParsedSettlement>(1) { ParseSinglePlantWorkbook(workbook) };
    }

    private ParsedSettlement ParseSinglePlantWorkbook(XLWorkbook workbook)
    {
        var issues = new List<ValidationIssue>();
        var summarySheet = workbook.Worksheets.First();
        var plantSheet = workbook.Worksheets.Skip(1).First();

        var plantName = ExtractPlantNameFromSheet(plantSheet);
        var summary = ParseSummary(summarySheet, issues);
        var (schemaVersion, hourly) = ParsePlantSheet(plantSheet, issues);

        CrossValidate(summary, hourly, issues);
        ValidateElhubESettConsistency(hourly, issues);

        return BuildParsedSettlement(plantName, plantId: null, schemaVersion, hourly, summary, issues);
    }

    private List<ParsedSettlement> ParseMultiPlantWorkbook(
        XLWorkbook workbook, IXLWorksheet summarySheet, IReadOnlyList<IXLWorksheet> plantSheets)
    {
        var results = new List<ParsedSettlement>(plantSheets.Count);
        foreach (var plantSheet in plantSheets)
        {
            var issues = new List<ValidationIssue>();
            var plantName = ExtractCanonicalNameFromR1(plantSheet) ?? ExtractPlantNameFromSheet(plantSheet);
            var slug = PlantSlug.ToSlug(plantName);
            if (string.IsNullOrEmpty(slug))
            {
                _logger.LogWarning(
                    "Hopper over fane {Sheet}: kunne ikke utlede gyldig plant-slug fra R1.",
                    plantSheet.Name);
                continue;
            }

            var (schemaVersion, hourly) = ParsePlantSheet(plantSheet, issues);
            ValidateElhubESettConsistency(hourly, issues);
            // Cross-validate er hopppet for multi-plant fordi Summering er
            // én rad per anlegg (ikke én rad totalt). Egen valideringsregel
            // kan legges til senere.

            results.Add(BuildParsedSettlement(plantName, slug, schemaVersion, hourly, summary: null, issues));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                "Multi-plant-fil ble detektert, men ingen anleggs-faner kunne parses.");
        }

        return results;
    }

    private static ParsedSettlement BuildParsedSettlement(
        string plantName, string? plantId,
        SettlementSchemaVersion schemaVersion,
        IReadOnlyList<SettlementHourlyRow> hourly,
        SettlementSummaryRow? summary,
        IReadOnlyList<ValidationIssue> issues)
    {
        var periodStart = hourly.Count > 0 ? hourly.Min(r => r.TimeUtc) : DateTimeOffset.MinValue;
        var periodEnd = hourly.Count > 0 ? hourly.Max(r => r.TimeUtc) : DateTimeOffset.MinValue;

        return new ParsedSettlement
        {
            PlantName = plantName,
            PlantId = plantId,
            SchemaVersion = schemaVersion switch
            {
                SettlementSchemaVersion.V1PortalMonthly => "portal-v1",
                _ => "unknown"
            },
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            Hourly = hourly,
            Summary = summary,
            Issues = issues,
        };
    }

    /// <summary>
    /// "1 Drivdal" → "Drivdal". Ark uten mellomrom returneres uendret. Brukes
    /// kun som fallback når R1-tittelen ikke kan tolkes.
    /// </summary>
    private static string ExtractPlantNameFromSheet(IXLWorksheet sheet)
    {
        var idx = sheet.Name.IndexOf(' ');
        return idx < 0 ? sheet.Name.Trim() : sheet.Name[(idx + 1)..].Trim();
    }

    /// <summary>
    /// Henter kanonisk plant-navn fra R1 i en anleggsfane. R1 har formatet
    /// "Løgjen 01.02.2026 - 28.02.2026" — vi plukker ut alt før første
    /// dato-substring. Returnerer null hvis R1 ikke matcher mønsteret.
    /// </summary>
    private static string? ExtractCanonicalNameFromR1(IXLWorksheet sheet)
    {
        var titleCell = sheet.Cell(1, 1);
        var title = titleCell.GetString();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var match = PlantTitleRegex.Match(title.Trim());
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    // ------------------------------------------------------------------
    // Summering-fane
    // ------------------------------------------------------------------
    private static SettlementSummaryRow? ParseSummary(IXLWorksheet sheet, List<ValidationIssue> issues)
    {
        if (sheet.RangeUsed() is null)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "SUMMARY_EMPTY",
                "Summering-fane er tom"));
            return null;
        }

        // Finn header-raden (inneholder "Tidsserie" eller "Time")
        var headerRow = FindHeaderRow(sheet, new[] { "tidsserie", "time" }, maxScanRows: 5);
        if (headerRow is null)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "SUMMARY_HEADER_NOT_FOUND",
                "Fant ikke headerrad i Summering-fane"));
            return null;
        }

        // Les headere og map til kanonske navn
        var headerCells = headerRow.Cells().ToList();
        var canonicalByColumn = new Dictionary<int, string>();
        for (var i = 0; i < headerCells.Count; i++)
        {
            var canonical = SettlementColumnMapping.Canonical(headerCells[i].GetString());
            if (canonical is not null)
            {
                canonicalByColumn[i] = canonical;
            }
        }

        // Første dataraden – ligger på neste rad etter header (ingen enhetsrad i Summering).
        var dataRow = headerRow.RowBelow();
        if (dataRow.IsEmpty())
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "SUMMARY_NO_DATA",
                "Summering-fane har header men ingen dataverdier"));
            return null;
        }

        var cells = dataRow.Cells(1, headerCells.Count).ToList();

        string? timeseriesLabel = null;
        double? mwhElhub = null, mwhESett = null, spotbud = null, spotoms = null,
            ubalanse = null, rkKjop = null, rkSalg = null, npGebyr = null,
            esettVol = null, esettUbal = null, sumSalg = null, megler = null, oppgjor = null;

        foreach (var (col, canonical) in canonicalByColumn)
        {
            if (col >= cells.Count)
            {
                continue;
            }

            var cell = cells[col];

            if (canonical == SettlementColumnMapping.Time)
            {
                timeseriesLabel = cell.GetString();
                continue;
            }

            var value = TryGetDouble(cell);
            switch (canonical)
            {
                case SettlementColumnMapping.MwhElhub: mwhElhub = value; break;
                case SettlementColumnMapping.MwhESett: mwhESett = value; break;
                case SettlementColumnMapping.Spotbud: spotbud = value; break;
                case SettlementColumnMapping.Spotomsetning: spotoms = value; break;
                case SettlementColumnMapping.Ubalanse: ubalanse = value; break;
                case SettlementColumnMapping.RkKjop: rkKjop = value; break;
                case SettlementColumnMapping.RkSalg: rkSalg = value; break;
                case SettlementColumnMapping.NordPoolGebyr: npGebyr = value; break;
                case SettlementColumnMapping.ESettVolumgebyr: esettVol = value; break;
                case SettlementColumnMapping.ESettUbalansegebyr: esettUbal = value; break;
                case SettlementColumnMapping.SumSalg: sumSalg = value; break;
                case SettlementColumnMapping.Meglerprovisjon: megler = value; break;
                case SettlementColumnMapping.Oppgjor: oppgjor = value; break;
                default: break;
            }
        }

        return new SettlementSummaryRow
        {
            TimeseriesLabel = timeseriesLabel ?? "",
            MwhElhub = mwhElhub,
            MwhESett = mwhESett,
            SpotbudMwh = spotbud,
            SpotomsetningNok = spotoms,
            UbalanseMwh = ubalanse,
            RkKjopNok = rkKjop,
            RkSalgNok = rkSalg,
            NordPoolGebyrNok = npGebyr,
            ESettVolumgebyrNok = esettVol,
            ESettUbalansegebyrNok = esettUbal,
            SumSalgNok = sumSalg,
            MeglerprovisjonNok = megler,
            OppgjorNok = oppgjor,
        };
    }

    // ------------------------------------------------------------------
    // Verk-fane (hour by hour)
    // ------------------------------------------------------------------
    private (SettlementSchemaVersion version, IReadOnlyList<SettlementHourlyRow> rows)
        ParsePlantSheet(IXLWorksheet sheet, List<ValidationIssue> issues)
    {
        var usedRange = sheet.RangeUsed()
            ?? throw new InvalidOperationException("Verk-fane er tom.");

        // Finn header-raden ved å scanne etter "Time"-celle – portalfiler har ofte
        // en tittelrad før headerraden (rad 1 = "Drivdal 01.02.2025 …", rad 2 = headere).
        var headerRow = FindHeaderRow(sheet, new[] { "time", "tidsserie" }, maxScanRows: 5);
        if (headerRow is null)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Error,
                "HOURLY_HEADER_NOT_FOUND",
                "Fant ikke headerrad i verk-fane (lette etter 'Time' i de første 5 radene)"));
            return (SettlementSchemaVersion.Unknown, Array.Empty<SettlementHourlyRow>());
        }
        var headerCells = headerRow.Cells().ToList();
        var headerStrings = headerCells.Select(c => c.GetString()).ToList();

        var schemaVersion = _schemaRegistry.Detect(headerStrings);
        if (schemaVersion == SettlementSchemaVersion.Unknown)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Error,
                "SCHEMA_UNKNOWN",
                "Kolonneheadere matcher ingen registrert skjemaversjon. " +
                $"Observerte kolonner: {string.Join(", ", headerStrings.Where(h => !string.IsNullOrWhiteSpace(h)))}"));
            return (schemaVersion, Array.Empty<SettlementHourlyRow>());
        }

        var canonicalByColumn = BuildCanonicalColumnMap(headerCells);

        var rows = new List<SettlementHourlyRow>(capacity: 744);  // opp til 31 × 24 + DST
        var tz = TimeZones.Norway;

        // Rad 2 er enhetsrad – skip. Data starter på rad 3.
        var firstDataRowNumber = headerRow.RowNumber() + 2;
        var lastRowNumber = usedRange.LastRow().RowNumber();

        for (var r = firstDataRowNumber; r <= lastRowNumber; r++)
        {
            ct_check();
            var row = sheet.Row(r);
            if (row.IsEmpty())
            {
                continue;
            }

            var parsed = ParseHourlyRow(row, canonicalByColumn, tz, issues);
            if (parsed is not null)
            {
                rows.Add(parsed);
            }
        }

        // Sortér på UTC for konsistens
        rows.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));

        // 15-min eksporter aggregeres til time-rader. Detekteringen ser på
        // de første 3 tids-radene; hvis alle differanser er 15 min slås
        // hver 4 rader sammen til én time. 60-min eksporter går uendret igjennom.
        var aggregated = HourlyAggregator.Process(rows, issues);

        return (schemaVersion, aggregated);

        // Lokal funksjon for framtidig å koble CancellationToken inn her –
        // ClosedXML er syntaktisk synkron så vi sjekker ikke per rad nå.
        static void ct_check() { }
    }

    private static Dictionary<int, string> BuildCanonicalColumnMap(IReadOnlyList<IXLCell> headerCells)
    {
        var map = new Dictionary<int, string>();
        foreach (var cell in headerCells)
        {
            var canonical = SettlementColumnMapping.Canonical(cell.GetString());
            if (canonical is not null)
            {
                // Excel-kolonnenummer er 1-basert
                map[cell.Address.ColumnNumber] = canonical;
            }
        }
        return map;
    }

    private static SettlementHourlyRow? ParseHourlyRow(
        IXLRow row,
        Dictionary<int, string> canonicalByColumn,
        TimeZoneInfo tz,
        List<ValidationIssue> issues)
    {
        // Samle alle kanonske verdier først
        var values = new Dictionary<string, double?>();
        string? timeStr = null;
        DateTime? timeDt = null;

        foreach (var (col, canonical) in canonicalByColumn)
        {
            var cell = row.Cell(col);

            if (canonical == SettlementColumnMapping.Time)
            {
                // Kan være datetime-verdi eller tekst "dd.MM.yyyy HH:mm"
                if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var dt))
                {
                    timeDt = dt;
                }
                else
                {
                    timeStr = cell.GetString();
                }
                continue;
            }

            values[canonical] = TryGetDouble(cell);
        }

        DateTime localNaive;
        if (timeDt is not null)
        {
            localNaive = timeDt.Value;
        }
        else if (!string.IsNullOrWhiteSpace(timeStr) &&
                 DateTime.TryParseExact(
                     timeStr.Trim(),
                     new[] { "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy HH:mm", "d.M.yyyy H:mm" },
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out var parsed))
        {
            localNaive = parsed;
        }
        else
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "HOURLY_INVALID_TIMESTAMP",
                $"Ugyldig Time-verdi på rad {row.RowNumber()}: '{timeStr}'",
                AffectedRows: 1));
            return null;
        }

        // DST-håndtering
        DateTimeOffset localOffset;
        try
        {
            // Hvis tidspunktet er i DST-gapet (mars, 02:00–02:59) → forkast med avvik
            if (tz.IsInvalidTime(localNaive))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, "DST_NONEXISTENT",
                    $"Rad {row.RowNumber()}: {localNaive:dd.MM.yyyy HH:mm} eksisterer ikke pga. DST-overgang – rad forkastet",
                    AffectedRows: 1));
                return null;
            }

            // Hvis tidspunktet er tvetydig (oktober, 02:00–02:59 kommer to ganger)
            // bruk standard-tid (earliest offset wins) som Python gjør med ambiguous="infer".
            if (tz.IsAmbiguousTime(localNaive))
            {
                // Ambiguous – returnerer to offset. Første er standard-tid (etter DST-slutt),
                // andre er DST-tid (før). Vi velger den FØRSTE forekomsten tidsmessig, som er
                // DST-tid (sommertid, +02:00 for Europe/Oslo).
                var offsets = tz.GetAmbiguousTimeOffsets(localNaive);
                // I tilfelle av tvetydighet: velg den med STØRRE offset (DST/sommertid = +02:00),
                // som representerer den FØRSTE forekomsten av klokkeslettet.
                var pick = offsets[0];
                foreach (var o in offsets)
                {
                    if (o > pick)
                    {
                        pick = o;
                    }
                }
                localOffset = new DateTimeOffset(localNaive, pick);
            }
            else
            {
                var offset = tz.GetUtcOffset(localNaive);
                localOffset = new DateTimeOffset(localNaive, offset);
            }
        }
        catch (ArgumentException ex)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "DST_ERROR",
                $"Rad {row.RowNumber()}: kunne ikke tolke {localNaive:dd.MM.yyyy HH:mm} i Europe/Oslo: {ex.Message}",
                AffectedRows: 1));
            return null;
        }

        var utc = localOffset.ToUniversalTime();

        return new SettlementHourlyRow
        {
            TimeUtc = utc,
            TimeLocal = localOffset,
            MwhElhub = values.GetValueOrDefault(SettlementColumnMapping.MwhElhub),
            MwhESett = values.GetValueOrDefault(SettlementColumnMapping.MwhESett),
            SpotbudMwh = values.GetValueOrDefault(SettlementColumnMapping.Spotbud),
            SpotprisNokMwh = values.GetValueOrDefault(SettlementColumnMapping.Spotpris),
            SpotomsetningNok = values.GetValueOrDefault(SettlementColumnMapping.Spotomsetning),
            UbalanseMwh = values.GetValueOrDefault(SettlementColumnMapping.Ubalanse),
            RkPrisNokMwh = values.GetValueOrDefault(SettlementColumnMapping.RkPris),
            RkKjopNok = values.GetValueOrDefault(SettlementColumnMapping.RkKjop),
            RkSalgNok = values.GetValueOrDefault(SettlementColumnMapping.RkSalg),
            NordPoolGebyrNok = values.GetValueOrDefault(SettlementColumnMapping.NordPoolGebyr),
            ESettVolumgebyrNok = values.GetValueOrDefault(SettlementColumnMapping.ESettVolumgebyr),
            ESettUbalansegebyrNok = values.GetValueOrDefault(SettlementColumnMapping.ESettUbalansegebyr),
            SumSalgNok = values.GetValueOrDefault(SettlementColumnMapping.SumSalg),
            MeglerprovisjonNok = values.GetValueOrDefault(SettlementColumnMapping.Meglerprovisjon),
            OppgjorNok = values.GetValueOrDefault(SettlementColumnMapping.Oppgjor),
            BruttoOmsetningNok = values.GetValueOrDefault(SettlementColumnMapping.BruttoOmsetning),
            ProduksjonplanMwh = values.GetValueOrDefault(SettlementColumnMapping.Produksjonplan),
            EffektavlesningerMw = values.GetValueOrDefault(SettlementColumnMapping.Effekt),
            AbsUbalansevolumMwh = values.GetValueOrDefault(SettlementColumnMapping.AbsUbalansevolum),
            UbalanseResultatNok = values.GetValueOrDefault(SettlementColumnMapping.UbalanseResultat),
            DqState = DataQualityState.Good,
        };
    }

    // ------------------------------------------------------------------
    // Valideringer
    // ------------------------------------------------------------------
    private static void CrossValidate(
        SettlementSummaryRow? summary,
        IReadOnlyList<SettlementHourlyRow> hourly,
        List<ValidationIssue> issues)
    {
        if (summary is null || hourly.Count == 0)
        {
            return;
        }

        CompareAggregate(summary.MwhElhub, hourly.Sum(h => h.MwhElhub ?? 0), "mwh_elhub", issues);
        CompareAggregate(summary.MwhESett, hourly.Sum(h => h.MwhESett ?? 0), "mwh_esett", issues);
        CompareAggregate(summary.OppgjorNok, hourly.Sum(h => h.OppgjorNok ?? 0), "oppgjor_nok", issues);
        CompareAggregate(summary.SumSalgNok, hourly.Sum(h => h.SumSalgNok ?? 0), "sum_salg_nok", issues);
    }

    private static void CompareAggregate(double? summaryValue, double hourlySum, string column,
        List<ValidationIssue> issues)
    {
        if (summaryValue is null)
        {
            return;
        }
        var diff = Math.Abs(summaryValue.Value - hourlySum);
        var tolerance = Math.Max(SummaryMismatchAbsToleranceMinimum,
            Math.Abs(summaryValue.Value) * SummaryMismatchRelTolerance);
        if (diff > tolerance)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "SUMMARY_HOURLY_MISMATCH",
                $"Summering.{column}={summaryValue.Value:F2} ≠ sum(timer)={hourlySum:F2} (avvik {diff:F3} > toleranse {tolerance:F3})"));
        }
    }

    private static void ValidateElhubESettConsistency(
        IReadOnlyList<SettlementHourlyRow> hourly,
        List<ValidationIssue> issues)
    {
        var mismatches = 0;
        foreach (var row in hourly)
        {
            if (row.MwhElhub is null || row.MwhESett is null)
            {
                continue;
            }
            if (Math.Abs(row.MwhElhub.Value - row.MwhESett.Value) > ElhubESettToleranceMwh)
            {
                mismatches++;
            }
        }

        if (mismatches > 0)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "ELHUB_ESETT_MISMATCH",
                $"{mismatches} timer har MWh-Elhub ≠ MWh-eSett (toleranse {ElhubESettToleranceMwh} MWh)",
                AffectedRows: mismatches));
        }
    }

    // ------------------------------------------------------------------
    // Hjelpere
    // ------------------------------------------------------------------
    private static IXLRow? FindHeaderRow(IXLWorksheet sheet, string[] needles, int maxScanRows)
    {
        var usedRange = sheet.RangeUsed();
        if (usedRange is null)
        {
            return null;
        }
        var first = usedRange.FirstRow().RowNumber();
        for (var r = first; r < first + maxScanRows; r++)
        {
            var row = sheet.Row(r);
            foreach (var cell in row.CellsUsed())
            {
                var text = cell.GetString().ToLowerInvariant();
                if (needles.Any(n => text.Contains(n, StringComparison.Ordinal)))
                {
                    return row;
                }
            }
        }
        return null;
    }

    private static double? TryGetDouble(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return null;
        }
        if (cell.TryGetValue<double>(out var d))
        {
            return d;
        }
        var s = cell.GetString();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        // Norsk desimalkomma fallback
        if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
        {
            return v2;
        }
        return null;
    }
}
