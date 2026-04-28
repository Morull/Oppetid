using MudBlazor;

namespace KraftverkUptime.Web.Theming;

/// <summary>
/// MudBlazor-tema med Dalane-Kraft-inspirert palett. Primærfargen er en dyp
/// korporat-blå som nikker til vannkraft; sekundærfargen er en teal som
/// kompletterer uten å konkurrere. Både lys- og mørk-variant er definert
/// slik at brukeren kan toggle.
/// </summary>
public static class DalaneTheme
{
    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1E5F8E",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#00A99D",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#F39C12",
            Info = "#2196F3",
            Success = "#2A9D8F",
            Warning = "#E9C46A",
            Error = "#E76F51",

            Background = "#F5F7FA",
            Surface = "#FFFFFF",
            AppbarBackground = "#0A2540",
            AppbarText = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1A2A3A",
            DrawerIcon = "#1E5F8E",

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
            Primary = "#4A9FD5",
            PrimaryContrastText = "#0A1628",
            Secondary = "#00C9B7",
            SecondaryContrastText = "#0A1628",
            Tertiary = "#FFB74D",
            Info = "#64B5F6",
            Success = "#66BB6A",
            Warning = "#FFD54F",
            Error = "#EF5350",

            Background = "#0A1628",
            Surface = "#142235",
            AppbarBackground = "#061424",
            AppbarText = "#E8EEF4",
            DrawerBackground = "#0F1E2F",
            DrawerText = "#E8EEF4",
            DrawerIcon = "#4A9FD5",

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
