namespace KraftverkUptime.Modules.Settlement.Parsing;

/// <summary>
/// Mapping fra norske kolonnenavn i portaleksport til kanonske felt i
/// <see cref="Dtos.SettlementHourlyRow"/>. To valg gjøres eksplisitt her:
///
/// 1. Varianter for norske spesialtegn håndteres ved å inkludere begge skrivemåter
///    (<c>Oppgjor</c> og <c>Oppgjør</c>, <c>RK-kjop</c> og <c>RK-kjøp</c>).
///    Det er tryggere å støtte begge enn å normalisere til én form ved parsing,
///    fordi portalen historisk har skrevet begge varianter.
///
/// 2. Kolonnenavnsammenligning er case-insensitive men trim-sensitive.
///    Dobbelt-mellomrom i portalens headere har ført til parseerror før – derfor
///    trimmes alle navn inn før lookup.
/// </summary>
public static class SettlementColumnMapping
{
    /// <summary>Kanonisk navn for en gitt header-streng.</summary>
    public static string? Canonical(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        var key = Normalize(header);
        return _map.TryGetValue(key, out var canonical) ? canonical : null;
    }

    public const string Time = "time_local";
    public const string MwhElhub = "mwh_elhub";
    public const string MwhESett = "mwh_esett";
    public const string Spotbud = "spotbud_mwh";
    public const string Spotpris = "spotpris_nok_mwh";
    public const string Spotomsetning = "spotomsetning_nok";
    public const string Ubalanse = "ubalanse_mwh";
    public const string RkPris = "rk_pris_nok_mwh";
    public const string RkKjop = "rk_kjop_nok";
    public const string RkSalg = "rk_salg_nok";
    public const string NordPoolGebyr = "nord_pool_gebyr_nok";
    public const string ESettVolumgebyr = "esett_volumgebyr_nok";
    public const string ESettUbalansegebyr = "esett_ubalansegebyr_nok";
    public const string SumSalg = "sum_salg_nok";
    public const string Meglerprovisjon = "meglerprovisjon_nok";
    public const string Oppgjor = "oppgjor_nok";
    public const string BruttoOmsetning = "brutto_omsetning_nok";
    public const string Produksjonplan = "produksjonplan_mwh";
    public const string Effekt = "effektavlesninger_mw";
    public const string AbsUbalansevolum = "abs_ubalansevolum_mwh";
    public const string UbalanseResultat = "ubalanse_resultat_nok";

    private static string Normalize(string s) =>
        s.Trim().Replace("  ", " ", StringComparison.Ordinal).ToLowerInvariant();

    private static readonly Dictionary<string, string> _map = Build();

    private static Dictionary<string, string> Build()
    {
        // Nøklene er normalisert (lowercase, trimmet) – verdien er kanonisk felt.
        var m = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["time"] = Time,
            ["tidsserie"] = Time, // Summering-faner bruker "Tidsserie"
            ["mwh-elhub"] = MwhElhub,
            ["mwh-esett"] = MwhESett,
            ["spotbud"] = Spotbud,
            ["spotpris"] = Spotpris,
            ["spotomsetning"] = Spotomsetning,
            ["ubalanse"] = Ubalanse,
            ["rk-pris"] = RkPris,
            ["rk-kjop"] = RkKjop,
            ["rk-kjøp"] = RkKjop,
            ["rk-salg"] = RkSalg,
            ["nord pool gebyr"] = NordPoolGebyr,
            ["esett volumgebyr"] = ESettVolumgebyr,
            ["esett ubalansegebyr"] = ESettUbalansegebyr,
            ["sum salg"] = SumSalg,
            ["meglerprovisjon"] = Meglerprovisjon,
            ["oppgjor"] = Oppgjor,
            ["oppgjør"] = Oppgjor,
            ["brutto omsetning"] = BruttoOmsetning,
            ["produksjonplan"] = Produksjonplan,
            ["effektavlesninger"] = Effekt,
            ["absolutt ubalansevolum"] = AbsUbalansevolum,
            ["tap/gevinst ubalanse eks. gebyr"] = UbalanseResultat,
        };
        return m;
    }
}
