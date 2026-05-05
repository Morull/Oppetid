namespace KraftverkUptime.Web.Services;

/// <summary>
/// Slår opp brukervennlig visnings-tekst for en cause-kode. Singleton i
/// WASM-prosessen — laster aliaslisten én gang ved første lookup og cacher
/// resultatet i minnet.
///
/// Brukes av Nedetid/Rapport/Vakt-ROI-tabellene via <see cref="Display"/>.
/// Hvis ingen alias finnes, returneres rå cause-kode (eller en fallback for
/// <c>annotation:</c>-prefikser som klassifiseres separat).
///
/// <see cref="RefreshAsync"/> kalles etter at brukeren har oppdatert et alias
/// fra Kategorier-siden, slik at andre sider får oppdatert tekst neste gang
/// de rendrer.
/// </summary>
public sealed class CauseFormatter
{
    private readonly AnnotationsApi _api;
    private Dictionary<string, string>? _cache;
    private Task<Dictionary<string, string>>? _loading;

    public CauseFormatter(AnnotationsApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>
    /// Returnerer brukervennlig tekst for koden. Hvis cache ikke er lastet
    /// ennå, returnerer rå kode synkront og trigger asynkron last i bakgrunnen
    /// (caller bør rendre på nytt etter <see cref="EnsureLoadedAsync"/>).
    /// </summary>
    public string Display(string? causeCode)
    {
        if (string.IsNullOrWhiteSpace(causeCode)) return "–";
        if (_cache is null) return causeCode;
        return _cache.TryGetValue(causeCode, out var txt) ? txt : causeCode;
    }

    /// <summary>
    /// Sikrer at aliasene er lastet. Trygt å kalle flere ganger — andre kall
    /// venter på samme load-task. Caller bør kalle StateHasChanged etterpå
    /// for å rendre med oppdatert tekst.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_cache is not null) return;
        _loading ??= LoadInternalAsync();
        _cache = await _loading.ConfigureAwait(false);
    }

    /// <summary>Tvinger ny last (etter at brukeren har endret et alias).</summary>
    public async Task RefreshAsync()
    {
        _loading = LoadInternalAsync();
        _cache = await _loading.ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> LoadInternalAsync()
    {
        try
        {
            var aliases = await _api.ListCauseAliasesAsync().ConfigureAwait(false);
            return aliases.ToDictionary(a => a.CauseCode, a => a.DisplayText, StringComparer.Ordinal);
        }
        catch
        {
            // Hvis API feiler, tom cache → fall-back til rå koder. UI fungerer
            // fortsatt; brukeren ser intern kode istedenfor pen tekst.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
