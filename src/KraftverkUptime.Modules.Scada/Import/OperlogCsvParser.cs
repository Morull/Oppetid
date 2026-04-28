using System.Globalization;
using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Import;

/// <summary>
/// Parser for Drivdal-format operlog-CSV. Eventer fra SCADA-operatorlog
/// og alarm-tabellen, med sekund-presisjon på timestamps.
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

        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Tom operlog-CSV — manglende header-rad.");
        var headers = headerLine.Split(';').Select(h => h.Trim()).ToList();

        int IdxOf(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var iTimestamp = IdxOf("timestamp");
        var iTag = IdxOf("tag");
        var iText = IdxOf("text");
        var iValue = IdxOf("value");
        var iAlarmType = IdxOf("alarmType");
        var iOffTimestamp = IdxOf("offTimestamp");
        var iUsername = IdxOf("username");

        if (iTimestamp < 0 || iTag < 0)
        {
            throw new InvalidDataException(
                "Operlog-CSV mangler påkrevde kolonner (timestamp, tag).");
        }

        var events = new List<ClassifiedEvent>();
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
            var value = iValue >= 0 ? cols[iValue] : "";
            var alarmType = iAlarmType >= 0 ? cols[iAlarmType] : "";
            var username = iUsername >= 0 ? cols[iUsername] : "";

            var (state, cause) = MapEvent(tag, alarmType);
            if (state is null) { rowsSkipped++; continue; }  // settpunkt-endring eller annet uten state-impact

            var rationale = string.IsNullOrWhiteSpace(text)
                ? $"operlog: {tag}"
                : $"operlog: {tag} — {text}";

            events.Add(new ClassifiedEvent(
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

        return new OperlogParseResult(plantId, rowsParsed, rowsSkipped, events);
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
    IReadOnlyList<ClassifiedEvent> Events);
