namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Felles grensesnitt for alle eksterne datakilder som plattformen konsumerer.
/// Implementasjoner skal være idempotente ved gjentatt innlasting av samme kilde.
/// </summary>
public interface IDataSource
{
    /// <summary>
    /// Kort navn som identifiserer kilden i logger og diagnostikk
    /// (f.eks. "settlement", "scada", "sildre").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Skjema-/kontraktsversjon som denne implementasjonen håndterer.
    /// Inkrementeres ved brytende endringer i eksport-format.
    /// </summary>
    string Version { get; }
}
