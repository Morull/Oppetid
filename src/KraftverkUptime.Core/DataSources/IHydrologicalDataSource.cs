namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Leverandør av hydrologiske data (NVE Sildre, egne sensorer, værtjenester).
/// Brukes for å klassifisere ResourceUnavailable-timer og for hydro-spesifikke KPI-er
/// (HydroResourceAvailability, EnvironmentalFlowCompliance, SpillLoss).
/// Ikke implementert i v1 – aktiveres på Nivå 1 i klassifiseringsmodenhetsstigen.
/// </summary>
public interface IHydrologicalDataSource : IDataSource
{
    /* Stub */
}
