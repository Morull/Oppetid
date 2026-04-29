using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Detekterer hvilke trip-events som ble forårsaket av tett inntaksrist
/// istedenfor mekanisk turbin-feil. Et rist-event matches mot en
/// rist-falltap-alarm i operlog innen ±<see cref="RistDetectionOptions.WindowMinutes"/>
/// minutter rundt trip-tidspunktet.
///
/// Bakgrunn: Liavatn Q1 2026 hadde 55 turbin-trips, hvor 73 % (≈40) skjedde
/// innen 60 min etter en `*_INNTAK_RIST_FALLTAP_*_AL`-alarm. Disse er ikke
/// "ekte" turbin-feil — de er konsekvenser av løvfall, is, fremmedlegemer.
/// </summary>
public interface IRistAlarmDetector
{
    /// <summary>
    /// Returnerer settet av sub-time-presise rist-alarm-tidspunkter (UTC) for
    /// plantet i perioden. Hentes fra <see cref="ClassifiedEvent"/>-rader med
    /// cause-code <c>operlog:rist-falltap</c>.
    /// </summary>
    IReadOnlyList<DateTimeOffset> ExtractRistAlarmTimes(IEnumerable<ClassifiedEvent> operlogEvents);

    /// <summary>
    /// Sjekker om en gitt trip-tid er innen vindu-grensen til en rist-alarm.
    /// Vindu: [tripTime − window, tripTime + window].
    /// </summary>
    bool IsRistRelated(
        DateTimeOffset tripTimeUtc,
        IReadOnlyList<DateTimeOffset> ristAlarmTimes);
}

/// <summary>
/// Standard-implementasjon av <see cref="IRistAlarmDetector"/>. Pure funksjon —
/// ingen DB-tilgang. Caller mater inn ClassifiedEvent-listen som hentes fra
/// operlog-importen.
/// </summary>
public sealed class RistAlarmDetector : IRistAlarmDetector
{
    private const string RistCauseCode = "operlog:rist-falltap";
    private readonly RistDetectionOptions _options;

    public RistAlarmDetector() : this(new RistDetectionOptions()) { }
    public RistAlarmDetector(RistDetectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<DateTimeOffset> ExtractRistAlarmTimes(IEnumerable<ClassifiedEvent> operlogEvents)
    {
        ArgumentNullException.ThrowIfNull(operlogEvents);
        return operlogEvents
            .Where(e => string.Equals(e.CauseCode, RistCauseCode, StringComparison.Ordinal))
            .Select(e => e.StartUtc)
            .OrderBy(t => t)
            .ToList();
    }

    public bool IsRistRelated(
        DateTimeOffset tripTimeUtc,
        IReadOnlyList<DateTimeOffset> ristAlarmTimes)
    {
        ArgumentNullException.ThrowIfNull(ristAlarmTimes);
        if (ristAlarmTimes.Count == 0) return false;

        var window = TimeSpan.FromMinutes(_options.WindowMinutes);
        foreach (var alarmTime in ristAlarmTimes)
        {
            if (alarmTime < tripTimeUtc - window) continue;
            if (alarmTime > tripTimeUtc + window) break; // sortert: kan stoppe tidlig
            return true;
        }
        return false;
    }
}

/// <summary>
/// Konfigurasjon for rist-deteksjonen. Default 60 min er empirisk fra Liavatn-
/// data (73 % korrelasjon). Kan tunes per anlegg senere via PlantConfig om
/// f.eks. lange inntakstunneler gir lengre forsinkelse mellom alarm og trip.
/// </summary>
public sealed record RistDetectionOptions
{
    public int WindowMinutes { get; init; } = 60;
}
