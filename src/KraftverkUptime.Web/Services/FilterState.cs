using System.Text.Json;
using Microsoft.JSInterop;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Global filter-state delt på tvers av sider (anlegg + periode + granularitet).
/// Eksponert via AppBarPlantPeriodSelector i topp-baren slik at brukeren ikke
/// må velge på nytt når de bytter mellom sider.
///
/// Registreres som Singleton i Web/Program.cs — i Blazor WASM tilsvarer
/// Singleton "én per fane" siden hver fane har egen DI-container.
///
/// Sidene lytter på <see cref="OnChange"/> og re-loader når state endres
/// fra AppBar-velgeren eller en annen side. URL-route-parameter (eks.
/// /nedetid/drivdal) overstyrer state ved første lasting; etterpå er
/// state autoritativ.
/// </summary>
public sealed class FilterState
{
    private const string LocalStorageKey = "dk_filter_state_v1";

    private readonly IJSRuntime _js;
    private bool _suppressPersist;

    private string _plantId = string.Empty;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private PeriodGranularity _granularity = PeriodGranularity.Maned;

    public FilterState(IJSRuntime js)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
    }

    // ---- Cache av plant-liste -----------------------------------------------
    // AppBar-velgeren laster plants én gang ved oppstart og lagrer her, slik
    // at hver side slipper å laste på nytt. Sider som tidligere kalte
    // ReportsApi.ListPlantsAsync bør nå lese FilterState.Plants.

    public IReadOnlyList<PlantDto> Plants { get; private set; } = Array.Empty<PlantDto>();

    public void SetPlants(IReadOnlyList<PlantDto> plants)
    {
        Plants = plants;
        NotifyChange();
    }

    // ---- Cache av sist hentede respons per side -----------------------------

    public NedetidResponse? CachedNedetid { get; private set; }
    public VaktRoiResponse? CachedVaktRoi { get; private set; }
    private FilterSnapshot? _nedetidSnapshot;
    private FilterSnapshot? _vaktRoiSnapshot;

    // ---- Hovedfeltene -------------------------------------------------------

    public string PlantId
    {
        get => _plantId;
        set
        {
            if (_plantId == value) return;
            _plantId = value;
            InvalidateAll();
            NotifyChangeAndPersist();
        }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set
        {
            if (_fromDate == value) return;
            _fromDate = value;
            InvalidateAll();
            NotifyChangeAndPersist();
        }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set
        {
            if (_toDate == value) return;
            _toDate = value;
            InvalidateAll();
            NotifyChangeAndPersist();
        }
    }

    public PeriodGranularity Granularity
    {
        get => _granularity;
        set
        {
            if (_granularity == value) return;
            _granularity = value;
            // Granularitet alene invaliderer ikke cache — det er datoene som teller.
            NotifyChangeAndPersist();
        }
    }

    /// <summary>True hvis alle felt er fylt slik at en spørring kan utføres.</summary>
    public bool ErKlar =>
        !string.IsNullOrEmpty(_plantId) && _fromDate.HasValue && _toDate.HasValue;

    public event Action? OnChange;

    /// <summary>
    /// Setter periode + granularitet i én operasjon (én OnChange-event).
    /// Brukes av hurtigvalg-knapper og ←/→-pilene i AppBar.
    /// </summary>
    public void SetPeriod(DateTime from, DateTime to, PeriodGranularity granularity)
    {
        var changed = false;
        if (_fromDate != from) { _fromDate = from; changed = true; }
        if (_toDate != to) { _toDate = to; changed = true; }
        if (_granularity != granularity) { _granularity = granularity; changed = true; }
        if (!changed) return;
        InvalidateAll();
        NotifyChangeAndPersist();
    }

    /// <summary>
    /// Setter alle felt på én gang og fyrer event én gang. Brukes ved oppstart
    /// for å initialisere fra default-verdier hvis localStorage er tom.
    /// </summary>
    public void SetIfEmpty(string plantId, DateTime fromDate, DateTime toDate)
    {
        var changed = false;
        if (string.IsNullOrEmpty(_plantId)) { _plantId = plantId; changed = true; }
        if (!_fromDate.HasValue) { _fromDate = fromDate; changed = true; }
        if (!_toDate.HasValue) { _toDate = toDate; changed = true; }
        if (changed) NotifyChangeAndPersist();
    }

    // ---- Cache-respons-API (uendret fra v1) ---------------------------------

    public void StoreNedetid(NedetidResponse response)
    {
        CachedNedetid = response;
        _nedetidSnapshot = Snapshot();
    }

    public bool HasFreshNedetid()
        => CachedNedetid is not null && _nedetidSnapshot?.Matches(_plantId, _fromDate, _toDate) == true;

    public void StoreVaktRoi(VaktRoiResponse response)
    {
        CachedVaktRoi = response;
        _vaktRoiSnapshot = Snapshot();
    }

    public bool HasFreshVaktRoi()
        => CachedVaktRoi is not null && _vaktRoiSnapshot?.Matches(_plantId, _fromDate, _toDate) == true;

    /// <summary>
    /// Tømmer alle cachet API-responser. Brukes etter at en annotering er
    /// lagret/slettet, slik at Nedetid + Vakt-ROI henter ferske data ved
    /// neste navigasjon. Endrer ikke filter-felt og fyrer ikke OnChange
    /// (caller styrer reload-tidspunkt selv).
    /// </summary>
    public void InvalidateCaches() => InvalidateAll();

    private FilterSnapshot Snapshot() => new(_plantId, _fromDate, _toDate);

    private void InvalidateAll()
    {
        CachedNedetid = null;
        CachedVaktRoi = null;
        _nedetidSnapshot = null;
        _vaktRoiSnapshot = null;
    }

    private void NotifyChange() => OnChange?.Invoke();

    private void NotifyChangeAndPersist()
    {
        OnChange?.Invoke();
        if (!_suppressPersist)
        {
            // Fire-and-forget. Vi venter ikke — UI skal være responsiv,
            // og persist-feil er ikke kritiske (worst case: state tapes
            // ved page-reload).
            _ = PersistAsync();
        }
    }

    // ---- localStorage-persistens --------------------------------------------

    /// <summary>
    /// Lastes på app-oppstart fra MainLayout. Setter felt fra forrige sesjon.
    /// Suppresser persist + change-events under lasting slik at vi ikke
    /// trigger en re-write umiddelbart.
    /// </summary>
    public async Task LoadFromStorageAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>(
                "localStorage.getItem", LocalStorageKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return;

            var dto = JsonSerializer.Deserialize<PersistedState>(json);
            if (dto is null) return;

            _suppressPersist = true;
            try
            {
                if (!string.IsNullOrEmpty(dto.PlantId)) _plantId = dto.PlantId;
                if (dto.FromDate.HasValue) _fromDate = dto.FromDate;
                if (dto.ToDate.HasValue) _toDate = dto.ToDate;
                if (Enum.TryParse<PeriodGranularity>(dto.Granularity, out var g))
                {
                    _granularity = g;
                }
                NotifyChange();
            }
            finally
            {
                _suppressPersist = false;
            }
        }
        catch
        {
            // Ignorer feil — bruker får default-state.
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            var dto = new PersistedState(
                _plantId,
                _fromDate,
                _toDate,
                _granularity.ToString());
            var json = JsonSerializer.Serialize(dto);
            await _js.InvokeVoidAsync("localStorage.setItem", LocalStorageKey, json)
                .ConfigureAwait(false);
        }
        catch
        {
            // Ignorer feil — persist er best-effort.
        }
    }

    private sealed record FilterSnapshot(string PlantId, DateTime? FromDate, DateTime? ToDate)
    {
        public bool Matches(string plantId, DateTime? fromDate, DateTime? toDate)
            => PlantId == plantId && FromDate == fromDate && ToDate == toDate;
    }

    private sealed record PersistedState(
        string PlantId,
        DateTime? FromDate,
        DateTime? ToDate,
        string Granularity);
}
