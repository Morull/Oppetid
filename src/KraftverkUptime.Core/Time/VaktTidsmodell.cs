namespace KraftverkUptime.Core.Time;

/// <summary>
/// Modellerer vakt-vinduet for hjem-vakt-ordningen ved kraftverk.
/// I drifts-leder-presentasjon for april 2026:
///
///   Vakt aktiv:
///     – Mandag-fredag 15:00-07:00 (neste morgen)
///     – Hele lørdag og søndag
///     – Hele norske offentlige helligdager
///
///   Counterfactual "uten vakt":
///     – Driftspersonell møter neste arbeidsdag kl. 08:00 (lokal tid)
///     – Arbeidsdag = mandag-fredag som ikke er helligdag
///
/// Tidssone-håndtering: alle innparametere er DateTimeOffset (UTC). Modellen
/// konverterer til Europe/Oslo for å bestemme lokale klokkeslett, ukedag og
/// helligdag-status. Returverdier er igjen UTC for å holde resten av systemet
/// tidssone-agnostisk.
///
/// Modellen er parametrisert via <see cref="VaktTidsmodellOptions"/> slik at
/// testene kan stille inn andre vinduer uten å duplisere logikken.
/// </summary>
public sealed class VaktTidsmodell
{
    private readonly VaktTidsmodellOptions _options;
    private readonly TimeZoneInfo _tz;

    public VaktTidsmodell() : this(VaktTidsmodellOptions.Default) { }

    public VaktTidsmodell(VaktTidsmodellOptions options)
        : this(options, TimeZones.Norway) { }

    /// <summary>Internal ctor for tester som vil tvinge en spesifikk tidssone.</summary>
    public VaktTidsmodell(VaktTidsmodellOptions options, TimeZoneInfo tz)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tz = tz ?? throw new ArgumentNullException(nameof(tz));
    }

    public VaktTidsmodellOptions Options => _options;

    /// <summary>
    /// Sjekker om gitt UTC-tidspunkt faller innenfor vakt-vinduet.
    /// Konverterer til lokal tid, sjekker ukedag + helligdag, og avgjør om
    /// klokkeslettet er innenfor vakt-vinduets dag/natt-grenser.
    /// </summary>
    public bool ErInnenforVakt(DateTimeOffset utcTidspunkt)
    {
        var lokal = TimeZoneInfo.ConvertTime(utcTidspunkt, _tz);
        var dato = DateOnly.FromDateTime(lokal.DateTime);
        var tid = lokal.TimeOfDay;

        // Helger og helligdager: vakt aktiv hele døgnet.
        if (lokal.DayOfWeek == DayOfWeek.Saturday) return true;
        if (NorgesHelligdager.ErHelligdagEllerSondag(dato)) return true;

        // Hverdager (mandag-fredag): to scenarier basert på om vakt-vinduet
        // wraps over midnatt (default: start 15:00 → slutt 07:00 neste morgen)
        // eller er innenfor samme døgn (eks. kveldsvakt 15:00 → 23:00).
        var start = _options.EttermiddagStart;
        var slutt = _options.MorgenCutoff;

        if (slutt <= start)
        {
            // WRAP-tilfelle (default 15→07): vakt aktiv hvis enten
            //   - vi er FØR slutt-tid (forrige natts vakt løper fortsatt), eller
            //   - vi er ETTER eller PÅ start-tid (vakt starter for kvelden).
            // Helligdag/helg som strekker seg forbi midnatt: dekkes av
            // helg/helligdag-blokken over for selve dagen 00:00-23:59.
            if (tid < slutt) return true;
            if (tid >= start) return true;
        }
        else
        {
            // NO-WRAP-tilfelle (eks. kveldsvakt 15→23): vakt aktiv kun
            // innenfor [start, slutt). Hverken før start eller fra og med
            // slutt regnes som vakt — drifts-leder har simulert at det IKKE
            // er nattvakt mellom 23:00 og neste dags 15:00.
            if (tid >= start && tid < slutt) return true;
        }

        // Utenfor vakt-vinduet: ordinær arbeidstid (eller "ikke vakt").
        return false;
    }

    /// <summary>
    /// Returnerer UTC-tidspunktet for "neste arbeidsdag kl. 08:00 lokal tid"
    /// regnet fra <paramref name="utcStart"/>. Brukes som counterfactual end-tid
    /// i Vakt-ROI: hvis det ikke fantes vakt, hvor lenge ville feilen ha vart?
    ///
    /// Definisjon:
    ///   Arbeidsdag = mandag-fredag som IKKE er norsk offentlig helligdag.
    ///
    /// Hvis utcStart treffer en hverdag før kl. 08:00 lokal tid, returnerer vi
    /// SAMME dag kl. 08:00 (fordi driftspersonell møter ved arbeidsdagens
    /// start uansett). For alle andre tidspunkter går vi framover dag for dag
    /// til vi treffer neste arbeidsdag og setter klokkeslettet til 08:00.
    /// </summary>
    public DateTimeOffset NesteArbeidsdagOppstart(DateTimeOffset utcStart)
    {
        var lokal = TimeZoneInfo.ConvertTime(utcStart, _tz);
        var dato = DateOnly.FromDateTime(lokal.DateTime);
        var tid = lokal.TimeOfDay;

        // Hvis vi er på en arbeidsdag før morgen-oppmøte, returnér dagens 08:00.
        if (ErArbeidsdag(dato) && tid < _options.OppmoteTidspunkt)
        {
            return TilUtc(dato, _options.OppmoteTidspunkt);
        }

        // Ellers: gå framover til første arbeidsdag.
        var kandidat = dato.AddDays(1);
        while (!ErArbeidsdag(kandidat))
        {
            kandidat = kandidat.AddDays(1);
        }
        return TilUtc(kandidat, _options.OppmoteTidspunkt);
    }

    /// <summary>Konverterer (lokal dato, lokal tid) til UTC for norsk tidssone.</summary>
    private DateTimeOffset TilUtc(DateOnly dato, TimeSpan klokkeslett)
    {
        var lokalDt = dato.ToDateTime(TimeOnly.FromTimeSpan(klokkeslett), DateTimeKind.Unspecified);
        return new DateTimeOffset(lokalDt, _tz.GetUtcOffset(lokalDt)).ToUniversalTime();
    }

    private static bool ErArbeidsdag(DateOnly dato)
    {
        if (dato.DayOfWeek == DayOfWeek.Saturday || dato.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }
        return !NorgesHelligdager.ErHelligdag(dato);
    }
}

/// <summary>
/// Konfigurasjon for vakt-tidsmodellen. Default-verdier matcher
/// drifts-leder-spesifikasjonen fra 2026-04-28: vakt 15-07 hverdager + helg/helligdag,
/// driftspersonell møter 08:00 ved counterfactual.
/// </summary>
public sealed record VaktTidsmodellOptions(
    TimeSpan EttermiddagStart,
    TimeSpan MorgenCutoff,
    TimeSpan OppmoteTidspunkt,
    TimeSpan VaktResponstid)
{
    /// <summary>Vakt 15-07 hverdager + hele helg/helligdag, 1.0 t responstid, 08:00 oppmøte.</summary>
    public static readonly VaktTidsmodellOptions Default = new(
        EttermiddagStart: new TimeSpan(15, 0, 0),
        MorgenCutoff: new TimeSpan(7, 0, 0),
        OppmoteTidspunkt: new TimeSpan(8, 0, 0),
        VaktResponstid: TimeSpan.FromHours(1.0));
}
