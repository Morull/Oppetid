namespace KraftverkUptime.Web.Services;

/// <summary>
/// Delt filter-state mellom /nedetid og /vakt-roi (og potensielt andre sider).
/// Husker valgt anlegg + periode for én browser-økt slik at brukeren ikke
/// må velge på nytt når de bytter mellom sider.
///
/// Registreres som Singleton i Web/Program.cs — i Blazor WASM tilsvarer
/// Singleton "én per fane" siden hver fane har egen DI-container.
///
/// Sidene lytter på <see cref="OnChange"/> hvis de ønsker å re-render når
/// state endres fra en annen side; alternativt leses verdiene bare ved
/// OnInitializedAsync.
/// </summary>
public sealed class FilterState
{
    private string _plantId = string.Empty;
    private DateTime? _fromDate;
    private DateTime? _toDate;

    // Cache av siste respons per side. Holder også en "snapshot" av filteret
    // som var aktivt da responsen ble lagret, så caller kan verifisere at
    // cachen fortsatt er gyldig.
    public NedetidResponse? CachedNedetid { get; private set; }
    public VaktRoiResponse? CachedVaktRoi { get; private set; }
    private FilterSnapshot? _nedetidSnapshot;
    private FilterSnapshot? _vaktRoiSnapshot;

    public string PlantId
    {
        get => _plantId;
        set { if (_plantId != value) { _plantId = value; InvalidateAll(); NotifyChange(); } }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set { if (_fromDate != value) { _fromDate = value; InvalidateAll(); NotifyChange(); } }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set { if (_toDate != value) { _toDate = value; InvalidateAll(); NotifyChange(); } }
    }

    /// <summary>True hvis alle felt er fylt slik at en spørring kan utføres.</summary>
    public bool ErKlar =>
        !string.IsNullOrEmpty(_plantId) && _fromDate.HasValue && _toDate.HasValue;

    public event Action? OnChange;

    /// <summary>
    /// Setter alle felt på én gang og fyrer event én gang. Brukes når en side
    /// initialiserer fra default-verdier.
    /// </summary>
    public void SetIfEmpty(string plantId, DateTime fromDate, DateTime toDate)
    {
        var changed = false;
        if (string.IsNullOrEmpty(_plantId)) { _plantId = plantId; changed = true; }
        if (!_fromDate.HasValue) { _fromDate = fromDate; changed = true; }
        if (!_toDate.HasValue) { _toDate = toDate; changed = true; }
        if (changed) NotifyChange();
    }

    /// <summary>
    /// Lagrer respons fra /nedetid sammen med snapshot av aktivt filter.
    /// Brukes ved navigasjon: neste side-lasting kan returnere cachet svar
    /// hvis filteret er uendret.
    /// </summary>
    public void StoreNedetid(NedetidResponse response)
    {
        CachedNedetid = response;
        _nedetidSnapshot = Snapshot();
    }

    /// <summary>True hvis cache for /nedetid er gyldig for nåværende filter.</summary>
    public bool HasFreshNedetid()
        => CachedNedetid is not null && _nedetidSnapshot?.Matches(_plantId, _fromDate, _toDate) == true;

    public void StoreVaktRoi(VaktRoiResponse response)
    {
        CachedVaktRoi = response;
        _vaktRoiSnapshot = Snapshot();
    }

    public bool HasFreshVaktRoi()
        => CachedVaktRoi is not null && _vaktRoiSnapshot?.Matches(_plantId, _fromDate, _toDate) == true;

    private FilterSnapshot Snapshot() => new(_plantId, _fromDate, _toDate);

    private void InvalidateAll()
    {
        CachedNedetid = null;
        CachedVaktRoi = null;
        _nedetidSnapshot = null;
        _vaktRoiSnapshot = null;
    }

    private void NotifyChange() => OnChange?.Invoke();

    private sealed record FilterSnapshot(string PlantId, DateTime? FromDate, DateTime? ToDate)
    {
        public bool Matches(string plantId, DateTime? fromDate, DateTime? toDate)
            => PlantId == plantId && FromDate == fromDate && ToDate == toDate;
    }
}
