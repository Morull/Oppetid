namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Leverandør av SCADA/historian-data (f.eks. OPC UA, PI, Ignition).
/// Ikke implementert i v1 – krever eieravtale og on-prem-konnektor.
/// Kontrakten utvides når modul KraftverkUptime.Modules.Scada bygges.
/// </summary>
public interface IScadaDataSource : IDataSource
{
    /* Stub */
}
