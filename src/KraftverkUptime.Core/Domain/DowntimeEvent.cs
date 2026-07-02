namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Aggregert nedetids-hendelse: én sammenhengende periode der anlegget var
/// klassifisert som nedetid (FO, MO, PO, RU, derating). Bygges fra
/// klassifiserte timer (settlement) og kan berikes med sub-time presisjon
/// fra operlog (<see cref="ClassifiedEvent"/>).
///
/// Skiller seg fra <see cref="ClassifiedEvent"/> ved at:
///   – ClassifiedEvent er én rad fra én kilde (operlog-event eller
///     klassifikator-overgang) — kan ha ukjent EndUtc og bærer ikke tap.
///   – DowntimeEvent er en presentasjons-orientert aggregering med varighet,
///     tapsestimater og kategori. Brukes til /nedetid og /vakt-roi.
/// </summary>
public sealed record DowntimeEvent
{
    public required string PlantId { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// Starten slik SCADA-klassifiseringen DETEKTERTE den — før en eventuell
    /// manuell start-korreksjon (SPEC-NEDETID-STARTTID-OVERRIDE). Null = ingen
    /// korreksjon er anvendt (StartUtc ER detektert start). Override-rader i
    /// <c>core.vakt_event_overrides</c> nøkles på detektert start (stabil
    /// nøkkel), mens all tids-matematikk (vakt-vindu, counterfactual, tap)
    /// bruker den effektive <see cref="StartUtc"/>. Bruk
    /// <see cref="EffektivDetectedStartUtc"/> for override-oppslag.
    /// </summary>
    public DateTimeOffset? DetectedStartUtc { get; init; }

    /// <summary>Detektert start for override-oppslag: eksplisitt satt, ellers StartUtc.</summary>
    public DateTimeOffset EffektivDetectedStartUtc => DetectedStartUtc ?? StartUtc;

    /// <summary>Varighet i timer, beregnet fra Start/End. Helt tall hvis events kommer fra time-aggregering.</summary>
    public double VarighetTimer => (EndUtc - StartUtc).TotalHours;

    /// <summary>Den dominerende UnitState i perioden (typisk fra første time eller mest hyppige).</summary>
    public required UnitState State { get; init; }

    /// <summary>
    /// Funksjonell kategori for grupperinger (donut-diagram, KPI-er, vakt-ROI-filter).
    /// Settes i analyzer basert på state + cause-code.
    /// </summary>
    public required DowntimeEventCategory Category { get; init; }

    /// <summary>CauseCode fra dominerende time, eller fra operlog-overlay hvis tilgjengelig.</summary>
    public string? CauseCode { get; init; }

    /// <summary>Estimert produksjonstap i MWh (sum av plan/spotbud over nedetidstimene).</summary>
    public required double TapMwh { get; init; }

    /// <summary>Estimert økonomisk tap i NOK (sum av tap_mwh × spotpris over nedetidstimene).</summary>
    public required double TapNok { get; init; }

    /// <summary>Antall timer i denne hendelsen som ble tatt fra settlement-data.</summary>
    public int TimerSettlement { get; init; }

    /// <summary>True hvis et eller flere operlog-events overlapper med eventet (gir bedre tids-presisjon).</summary>
    public bool HarOperlogMatch { get; init; }

    /// <summary>Fritekst-rasjonale fra dominerende klassifikasjons-rad (best-effort).</summary>
    public string? Rationale { get; init; }
}

/// <summary>
/// Funksjonell kategorisering brukt i UI og Vakt-ROI-filter.
/// Mer brukervennlig enn rå <see cref="UnitState"/> fordi den slår sammen
/// FO + ukjent-trip i samme bøtte og skiller eksplisitt eksterne
/// forstyrrelser fra anlegget-egne feil.
/// </summary>
public enum DowntimeEventCategory
{
    /// <summary>Trip / feil-stopp som krever manuell intervensjon. Reddbar av vakt.</summary>
    TripFeil,

    /// <summary>
    /// Trip forårsaket av tett inntaksrist (løvfall, is, fremmedlegemer).
    /// Skiller seg fra TripFeil ved at årsaken er vedlikeholds-relatert
    /// istedenfor mekanisk/elektrisk turbin-feil. Reddbar av vakt (rens av rist).
    /// Detekteres automatisk når en rist-falltap-alarm er aktiv ±60 min rundt
    /// trip-tidspunktet.
    /// </summary>
    TettInntaksrist,

    /// <summary>Eksterne forstyrrelser (nett-fall, frekvens-avvik). Reddbar i v1 (konservativt).</summary>
    EksternForstyrrelse,

    /// <summary>Planlagt vedlikehold eller revisjon. Ikke reddbar — vakten ville ikke akselerert.</summary>
    PlanlagtVedlikehold,

    /// <summary>Markedsstyrt stopp (lav vannverdi / spotpris). Ikke nedetid i vanlig forstand.</summary>
    Markedstopp,

    /// <summary>Ressursbegrensning (vannmangel, islegging). Outside Management Control.</summary>
    Ressursmangel,

    /// <summary>Datakvalitet-problem (manglende Elhub, negativ avlesning). Ikke nedetid.</summary>
    DataMangel,

    /// <summary>Ukjent/ikke-klassifisert. Konservativt: regnes som potensielt reddbar.</summary>
    UkjentNedetid,
}
