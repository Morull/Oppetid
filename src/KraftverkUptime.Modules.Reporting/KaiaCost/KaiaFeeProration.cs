namespace KraftverkUptime.Modules.Reporting.KaiaCost;

/// <summary>
/// Ren, statisk pro-rata-beregning av KAIAs faste årsavgift på en rapportperiode.
/// Skilt fra <c>KaiaCostQueryService</c> slik at den kan unit-testes uten EF,
/// DI eller mockede repositories — den er kjernen i forretningslogikken.
///
/// Formel:
/// <code>
///   fastAvgift = annualFee × (dager_i_periode / dager_i_året)
///   dager_i_periode = round((end - start).TotalDays)   // DST-trygg
///   dager_i_året     = 366 hvis skuddår basert på start.Year, ellers 365
/// </code>
/// Tolv hele månedseksporter summerer da til nøyaktig <c>annualFee</c>;
/// en delvis måned (f.eks. mai 01.–17.) gir 17/365 av årsavgiften.
/// </summary>
public static class KaiaFeeProration
{
    /// <summary>
    /// Beregn pro-rata-andelen av <paramref name="annualFee"/> for perioden
    /// [<paramref name="periodStartUtc"/>, <paramref name="periodEndUtc"/>].
    /// Returnerer 0 for tom eller invertert periode. Negativ annualFee
    /// passerer urørt (gir negativ kostnad — kalleren bestemmer fortolkning).
    /// </summary>
    public static double Compute(
        double annualFee,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc)
    {
        if (periodEndUtc <= periodStartUtc)
        {
            return 0;
        }

        // Math.Round((end - start).TotalDays) håndterer DST-overgangene i
        // mars/oktober: en full januar gir 31, en full mars gir 31 (selv om
        // TotalDays er 30.958 fordi 27.03 har 23 timer), en full oktober gir
        // 31 (TotalDays 31.042 fordi 30.10 har 25 timer). MidpointRounding
        // til ToEven (.NET default) er trygt fordi forskjellene mot heltall
        // er 0.04–0.05.
        var daysInPeriod = Math.Round((periodEndUtc - periodStartUtc).TotalDays);
        if (daysInPeriod <= 0)
        {
            return 0;
        }

        var daysInYear = DateTime.IsLeapYear(periodStartUtc.Year) ? 366 : 365;
        return annualFee * daysInPeriod / daysInYear;
    }
}
