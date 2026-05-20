namespace KraftverkUptime.Web.Theming;

/// <summary>
/// Semantiske fargetokens — ÉN kilde til sannhet for hva "positivt", "negativt"
/// osv. betyr visuelt. Erstatter ~17 hardkodede hex-verdier spredt utover
/// Razor-sidene (UI-gjennomgang 2026-05-20 tiltak 2).
///
/// Verdiene er CSS-variabler fra MudBlazor's palette så lys- og mørk-modus
/// virker automatisk. <see cref="DalaneTheme"/> styrer hvilke faktiske hex-
/// verdier hver mode bruker — ingen hardkoding her.
///
/// Bruk via <see cref="ForAccent(KpiAccent)"/> for KpiCard, eller direkte for
/// inline-styles:
/// <code>
///   Style="@($"border-left: 4px solid {SemanticColors.Positive};")"
/// </code>
/// </summary>
public static class SemanticColors
{
    /// <summary>God status / oppgang når høyere-er-bedre. Brand-grønn.</summary>
    public const string Positive = "var(--mud-palette-success)";

    /// <summary>Dårlig status / fall når høyere-er-bedre. Tema-rød.</summary>
    public const string Negative = "var(--mud-palette-error)";

    /// <summary>Advarsel — bør sees på, men ikke kritisk.</summary>
    public const string Warning = "var(--mud-palette-warning)";

    /// <summary>Nøytral / informativ. Brand-blå.</summary>
    public const string Neutral = "var(--mud-palette-primary)";

    /// <summary>Sekundær / støttende. Brand-teal.</summary>
    public const string Accent = "var(--mud-palette-secondary)";

    /// <summary>Ingen verdi / inaktiv. Disabled-grå.</summary>
    public const string Muted = "var(--mud-palette-text-disabled)";

    /// <summary>
    /// Velger fargetoken for en gitt <see cref="KpiAccent"/>. <c>Auto</c>
    /// returnerer <see cref="Neutral"/> — kallere som ønsker trend-utledning
    /// må håndtere det selv (typisk via <c>TrendCurrent</c>/<c>TrendPrevious</c>
    /// i KpiCard).
    /// </summary>
    public static string ForAccent(KpiAccent accent) => accent switch
    {
        KpiAccent.Positive => Positive,
        KpiAccent.Negative => Negative,
        KpiAccent.Warning => Warning,
        KpiAccent.Accent => Accent,
        KpiAccent.Muted => Muted,
        _ => Neutral,
    };

    /// <summary>
    /// Velger fargetoken basert på trend mot forrige periode, med
    /// <paramref name="higherIsBetter"/>-vridning. Returnerer
    /// <see cref="Neutral"/> hvis trend er flat eller mangler grunnlag.
    /// </summary>
    public static string ForTrend(double? current, double? previous, bool higherIsBetter)
    {
        if (!current.HasValue || !previous.HasValue || previous.Value == 0)
        {
            return Neutral;
        }
        var pct = (current.Value - previous.Value) / Math.Abs(previous.Value) * 100;
        // < 0.5 % regnes som flat — drifts-leder bryr seg ikke om støy.
        if (Math.Abs(pct) < 0.5) return Neutral;
        var isUp = pct > 0;
        var isGood = isUp == higherIsBetter;
        return isGood ? Positive : Negative;
    }
}

/// <summary>
/// Semantisk fargerolle for en KPI-verdi. Bruk for å unngå hardkodet hex.
/// <see cref="Auto"/> betyr "la KpiCard utlede fra trend, eller bruk Neutral".
/// </summary>
public enum KpiAccent
{
    Auto,
    Neutral,
    Positive,
    Negative,
    Warning,
    Accent,
    Muted,
}

/// <summary>
/// Visnings-tilstand for KpiCard. <see cref="NoData"/> erstatter dagens
/// tvetydige «–»-streng som ble forvekslet med null.
/// </summary>
public enum KpiState
{
    Normal,
    NoData,
    Loading,
}
