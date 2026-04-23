namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Leverandør av vedlikeholdsdata fra CMMS-systemer (IFS, Maximo, SAP PM).
/// Brukes til å berike klassifisering med faktiske arbeidsordrer, ikke proxy-heuristikk.
/// Ikke implementert i v1 – aktiveres på Nivå 4 i klassifiseringsmodenhetsstigen.
/// </summary>
public interface ICmmsDataSource : IDataSource
{
    /* Stub */
}
