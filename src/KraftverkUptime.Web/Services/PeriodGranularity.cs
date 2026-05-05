namespace KraftverkUptime.Web.Services;

/// <summary>
/// Granularitet for global periode-velger i AppBar. Brukt av
/// <see cref="FilterState"/> + <see cref="PeriodCalculator"/> til å avgjøre
/// hvor mye ←/→-pilene flytter perioden.
///
/// <see cref="Egendefinert"/> betyr at brukeren har valgt en spesifikk
/// dato-range som ikke matcher noen av de andre presetene; arrows
/// deaktiveres da.
/// </summary>
public enum PeriodGranularity
{
    Maned,
    Kvartal,
    Ar,
    HittilIAr,
    Egendefinert
}
