namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Strategi for hvordan Vakt-ROI bestemmer "overløp" (om vannet ville rent
/// forbi turbinen uten produksjon) for et anlegg. Plant-level fordi det er
/// anleggets fysikk som avgjør: native overflow-tag, terskel mot HRV på
/// terminal-dam, eller utledning fra produksjons-historikk.
/// </summary>
public enum OverflowMode
{
    /// <summary>
    /// Native SCADA-tag med rolle <see cref="SignalRole.OverflowFlow"/> på
    /// terminal-dam. Default — fungerer for de fleste anleggene (Drivdal,
    /// Vikeså, Lindland, Øgreyfoss, Løgjen, Haukland).
    /// </summary>
    NativeTag,

    /// <summary>
    /// Utledet fra terminal-damens oppstrøms-nivå (<see cref="SignalRole.UpstreamLevel"/>):
    /// overløp regnes som aktivt når <c>level - HRV &gt; threshold</c>.
    /// Brukes for Ørsdalen som ikke har egen overflow-tag — drifts-leder
    /// fyller inn HRV på dammen + terskel (typisk 10 cm).
    /// </summary>
    LevelProxy,

    /// <summary>
    /// Utledet fra produksjonshistorikk (<see cref="SignalRole.GeneratorActivePower"/>):
    /// overløps-timer = timer der GeneratorActivePower &gt; 0. Logikken er at
    /// hvis anlegget produserer akkurat før en alarm, så ville produksjon
    /// pågått i counterfactual-vinduet hvis vakten ikke hadde restartet.
    /// Brukes for Stølskraft (drikkevannskraftverk uten magasin-telemetri).
    /// </summary>
    ProductionStateProxy,
}
