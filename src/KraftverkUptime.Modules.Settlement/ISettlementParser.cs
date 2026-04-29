using KraftverkUptime.Core.DataSources;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Settlement;

/// <summary>
/// Konkret kontrakt for modul-interne konsumenter av settlement-parser.
/// Utvider Core-markøren <see cref="ISettlementDataSource"/>. Grunnen til at den
/// konkrete signaturen lever her og ikke i Core er at <see cref="ParsedSettlement"/>
/// er modul-lokal; Core skal forbli fri for domene-DTO-er.
/// </summary>
public interface ISettlementParser : ISettlementDataSource
{
    /// <summary>
    /// Parser en portaleksport-strøm. Kaller eier strømmen og er ansvarlig for
    /// å disponere den. Metoden leser hele strømmen før den returnerer.
    ///
    /// For multi-anleggsfiler returneres første anlegg fra workbooket. Bruk
    /// <see cref="ParseAllAsync"/> for å hente alle.
    /// </summary>
    Task<ParsedSettlement> ParseAsync(Stream content, CancellationToken ct = default);

    /// <summary>
    /// Parser én eller flere anleggs-faner fra en .xlsx. Returnerer ett resultat
    /// per anlegg som ble funnet. For multi-plant-filer (med "Summering"-fane
    /// + flere "<c>n Navn</c>"-faner) returneres ett <see cref="ParsedSettlement"/>
    /// per anleggsfane med <see cref="ParsedSettlement.PlantId"/> satt fra
    /// kanonisk navn i R1. For enkelt-plant-filer returneres en liste med ett
    /// element (PlantId = null — caller bruker plantId fra URL).
    /// </summary>
    Task<IReadOnlyList<ParsedSettlement>> ParseAllAsync(
        Stream content, CancellationToken ct = default);
}
