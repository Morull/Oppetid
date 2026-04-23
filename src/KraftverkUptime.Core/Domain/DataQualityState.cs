namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Førsteklasses-type for datakvalitetstilstand. Manglende data skal aldri
/// stille imputeres eller forveksles med nedetid – dette håndheves ved
/// å tvinge forfatteren til å velge en DataQualityState eksplisitt.
/// </summary>
public enum DataQualityState
{
    /// <summary>Verdi målt og uten mistanke om feil.</summary>
    Good,

    /// <summary>Verdi tilgjengelig, men med lavere tillit (f.eks. avvik mellom parallelle kilder innenfor terskel).</summary>
    Uncertain,

    /// <summary>Verdi er beregnet eller interpolert; ikke direkte målt.</summary>
    Substituted,

    /// <summary>Ingen måling tilgjengelig – skal aldri tolkes som 0 eller som nedetid.</summary>
    InformationUnavailable,

    /// <summary>Verdi mottatt, men utenfor akseptabelt område. Krever manuell behandling.</summary>
    Quarantined,

    /// <summary>Verdi avvist og kastet etter skjema- eller plausibilitetskontroll.</summary>
    Rejected
}
