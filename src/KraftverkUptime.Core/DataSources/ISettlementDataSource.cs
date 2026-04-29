namespace KraftverkUptime.Core.DataSources;

/// <summary>
/// Markørgrensesnitt for leverandør av oppgjørsdata fra KAIA-portalen.
/// KAIA aggregerer underliggende data fra Elhub (målt produksjon),
/// eSett (ubalanse-oppgjør) og Nord Pool (spotpris). Kolonnenavnene i
/// eksporten beholder Elhub/eSett-terminologi siden det er KAIA sin
/// valgte navngiving — derfor heter <c>MwhElhub</c>/<c>MwhESett</c>
/// fortsatt det selv om datakilden er KAIA.
///
/// Konkret signatur og DTO-er defineres i KraftverkUptime.Modules.Settlement.
/// Grunnen til at dette står i Core er slik at annen kode (analyzer, reporter)
/// kan motta en ISettlementDataSource via DI uten å referere modul-assembly.
/// </summary>
public interface ISettlementDataSource : IDataSource
{
    /* Signatur spesifiseres i Prompt 2 / modulprosjektet */
}
