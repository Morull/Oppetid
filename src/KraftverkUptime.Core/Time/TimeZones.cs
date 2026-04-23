namespace KraftverkUptime.Core.Time;

/// <summary>
/// Sentrale tidssoneverdier. UTC internt, Europe/Oslo ved I/O.
/// </summary>
public static class TimeZones
{
    /// <summary>IANA-navn for norsk tidssone. Foretrukket; fungerer på Linux og Windows (med ICU aktivert).</summary>
    public const string NorwayIana = "Europe/Oslo";

    /// <summary>
    /// Windows-native ID for norsk tidssone. Fallback når ICU er deaktivert
    /// (<c>InvariantGlobalization=true</c>) og IANA→Windows-mapping ikke er tilgjengelig.
    /// </summary>
    public const string NorwayWindows = "W. Europe Standard Time";

    /// <summary>Cached TimeZoneInfo-instans. Prøver IANA først, faller tilbake til Windows-ID.</summary>
    public static readonly TimeZoneInfo Norway = ResolveNorway();

    private static TimeZoneInfo ResolveNorway()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(NorwayIana);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows uten ICU (InvariantGlobalization): bruk Windows-native ID.
            return TimeZoneInfo.FindSystemTimeZoneById(NorwayWindows);
        }
    }
}
