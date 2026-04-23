namespace KraftverkUptime.Core.Domain;

/// <summary>
/// IEEE 762 / NERC GADS-tilpasset enhetstilstand for ett tidsrom.
/// Hver time per aggregat tilordnes nøyaktig én UnitState.
/// Tellere for KPI-beregninger er dokumentert i kommentarene.
/// </summary>
public enum UnitState
{
    /// <summary>IS – normal produksjon. Teller mot Service Hours (SH).</summary>
    InService,

    /// <summary>RS – tilgjengelig, men markedsstyrt stopp (lav spotpris, vannverdi). Teller mot Available Hours (AH).</summary>
    ReserveShutdown,

    /// <summary>PO – planlagt revisjon (&gt; 4 uker varsel). Teller mot Unavailable Hours (UH).</summary>
    PlannedOutage,

    /// <summary>MO – vedlikehold (&lt; 4 uker varsel). Teller mot Unavailable Hours (UH).</summary>
    MaintenanceOutage,

    /// <summary>FO – trip / uvarslet stopp. Teller mot Forced Outage Hours (FOH).</summary>
    ForcedOutage,

    /// <summary>D1–D4 – redusert effekt pga. feil. Teller mot Equivalent Forced Derated Hours (EFDH).</summary>
    ForcedDerating,

    /// <summary>PD – redusert effekt pga. vedlikehold. Teller mot Equivalent Planned Derated Hours (EPDH).</summary>
    PlannedDerating,

    /// <summary>RU – ressursbegrenset (vannmangel, islegging, minstevassføring). Outside Management Control (OMC).</summary>
    ResourceUnavailable,

    /// <summary>IU – manglende data. Egen kategori; skal ALDRI tolkes som nedetid.</summary>
    InformationUnavailable
}
