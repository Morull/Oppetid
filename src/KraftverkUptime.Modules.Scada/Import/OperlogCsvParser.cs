using System.Globalization;
using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Import;

/// <summary>
/// Parser for KraftScada operlog-CSV. Eventer fra operatorlog og alarm-tabellen,
/// med sekund-presisjon på timestamps. Anlegg-uavhengig — fungerer for alle
/// stasjoner i KraftScada-eksporten via station-feltet (se <see cref="ParseMultiPlant"/>).
///
/// Format (verifisert mot operlog-export-2026-04-28...csv):
/// <code>
///   timestamp;station;username;tag;text;value;operatorType;originTable;alarmType;categoryNumber;offTimestamp
///   2026-02-27T12:00:01.000Z;Drivdal;AGC;DRIVDAL_G1_KONTROLL_AGC_DB_SP;G1 AGC ...;2,2;...;operatorlog;;;
///   2026-02-04T07:51:02.000Z;Drivdal;Drivdal;DRIVDAL_G1_KONTROLL_STARTER_AL;G1 startsekvens pågår;;;alarmlog;event;3;2026-02-04T07:59:16.000Z
/// </code>
///
/// Hver event mappes til en <see cref="ClassifiedEvent"/>:
///  – <c>STARTER_AL</c> → start på InService-periode
///  – <c>STOPPER_AL</c>, <c>FEIL_AL</c> → start på ForcedOutage / MaintenanceOutage
///  – Settpunkt-endringer → comment-only annotering (ingen state-endring)
///
/// Mapping-reglene er enkle her — finjusteres når <c>FusionClassifier</c> bygges.
/// </summary>
public sealed class OperlogCsvParser
{
    public OperlogParseResult Parse(string ownerOrgId, string plantId, TextReader reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentNullException.ThrowIfNull(reader);

        // Single-plant: alle rader tilhører samme plant uavhengig av station.
        var multi = ParseMultiPlant(ownerOrgId, reader, _ => plantId);
        var events = multi.EventsByPlantId.TryGetValue(plantId, out var list)
            ? list
            : Array.Empty<ClassifiedEvent>();
        var rawStats = multi.RowsByPlantId.TryGetValue(plantId, out var stats) ? stats : null;
        return new OperlogParseResult(plantId, multi.RowsParsed, multi.RowsSkipped, events, rawStats);
    }

    /// <summary>
    /// Multi-plant-variant: én CSV med events fra flere stasjoner.
    /// Hver rad rutes til riktig anlegg via <paramref name="stationToPlantId"/>-
    /// callbacken. Rader der callbacken returnerer null (ukjent stasjon)
    /// telles som <see cref="MultiPlantOperlogParseResult.UnknownStations"/>.
    /// </summary>
    public MultiPlantOperlogParseResult ParseMultiPlant(
        string ownerOrgId,
        TextReader reader,
        Func<string, string?> stationToPlantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(stationToPlantId);

        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Tom operlog-CSV — manglende header-rad.");
        var headers = headerLine.Split(';').Select(h => h.Trim()).ToList();

        int IdxOf(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var iTimestamp = IdxOf("timestamp");
        var iStation = IdxOf("station");
        var iTag = IdxOf("tag");
        var iText = IdxOf("text");
        var iAlarmType = IdxOf("alarmType");
        var iOffTimestamp = IdxOf("offTimestamp");

        if (iTimestamp < 0 || iTag < 0)
        {
            throw new InvalidDataException(
                "Operlog-CSV mangler påkrevde kolonner (timestamp, tag).");
        }

        var byPlant = new Dictionary<string, List<ClassifiedEvent>>(StringComparer.Ordinal);
        // Rå rad-stats per plant — telles for ALLE rader som ble routet til en plant,
        // uavhengig av om eventen produserte state-change. Brukes til data_imports-
        // logging så status-matrisen ser at vi mottok data for plant-en.
        var rawStatsByPlant = new Dictionary<string, RawStats>(StringComparer.Ordinal);
        var unknownStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowsParsed = 0;
        var rowsSkipped = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(';');
            if (cols.Length < headers.Count) { rowsSkipped++; continue; }

            if (!TryParseUtc(cols[iTimestamp], out var startUtc)) { rowsSkipped++; continue; }
            DateTimeOffset? endUtc = null;
            if (iOffTimestamp >= 0 && TryParseUtc(cols[iOffTimestamp], out var endParsed))
            {
                endUtc = endParsed;
            }

            var tag = cols[iTag].Trim();
            var text = iText >= 0 ? cols[iText] : "";
            var alarmType = iAlarmType >= 0 ? cols[iAlarmType] : "";
            var station = iStation >= 0 ? cols[iStation].Trim() : "";

            // Plant-routing først — selv settpunkt-events teller som "data mottatt"
            // for plant-en, og må derfor knyttes til plant før MapEvent-skipping.
            var plantId = string.IsNullOrEmpty(station) ? null : stationToPlantId(station);
            if (string.IsNullOrEmpty(plantId))
            {
                rowsSkipped++;
                if (!string.IsNullOrEmpty(station)) unknownStations.Add(station);
                continue;
            }

            // Oppdater rad-stats for plant — alle rader teller, også settpunkt-only.
            if (rawStatsByPlant.TryGetValue(plantId, out var stats))
            {
                stats.Count++;
                if (startUtc < stats.MinTime) stats.MinTime = startUtc;
                if (startUtc > stats.MaxTime) stats.MaxTime = startUtc;
            }
            else
            {
                rawStatsByPlant[plantId] = new RawStats
                {
                    Count = 1,
                    MinTime = startUtc,
                    MaxTime = startUtc,
                };
            }

            var (state, cause) = MapEvent(tag, alarmType);
            if (state is null)
            {
                // Settpunkt-endring eller annen ikke-state-event. Telt i raw-stats
                // over slik at plant-en ikke vises som "manglende data", men
                // produserer ingen ClassifiedEvent.
                rowsSkipped++;
                continue;
            }

            var rationale = string.IsNullOrWhiteSpace(text)
                ? $"operlog: {tag}"
                : $"operlog: {tag} — {text}";

            if (!byPlant.TryGetValue(plantId, out var bucket))
            {
                bucket = new List<ClassifiedEvent>();
                byPlant[plantId] = bucket;
            }
            bucket.Add(new ClassifiedEvent(
                Id: 0,
                OwnerOrgId: ownerOrgId,
                PlantId: plantId,
                StartUtc: startUtc,
                EndUtc: endUtc,
                State: state.Value,
                CauseCode: cause,
                Confidence: 0.95,
                SourcesJson: """["Operlog"]""",
                Rationale: rationale));
            rowsParsed++;
        }

        var roBuckets = byPlant.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ClassifiedEvent>)kv.Value,
            StringComparer.Ordinal);
        var roStats = rawStatsByPlant.ToDictionary(
            kv => kv.Key,
            kv => new PlantRawRowStats(kv.Value.Count, kv.Value.MinTime, kv.Value.MaxTime),
            StringComparer.Ordinal);

        return new MultiPlantOperlogParseResult(
            RowsParsed: rowsParsed,
            RowsSkipped: rowsSkipped,
            UnknownStations: unknownStations.Count,
            UnknownStationNames: unknownStations.ToList(),
            EventsByPlantId: roBuckets,
            RowsByPlantId: roStats);
    }

    private sealed class RawStats
    {
        public int Count;
        public DateTimeOffset MinTime;
        public DateTimeOffset MaxTime;
    }

    /// <summary>
    /// Mapper en operlog-event til (UnitState, CauseCode) eller null hvis eventen
    /// ikke skal påvirke klassifisering (typisk settpunkt-endring).
    /// </summary>
    private static (UnitState? State, string? Cause) MapEvent(string tag, string alarmType)
    {
        var t = tag.ToUpperInvariant();
        var isAlarmEvent = alarmType.Equals("event", StringComparison.OrdinalIgnoreCase);

        if (t.Contains("STARTER_AL"))
        {
            return (UnitState.InService, "operlog:start");
        }
        if (t.Contains("STOPPER_AL"))
        {
            return (UnitState.MaintenanceOutage, "operlog:stop");
        }
        // Rist-falltap-alarm: tett inntaksrist. Egen cause-code slik at
        // rist-detektor kan korrelere med trip-events (innen ±60 min).
        // Markerer ikke nedetid alene — bare som markør for senere matching.
        if (t.Contains("RIST_FALLTAP"))
        {
            return (UnitState.ForcedOutage, "operlog:rist-falltap");
        }
        if (t.Contains("FEIL_AL") || t.Contains("HAVARI"))
        {
            return (UnitState.ForcedOutage, "operlog:fault");
        }
        if (isAlarmEvent && t.Contains("_AL"))
        {
            // Generisk alarm-event uten klar kategori — registreres som FO med
            // generisk cause-code. Operatøren kan refinere via annotering.
            return (UnitState.ForcedOutage, "operlog:alarm");
        }

        // Settpunkt-endringer (KONTROLL_REG_*, AGC_*, etc.) påvirker ikke state.
        return (null, null);
    }

    private static bool TryParseUtc(string raw, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateTimeOffset.TryParse(
            raw.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out utc);
    }
}

public sealed record OperlogParseResult(
    string PlantId,
    int RowsParsed,
    int RowsSkipped,
    IReadOnlyList<ClassifiedEvent> Events,
    PlantRawRowStats? RawStats = null);

/// <summary>
/// Resultat fra <see cref="OperlogCsvParser.ParseMultiPlant"/> — én CSV
/// kan inneholde events fra flere stasjoner. Fordeling per plant ligger i
/// <see cref="EventsByPlantId"/>.
///
/// <see cref="RowsByPlantId"/> teller alle rader som tilhører en plant
/// (inkludert settpunkt-endringer og andre events som ikke produserer
/// state-changes). Brukes til å logge "data mottatt for plant" til
/// data_imports selv når ingen enkelt-event er state-classified —
/// ellers ville et anlegg med kun settpunkt-events i en periode bli
/// rapportert som "manglende data" i status-matrisen.
/// </summary>
public sealed record MultiPlantOperlogParseResult(
    int RowsParsed,
    int RowsSkipped,
    int UnknownStations,
    IReadOnlyList<string> UnknownStationNames,
    IReadOnlyDictionary<string, IReadOnlyList<ClassifiedEvent>> EventsByPlantId,
    IReadOnlyDictionary<string, PlantRawRowStats> RowsByPlantId);

/// <summary>
/// Per-plant rad-telling og tidsstempel-grenser for operlog-importer.
/// Bruksområde: logging til data_imports-tabellen så status-matrisen ser
/// at "noen rader for denne plant ble mottatt for denne perioden".
/// </summary>
public sealed record PlantRawRowStats(
    int RowCount,
    DateTimeOffset MinTimestampUtc,
    DateTimeOffset MaxTimestampUtc);
