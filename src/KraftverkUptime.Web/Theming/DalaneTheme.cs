using MudBlazor;

namespace KraftverkUptime.Web.Theming;

/// <summary>
/// MudBlazor-tema som matcher Dalane Krafts visuelle profil (hentet fra
/// dalane-kraft.no 2026-05-04). Logoens karakteristiske grønn → teal → blå-
/// gradient gjennom heksagon-konturen er oversatt til:
///   Primary  = blå (#1F84B7) — matcher "KRAFT"-teksten i logoen
///   Secondary = teal (#1AA09F) — matcher midten av heksagon-gradienten
///   Tertiary = grønn (#2A8B5C) — matcher topp-venstre av heksagon-gradienten
///
/// Både lys- og mørk-variant er definert. Mørk-mode bruker lysere skygger
/// av samme palett for å bevare brand-gjenkjennelse uten å bli grell.
/// </summary>
public static class DalaneTheme
{
    // Dalane Kraft brand-palette (lest fra logo 2026-05-04)
    private const string BrandBlue = "#1F84B7";        // hoved — matcher KRAFT-teksten
    private const string BrandTeal = "#1AA09F";        // midt-gradient
    private const string BrandGreen = "#2A8B5C";       // topp-gradient
    private const string BrandBlueDeep = "#0E2F47";    // mørk variant for app-bar

    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = BrandBlue,
            PrimaryContrastText = "#FFFFFF",
            Secondary = BrandTeal,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = BrandGreen,
            Info = "#3DA1C9",          // lys variant av primary for info-meldinger
            Success = BrandGreen,      // brukes til OK-status (komplett-celler etc.)
            Warning = "#E9A235",       // varm gul/oransje, kontraster mot kald palett
            Error = "#D14B3F",         // dempet rød

            Background = "#F4F8FB",    // svak blå-tone — matcher kald brand
            Surface = "#FFFFFF",
            AppbarBackground = BrandBlueDeep,
            AppbarText = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1A2A3A",
            DrawerIcon = BrandBlue,

            TextPrimary = "#1A2A3A",
            TextSecondary = "#4A5A6A",
            TextDisabled = "#9AA5B0",
            ActionDefault = "#4A5A6A",
            ActionDisabled = "#CBD3DC",

            DividerLight = "#E1E6ED",
            Divider = "#D4DAE3",
        },
        PaletteDark = new PaletteDark
        {
            // Lysere variant av brand-fargene for god kontrast på mørk bakgrunn
            Primary = "#4DB0DC",
            PrimaryContrastText = "#0A1628",
            Secondary = "#3FCBC9",
            SecondaryContrastText = "#0A1628",
            Tertiary = "#5BCC8E",
            Info = "#67BEE2",
            Success = "#5BCC8E",
            Warning = "#F0BB55",
            Error = "#E5685C",

            Background = "#0A1A2A",
            Surface = "#13263A",
            AppbarBackground = "#061321",
            AppbarText = "#E8EEF4",
            DrawerBackground = "#0F1F31",
            DrawerText = "#E8EEF4",
            DrawerIcon = "#4DB0DC",

            TextPrimary = "#E8EEF4",
            TextSecondary = "#A8B5C4",
            TextDisabled = "#6A7685",
            ActionDefault = "#A8B5C4",
            ActionDisabled = "#3A4654",

            DividerLight = "#233248",
            Divider = "#1B2A3E",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "system-ui", "Segoe UI", "Roboto", "sans-serif"],
                FontSize = "0.95rem",
                LineHeight = "1.5",
            },
            H1 = new H1Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "2.2rem", FontWeight = "600" },
            H2 = new H2Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1.6rem", FontWeight = "600" },
            H3 = new H3Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1.3rem", FontWeight = "600" },
            H4 = new H4Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1.15rem", FontWeight = "500" },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "600" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "64px",
        },
    };
}
