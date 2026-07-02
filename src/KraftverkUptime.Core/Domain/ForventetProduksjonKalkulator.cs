namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Beregner forventet produksjon for en periode gitt normalårsproduksjon og en
/// valgfri månedsprofil (SPEC-MAANEDSPROFIL-NORMALAAR, 2026-07-02).
///
/// Vannkraft er sterkt sesongavhengig — flat pro-rata (<c>normalGwh × 1000 ×
/// dager / dager_i_året</c>) overestimerer sommerforventningen og underestimerer
/// vinteren. Med månedsprofil (12 prosentverdier, sum 100, indeks 0 = januar)
/// vektes forventningen etter verkets faktiske produksjonsfordeling;
/// delmåneder vektes dagbasert innenfor måneden.
///
/// Delt mellom Web (client-side KPI-er i Produksjon/Portefølje) og fremtidige
/// API-tjenester slik at tallene er identiske overalt — tidligere lå flat-
/// beregningen duplisert i to razor-filer.
/// </summary>
public static class ForventetProduksjonKalkulator
{
    /// <summary>Antall verdier en gyldig månedsprofil må ha.</summary>
    public const int ProfilLengde = 12;

    /// <summary>Toleranse for profilsum-validering: 100 ± denne.</summary>
    public const double SumToleranse = 0.1;

    /// <summary>
    /// Forventet produksjon i MWh for perioden [<paramref name="from"/>,
    /// <paramref name="to"/>) gitt normalår (GWh) og månedsprofil.
    /// <paramref name="profil"/> = null eller ugyldig lengde → flat fordeling
    /// (dagens pro-rata-oppførsel, inkl. skuddår-håndtering).
    /// Returnerer null hvis normalår mangler/≤ 0 eller perioden er tom.
    /// Perioder som krysser årsskiftet støttes (profilen gjentas per år).
    /// </summary>
    public static double? ForventetMwh(
        double? normalGwh, double[]? profil, DateTime from, DateTime to)
    {
        if (normalGwh is not { } gwh || gwh <= 0) return null;
        if (to <= from) return null;

        // Uten (gyldig) profil: behold flat dagbasert pro-rata slik at tall er
        // sammenlignbare med dagens visninger for verk uten profil.
        if (profil is null || profil.Length != ProfilLengde)
        {
            var dager = (to - from).TotalDays;
            var dagerIAaret = DateTime.IsLeapYear(from.Year) ? 366 : 365;
            return gwh * 1000.0 * dager / dagerIAaret;
        }

        // Månedsvektet: iterér kalendermånedene perioden berører. For hver
        // måned m i år y: forventet += normalMwh × (profil[m−1]/100) ×
        // dekkedeDager / dagerIMåneden. Dag-brøker (klokkeslett) telles med i
        // TotalDays slik at delmåneder vektes kontinuerlig.
        var normalMwh = gwh * 1000.0;
        double forventet = 0;

        // Start på månedsgrensa før/på from; hopp måned for måned til to.
        var maanedStart = new DateTime(from.Year, from.Month, 1, 0, 0, 0, from.Kind);
        while (maanedStart < to)
        {
            var nesteMaaned = maanedStart.AddMonths(1);
            var overlappStart = from > maanedStart ? from : maanedStart;
            var overlappSlutt = to < nesteMaaned ? to : nesteMaaned;
            if (overlappSlutt > overlappStart)
            {
                var dekkedeDager = (overlappSlutt - overlappStart).TotalDays;
                var dagerIMaaned = DateTime.DaysInMonth(maanedStart.Year, maanedStart.Month);
                forventet += normalMwh
                    * (profil[maanedStart.Month - 1] / 100.0)
                    * dekkedeDager / dagerIMaaned;
            }
            maanedStart = nesteMaaned;
        }

        return forventet;
    }

    /// <summary>
    /// Validerer en månedsprofil: nøyaktig 12 verdier, alle ≥ 0, sum 100 ± 0,1.
    /// null regnes som gyldig («ikke satt» → flat fallback). Returnerer feil-
    /// beskrivelse på norsk, eller null når profilen er gyldig — brukes av
    /// API-valideringen (400) og PlantAdmin-skjemaet.
    /// </summary>
    public static string? ValiderProfil(double[]? profil)
    {
        if (profil is null) return null;
        if (profil.Length != ProfilLengde)
        {
            return $"Månedsprofilen må ha nøyaktig {ProfilLengde} verdier (fikk {profil.Length}).";
        }
        if (profil.Any(v => v < 0 || double.IsNaN(v) || double.IsInfinity(v)))
        {
            return "Månedsprofilen kan ikke inneholde negative eller ugyldige verdier.";
        }
        var sum = profil.Sum();
        if (Math.Abs(sum - 100.0) > SumToleranse)
        {
            return $"Månedsprofilen må summere til 100 % (± {SumToleranse:0.0}); summen er {sum:0.0} %.";
        }
        return null;
    }
}
