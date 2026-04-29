namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Slår opp hvilke timer i en periode som hadde overløp i magasinet for et
/// gitt anlegg. Brukes av Vakt-ROI-beregningen til å skille mellom
/// "vannet rant forbi turbinen" (reddbar produksjon) og "vannet er trygt
/// magasinert" (ingen ROI siden vannet kan brukes senere).
/// </summary>
public interface IOverflowQueryService
{
    /// <summary>
    /// Henter settet av overløps-timer + om SCADA-data faktisk dekker perioden.
    /// <see cref="OverflowDataset.DataAvailable"/> skiller "ingen overløp"
    /// (vannet trygt magasinert) fra "vi vet ikke" (mangler data) — viktig
    /// for ROI-flagget <c>OverflowDataMissing</c>.
    /// </summary>
    Task<OverflowDataset> GetOverflowDatasetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>True hvis plantet har en konfigurert <c>OverflowFlow</c>-tag.</summary>
    Task<bool> HasOverflowTagAsync(string plantId, CancellationToken ct);
}

/// <summary>
/// Resultatet fra <see cref="IOverflowQueryService.GetOverflowDatasetAsync"/>.
/// </summary>
/// <param name="OverflowHours">Timer (UTC, time-presisjon) der overløp er registrert over støy-terskelen.</param>
/// <param name="DataAvailable">
/// True hvis OverflowFlow-tagen finnes OG minst ett sample er importert
/// i den forespurte perioden. False = vi har ikke datagrunnlag for å si om
/// det var overløp; ROI behandles konservativt = 0 og flagges som data missing.
/// </param>
public sealed record OverflowDataset(
    IReadOnlySet<DateTimeOffset> OverflowHours,
    bool DataAvailable);
