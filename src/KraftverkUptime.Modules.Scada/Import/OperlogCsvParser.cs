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

            var mapping = MapEvent(tag, alarmType);
            if (mapping.State is null)
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
                State: mapping.State.Value,
                CauseCode: mapping.Cause,
                Confidence: mapping.Confidence,
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
    /// Mapper en operlog-event til <see cref="OperlogMapping"/> som inkluderer
    /// (UnitState, CauseCode, Confidence). Returnerer State=null for settpunkt-
    /// endringer og andre operatorlog-events som ikke skal påvirke klassifisering.
    ///
    /// Konfidens-skala (brukes av FusionClassifier til å vekte mot SCADA):
    ///   0.95 — klare nedetid-utløsere (start/stop, nodstopp, hurtigstopp, havari)
    ///   0.80 — bekreftede tekniske feil (turb-feil, generisk feil)
    ///   0.50 — terskel-alarmer (HH/LL) der SCADA-trender bør bekrefte
    ///   0.40 — øvrige alarmer (annoteringer, ikke selvstendig nedetid)
    ///
    /// FusionClassifier overstyrer aldri SCADA-state med operlog, så confidence
    /// her er primært for sortering/filtrering i nedetid-tidslinjen.
    /// </summary>
    private static OperlogMapping MapEvent(string tag, string alarmType)
    {
        var t = tag.ToUpperInvariant();
        var isEventType = alarmType.Equals("event", StringComparison.OrdinalIgnoreCase);
        var isAlarmType = alarmType.Equals("alarm", StringComparison.OrdinalIgnoreCase);
        var isAlarmRow = isEventType || isAlarmType;

        // 1) Klare drift-overganger (start/stop) — høy confidence
        if (t.Contains("STARTER_AL"))
        {
            return new(UnitState.InService, "operlog:start", 0.95);
        }
        if (t.Contains("STOPPER_AL"))
        {
            return new(UnitState.MaintenanceOutage, "operlog:stop", 0.95);
        }

        // 2) Nødstopp / hurtigstopp — eksplisitt nedetid
        if (t.Contains("NODSTOPP"))
        {
            return new(UnitState.ForcedOutage, "operlog:nodstopp", 0.95);
        }
        if (t.Contains("HURTIGSTOPP"))
        {
            return new(UnitState.ForcedOutage, "operlog:hurtigstopp", 0.95);
        }

        // 3) Rist-falltap (tett inntaksrist) — egen cause-code for rist-detektor
        if (t.Contains("RIST_FALLTAP"))
        {
            return new(UnitState.ForcedOutage, "operlog:rist-falltap", 0.85);
        }

        // 4) Tekniske feil (turbinfeil, generisk feil, havari)
        if (t.Contains("TURB_FEIL"))
        {
            return new(UnitState.ForcedOutage, "operlog:turb-feil", 0.85);
        }
        if (t.Contains("FEIL_AL") || t.Contains("HAVARI"))
        {
            return new(UnitState.ForcedOutage, "operlog:fault", 0.80);
        }

        // 5) Terskel-alarmer (lav-lav / høy-høy) — registreres som svake nedetid-
        // signaler. FusionClassifier bevarer SCADA-state hvis enheten faktisk
        // produserer, så disse blir kun synlige som markører i tidslinjen og
        // beriker rationale/tooltip.
        if (isAlarmRow && t.Contains("_LL_AL"))
        {
            return new(UnitState.ForcedOutage, "operlog:lav-lav-alarm", 0.50);
        }
        if (isAlarmRow && t.Contains("_HH_AL"))
        {
            return new(UnitState.ForcedOutage, "operlog:hoy-hoy-alarm", 0.50);
        }

        // 6) Generiske alarmer med _AL-suffiks (uansett alarmType) — tag som
        // MaintenanceOutage med lav confidence så de er sporbare i historikken
        // uten å konkurrere med SCADA på state. Tidligere krevde dette
        // alarmType=event — men alarmType=alarm er den vanlige verdien for
        // aktive alarmer i KraftScada-eksporten, så vi inkluderer begge.
        if (isAlarmRow && t.Contains("_AL"))
        {
            return new(UnitState.MaintenanceOutage, "operlog:annen-alarm", 0.40);
        }

        // Settpunkt-endringer (KONTROLL_REG_*, AGC_*, etc.) påvirker ikke state.
        return new(null, null, 0);
    }

    /// <summary>Resultat av <see cref="MapEvent"/>.</summary>
    private readonly record struct OperlogMapping(
        UnitState? State,
        string? Cause,
        double Confidence);

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
