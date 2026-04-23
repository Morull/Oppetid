namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Markørgrensesnitt for leverandør av oppgjørsdata fra Elhub/eSett-portaleksport.
/// Konkret signatur og DTO-er defineres i KraftverkUptime.Modules.Settlement.
/// Grunnen til at dette står i Core er slik at annen kode (analyzer, reporter)
/// kan motta en ISettlementDataSource via DI uten å referere modul-assembly.
/// </summary>
public interface ISettlementDataSource : IDataSource
{
    /* Signatur spesifiseres i Prompt 2 / modulprosjektet */
}
