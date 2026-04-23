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
    /// </summary>
    Task<ParsedSettlement> ParseAsync(Stream content, CancellationToken ct = default);
}
