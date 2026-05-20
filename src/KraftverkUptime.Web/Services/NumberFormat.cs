using System.Globalization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Felles nb-NO-tallformat (mellomrom som tusenskille, komma som desimal-
/// separator). Erstatter 9 kopier av samme <see cref="NumberFormatInfo"/>-
/// initialisering spredt over Razor-sidene (UI-gjennomgang 2026-05-20 tiltak 9).
///
/// Bakgrunn: Blazor WASM kjøres med <c>InvariantGlobalization=true</c> og
/// <c>CultureInfo.GetCultureInfo("nb-NO")</c> kaster <c>CultureNotFoundException</c>.
/// Vi må derfor bygge en bare <see cref="NumberFormatInfo"/> manuelt.
/// </summary>
public static class NumberFormat
{
    /// <summary>
    /// Norsk tallformat: mellomrom som gruppe-separator, komma som desimal,
    /// 3-sifrede grupper. Bruk via formaterings-string: <c>v.ToString("N0", NumberFormat.Norsk)</c>.
    /// </summary>
    public static readonly NumberFormatInfo Norsk = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 },
    };

    /// <summary>NOK med 0 desimaler ("19 618 NOK"). Null/NaN → "–".</summary>
    public static string Nok(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "–";
        return v.ToString("N0", Norsk);
    }

    /// <summary>NOK med fortegn ("+19 618" / "−19 618"). 0 → "–".</summary>
    public static string NokSigned(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v == 0) return "–";
        var sign = v > 0 ? "+" : "";
        return sign + v.ToString("N0", Norsk);
    }

    /// <summary>MWh med 1 desimal ("19 617,9"). 0 → "–".</summary>
    public static string Mwh(double v) =>
        v == 0 ? "–" : v.ToString("F1", Norsk);

    /// <summary>Andel formatert som prosent med 1 desimal ("87,3 %"). 0 → "–".</summary>
    public static string Ratio(double v) =>
        v == 0 ? "–" : (v * 100).ToString("F1", Norsk) + " %";

    /// <summary>Timer med 1 desimal. 0 → "–".</summary>
    public static string Hours(double v) =>
        v == 0 ? "–" : v.ToString("F1", Norsk);
}
