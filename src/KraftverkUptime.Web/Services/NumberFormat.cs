using System.Globalization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Felles nb-NO-tallformat (mellomrom som tusenskille, komma som desimal-
/// separator). Erstatter ~10 kopier av samme <see cref="NumberFormatInfo"/>-
/// initialisering spredt over Razor-sidene (UI-gjennomgang 2026-05-20 tiltak 9
/// + Testrapport 2026-05-21 funn #1 og #3).
///
/// Bakgrunn: Blazor WASM kjøres med <c>InvariantGlobalization=true</c> i csproj
/// (sparer ~10 MB ICU-nedlasting). Det betyr at
/// <c>new CultureInfo("nb-NO")</c> og <c>CultureInfo.GetCultureInfo("nb-NO")</c>
/// kaster <see cref="CultureNotFoundException"/> og trigger Blazors globale
/// feilbanner. Vi må derfor bygge en <see cref="NumberFormatInfo"/> manuelt og
/// passere den eksplisitt til alle <c>ToString(format, …)</c>-kall.
///
/// Konvensjon for "tomme" verdier: 0 og null vises som "–" i KPI-kort (slik at
/// reelle null-verdier ikke ser ut som "0,00 NOK"). Negative verdier vises med
/// fortegn — komponentene som har «alt over null = bra» skal bytte til
/// <see cref="NokSigned(double)"/> som setter "+" foran positive tall.
/// </summary>
public static class NumberFormat
{
    /// <summary>
    /// Norsk tallformat: mellomrom som gruppe-separator, komma som desimal,
    /// 3-sifrede grupper. Brukes direkte i <c>v.ToString("N0", NumberFormat.Norsk)</c>
    /// når en av <c>Nok/Mwh/...</c>-hjelperne ikke passer.
    /// </summary>
    public static readonly NumberFormatInfo Norsk = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 },
        PercentGroupSeparator = " ",
        PercentDecimalSeparator = ",",
        PercentGroupSizes = new[] { 3 },
    };

    // ----- NOK -------------------------------------------------------------

    /// <summary>NOK med 0 desimaler ("19 618 NOK"). 0/NaN/null → "–".</summary>
    public static string Nok(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "–";
        return v.ToString("N0", Norsk);
    }

    /// <summary>Nullable-variant: null → "–".</summary>
    public static string Nok(double? v) => v.HasValue ? Nok(v.Value) : "–";

    /// <summary>
    /// NOK med 2 desimaler og "kr"-suffiks ("19 618,42 kr"). Brukes til KAIA-
    /// kostnad der vi vil vise øre-nøyaktighet (megler-provisjon kan være ned
    /// til kroner). Null → "–".
    /// </summary>
    public static string NokKr2(double? v) =>
        v.HasValue ? v.Value.ToString("N2", Norsk) + " kr" : "–";

    /// <summary>NOK med fortegn ("+19 618" / "−19 618"). 0/null → "–".</summary>
    public static string NokSigned(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v == 0) return "–";
        var sign = v > 0 ? "+" : "";
        return sign + v.ToString("N0", Norsk);
    }

    /// <summary>Nullable-variant av <see cref="NokSigned(double)"/>.</summary>
    public static string NokSigned(double? v) => v.HasValue ? NokSigned(v.Value) : "–";

    // ----- Energi og effekt ------------------------------------------------

    /// <summary>MWh med 1 desimal ("19 617,9"). 0 → "–".</summary>
    public static string Mwh(double v) =>
        v == 0 ? "–" : v.ToString("F1", Norsk);

    /// <summary>Generisk N-format for "ren" tall, ingen suffiks. Null → "–".</summary>
    public static string Number(double? v, string format) =>
        v.HasValue ? v.Value.ToString(format, Norsk) : "–";

    /// <summary>Heltall med tusenskille ("12 345"). Null → "–".</summary>
    public static string IntegerN0(double? v) => Number(v, "N0");

    // ----- Andel / prosent -------------------------------------------------

    /// <summary>
    /// Andel formatert som prosent med 1 desimal ("87,3 %"). Verdien
    /// MULTIPLISERES med 100 — så <c>Ratio(0.873)</c> → "87,3 %". 0/null → "–".
    /// </summary>
    public static string Ratio(double v) =>
        v == 0 ? "–" : (v * 100).ToString("F1", Norsk) + " %";

    /// <summary>Nullable-variant av <see cref="Ratio(double)"/>.</summary>
    public static string Ratio(double? v) => v.HasValue ? Ratio(v.Value) : "–";

    /// <summary>
    /// Andel med ULIKE antall desimaler enn <see cref="Ratio(double)"/>.
    /// Multipliserer med 100. <c>Pct(0.873, 2)</c> → "87,30 %". null → "–".
    /// </summary>
    public static string Pct(double? v, int decimals) =>
        v.HasValue
            ? (v.Value * 100).ToString("F" + decimals, Norsk) + " %"
            : "–";

    /// <summary>
    /// Signert prosent med 1 desimal og fortegn ("+12,3 %", "−4,8 %"). For
    /// trend-deltaer i KPI-kort — verdien er ALLEREDE i prosent (multipliseres
    /// ikke). 0 → "0,0 %" (ikke "–", siden 0 her betyr "uendret").
    /// </summary>
    public static string SignedPctValue(double pct)
    {
        var sign = pct >= 0 ? "+" : "";
        return sign + pct.ToString("F1", Norsk) + " %";
    }

    // ----- Tid -------------------------------------------------------------

    /// <summary>Timer med 1 desimal. 0 → "–".</summary>
    public static string Hours(double v) =>
        v == 0 ? "–" : v.ToString("F1", Norsk);

    /// <summary>Nullable-variant av <see cref="Hours(double)"/>.</summary>
    public static string Hours(double? v) => v.HasValue ? Hours(v.Value) : "–";
}
