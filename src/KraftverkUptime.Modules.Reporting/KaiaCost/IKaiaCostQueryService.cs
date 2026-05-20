namespace KraftverkUptime.Modules.Reporting.KaiaCost;

/// <summary>
/// Beregner og henter KAIA-kostnad per rapportperiode. Kostnaden består av
/// to komponenter:
///   1. Meglerprovisjon — hentet fra <c>SettlementImportRecord.MeglerprovisjonNok</c>
///      (lagret negativ i kilden, snus til positiv kostnad her).
///   2. Fast årsavgift pro-rata — <c>PlantRegistration.KaiaAnnualFeeNok</c>
///      ganget med (dager i periode / dager i året).
///
/// Spec: <c>docs/SPEC-KAIA-KOSTNAD.md</c>. Implementasjon i
/// <c>Infrastructure/Reporting/KaiaCostQueryService.cs</c>.
/// </summary>
public interface IKaiaCostQueryService
{
    /// <summary>
    /// KAIA-kostnad for én import (= ett anlegg, én periode). Identifiseres
    /// ved (<paramref name="plantId"/>, <paramref name="idempotencyKey"/>) for
    /// å støtte reimport-historikk uten å treffe gammel data.
    /// </summary>
    Task<KaiaCostResult?> GetForImportAsync(
        string plantId, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// KAIA-kostnad for alle anlegg i porteføljen, for importer som dekker
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>]. Bruker
    /// "nyeste import som dekker perioden"-semantikken (samme som
    /// <c>FindLatestCoveringAsync</c>) slik at reimport ikke dobbelteller.
    /// </summary>
    Task<IReadOnlyList<KaiaCostResult>> GetForPortfolioAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <summary>
/// KAIA-kostnad for én rapportperiode for ett anlegg. Alle kronebeløp er
/// positive (kostnader). Null på <see cref="MeglerprovisjonNok"/> og
/// <see cref="TotalNok"/> betyr "ukjent" (Summering-fanen manglet i
/// eksporten); 0 betyr "ingen handler i perioden". De to er funksjonelt
/// forskjellige og skal vises ulikt i UI.
/// </summary>
public sealed record KaiaCostResult
{
    public required string PlantId { get; init; }
    public required string PlantName { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }

    /// <summary>Meglerprovisjon som positiv kostnad. Null hvis ukjent.</summary>
    public double? MeglerprovisjonNok { get; init; }

    /// <summary>Fast årsavgift pro-rata periodens lengde.</summary>
    public double FastAvgiftNok { get; init; }

    /// <summary>MeglerprovisjonNok + FastAvgiftNok. Null hvis meglerprovisjon ukjent.</summary>
    public double? TotalNok { get; init; }

    /// <summary>Datakvalitets-merknad, f.eks. "Meglerprovisjon mangler i eksport".</summary>
    public string? Note { get; init; }
}
