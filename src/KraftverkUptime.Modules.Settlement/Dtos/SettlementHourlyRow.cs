using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Settlement.Dtos;

/// <summary>
/// Én time i en verk-fane. Alle numeriske verdier er nullable – null betyr
/// "ikke målt" eller "kolonne fantes ikke i denne eksportversjonen". Det er
/// en funksjonell forskjell mot 0, og skal aldri konverteres stille.
///
/// TimeUtc er kanonisk tid; TimeLocal beholdes for UI og revisjonsspor.
/// DqState settes av DataQualityReportBuilder – er aldri null i resultatet
/// fra en fullført import.
/// </summary>
public sealed record SettlementHourlyRow
{
    public required DateTimeOffset TimeUtc { get; init; }
    public required DateTimeOffset TimeLocal { get; init; }

    // Energi
    public double? MwhElhub { get; init; }
    public double? MwhESett { get; init; }

    // Marked
    public double? SpotbudMwh { get; init; }
    public double? SpotprisNokMwh { get; init; }
    public double? SpotomsetningNok { get; init; }

    // Ubalanse og regulerkraft
    public double? UbalanseMwh { get; init; }
    public double? RkPrisNokMwh { get; init; }
    public double? RkKjopNok { get; init; }
    public double? RkSalgNok { get; init; }

    // Gebyrer og oppgjør
    public double? NordPoolGebyrNok { get; init; }
    public double? ESettVolumgebyrNok { get; init; }
    public double? ESettUbalansegebyrNok { get; init; }
    public double? SumSalgNok { get; init; }
    public double? MeglerprovisjonNok { get; init; }
    public double? OppgjorNok { get; init; }

    // Utvidede kolonner (nyere eksportformat)
    public double? BruttoOmsetningNok { get; init; }
    public double? ProduksjonplanMwh { get; init; }
    public double? EffektavlesningerMw { get; init; }
    public double? AbsUbalansevolumMwh { get; init; }
    public double? UbalanseResultatNok { get; init; }

    public DataQualityState DqState { get; init; } = DataQualityState.Good;
}
