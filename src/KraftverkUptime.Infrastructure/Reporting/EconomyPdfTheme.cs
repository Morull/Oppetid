namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Designtokens for Økonomi-rapport-PDF. Speiler appens mørke tema
/// (<c>dk-theme.css</c>) slik at PDF-en visuelt likner skjermbildet.
/// Alle verdier er hex-strenger uten "#" — QuestPDF og SkiaSharp tar imot
/// hex med eller uten "#", men vi normaliserer på "#"-prefiks her for
/// konsistens.
///
/// Endringer her er en visuell endring; ingen funksjonell betydning.
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md, del 6.
/// </summary>
public static class EconomyPdfTheme
{
    public const string Background = "#0F1620";
    public const string Surface = "#1A2330";
    public const string SurfaceAlt = "#222C3D";
    public const string TextPrimary = "#E6EDF5";
    public const string TextMuted = "#8DA0B5";
    public const string TextSubtle = "#5A6B7F";
    public const string AccentGood = "#3DDC97";
    public const string AccentBad = "#F45B69";
    public const string AccentNeutral = "#6B8AAB";
    public const string Border = "#2A364B";

    /// <summary>
    /// Sans-serif. Bruker QuestPDFs innebygde "Lato" som ikke krever
    /// fontconfig/freetype-pakker i container-imaget. "Inter" ble droppet
    /// fra første versjon fordi den krevde apt-pakker som vi ikke ville
    /// dra inn (Spec NESTE-CHAT-OKONOMI-OPPFOLGING.md Funn 5, 2026-05-22).
    /// </summary>
    public const string FontFamily = "Lato";
}
