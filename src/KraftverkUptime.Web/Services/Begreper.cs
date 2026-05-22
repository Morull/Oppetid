namespace KraftverkUptime.Web.Services;

/// <summary>
/// Felles ordliste for forkortelser og fag-begreper brukt i UI-en. Spec
/// FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING.md Del 5.
///
/// Én kilde for all forklaringstekst — brukes av header-tooltips på
/// kolonner, kort-popup på tabeller/grafer, og dialog-undertekster.
/// Hvis vi endrer formuleringen av f.eks. "Capture rate", gjør vi det ÉN
/// gang her og det propagerer overalt.
///
/// Konvensjon: nøklene er enten det synlige symbolet ("η", "Δη", "MW",
/// "kW") eller en stabil kortform ("CapturePris", "PlanTreff"). Aldri
/// avhengig av oversettelse eller lokalisering — det er nøkler, ikke
/// presentasjons-tekst.
/// </summary>
public static class Begreper
{
    private static readonly Dictionary<string, string> _ordliste = new(StringComparer.Ordinal)
    {
        // ----- Fysiske enheter -----
        ["η"] = "Virkningsgrad — andel av tilgjengelig energi som blir til strøm. Måles i prosent.",
        ["Δη"] = "Avvik i virkningsgrad: faktisk η minus forventet η for effekt-bin'en. Negativ verdi = underytelse mot baseline.",
        ["P"] = "Effekt målt i kW. η(P)-kurve = virkningsgrad som funksjon av effekt.",
        ["kW"] = "Kilowatt — øyeblikks-effekt.",
        ["kWh"] = "Kilowattime — energi (effekt × tid).",
        ["MW"] = "Megawatt — 1000 kW.",
        ["MWh"] = "Megawattime — 1000 kWh.",
        ["GWh"] = "Gigawattime — 1 000 000 kWh. Normalt brukt for årsproduksjon.",
        ["m³/s"] = "Kubikkmeter vann per sekund — vannføring gjennom turbinen.",
        ["m³ per kWh"] = "Spesifikt vannforbruk — vannmengde brukt per produsert kWh. Lavere = bedre.",
        ["NOK"] = "Norske kroner.",
        ["NOK/MWh"] = "Norske kroner per megawattime — prisenhet i kraftmarkedet.",
        ["NOK/år"] = "Norske kroner per år — typisk brukt for årsavgifter (eks. KAIA).",
        ["t"] = "Timer.",

        // ----- Episode- og effektivitets-begreper -----
        ["Bin"] = "Effekt-intervall (200 kW bredt) som målepunkter grupperes i. Brukes for baseline-sammenligning.",
        ["EffektBin"] = "Effekt-intervall (200 kW bredt) som målepunkter grupperes i. Brukes for baseline-sammenligning.",
        ["Baseline"] = "Anleggets snitt-η i samme effekt-bin. Sammenlignings-grunnlag for å oppdage operative avvik.",
        ["SweetSpot"] = "Anleggets toppunkt i η(P)-kurven — effekten der virkningsgraden er høyest.",
        ["NormalDrift"] = "Produksjons-intervall der η ≥ 50 % (Genuine). Inngår i alle snitt-aggregat.",
        ["StartStopp"] = "Ramp-intervall der P er over terskel men η under gulvet. Synlig i scatter men teller IKKE i snitt-aggregat.",
        ["TaptEnergi"] = "Energi vi kunne hatt om virkningsgraden var på baseline: faktisk × (baseline − faktisk) / faktisk.",
        ["TaptVerdi"] = "Tapt energi × spotpris (NOK/MWh) for samme klokketime.",

        // ----- KPI-er fra rapport-katalogen -----
        ["AF"] = "Availability Factor — tilgjengelighetsfaktor. Andel av tiden anlegget var driftsklart.",
        ["AvailabilityFactor_AF"] = "Availability Factor — tilgjengelighetsfaktor. Andel av tiden anlegget var driftsklart.",
        ["FOR"] = "Forced Outage Rate — tvungen utfallsrate. Andel av tiden ute pga. uplanlagt feil.",
        ["ForcedOutageRate_FOR"] = "Forced Outage Rate — tvungen utfallsrate. Andel av tiden ute pga. uplanlagt feil.",
        ["ServiceHours_SH"] = "Drifts-timer (Service Hours) — antall timer i drift i perioden.",
        ["ForcedOutageHours_FOH"] = "Nedetid-timer (Forced Outage Hours) — antall timer ute pga. uplanlagt feil.",
        ["TotalProduction_MWh"] = "Total produksjon i perioden, summert fra Elhub-måleverdier.",
        ["Spotomsetning_NOK"] = "Brutto-inntekt fra spotmarkedet (day-ahead) i perioden.",
        ["Ubalansekost_NOK"] = "Total kostnad ved å avvike fra Spotbud i ubalanse-oppgjøret.",
        ["Oppgjor_NOK"] = "Sum oppgjør i perioden — netto inn-/utbetaling fra megler.",
        ["BidDelivery"] = "Bud-leveranse — Σ Elhub / Σ Spotbud. Hvor godt vi leverte det vi budga.",
        ["Confidence"] = "Datakonfidens — kvalitetsmål på de underliggende målingene (Elhub + ubalanse).",
        ["KPI"] = "Key Performance Indicator — nøkkeltall.",
        ["UTC"] = "Coordinated Universal Time — tidsstempler i rapporten er i UTC, ikke norsk tid.",

        // ----- Capture rate -----
        ["CR"] = "Capture rate — forholdet mellom oppnådd snittpris og referansepris (baseline) for perioden.",
        ["TimesCr"] = "Capture rate mot times-baseline — sammenligner mot times-spotpris.",
        ["DagCr"] = "Capture rate mot dag-baseline — sammenligner mot dagens snitt-spotpris.",
        ["RaCr"] = "Rå capture rate — før filtrering eller justering.",
        ["CapturePris"] = "Capture-pris — anleggets oppnådde snittpris i NOK/MWh.",
        ["Merverdi"] = "Merverdi — NOK tjent ut over baseline-prisen.",
        ["TimesBaseline"] = "Times-baseline — referansepris (vektet times-spotpris) capture rate måles mot.",
        ["DagBaseline"] = "Dag-baseline — referansepris (dagens snitt-spotpris) capture rate måles mot.",

        // ----- Plan og produksjon -----
        ["Hydrogrid"] = "Leverandør av produksjonsplan — angir plan-MWh per time som anlegget skal produsere etter.",
        ["PlanTreff"] = "Plan-treff — hvor godt faktisk produksjon traff Hydrogrid-planen. 1 − MAE/Σplan.",
        ["KapUtn"] = "Kapasitetsutnyttelse — faktisk produksjon delt på maks mulig i perioden.",
        ["Elhub"] = "Norsk datahub for måleverdier. «MwhElhub» = faktisk levert energi.",
        ["Spotbud"] = "Spotbud — budgitt volum mot spotmarkedet (day-ahead).",
        ["HydrogridMerverdi"] = "Hydrogrid timing-merverdi — NOK tjent på å følge plan-timing vs. en passiv strategi.",

        // ----- Drift og data -----
        ["SCADA"] = "Supervisory Control And Data Acquisition — drifts-overvåkings-systemet (sanntidssignaler fra anlegget).",
        ["KAIA"] = "Megler-systemet importfilene kommer fra — KAIA Energy. Leverer settlement-avregninger og fast årsavgift.",
        ["TvangsDerating"] = "Tvungen reduksjon av effekt under maks pga. ekstern begrensning (vann, nett, plan).",
        ["Drift"] = "Anlegget produserer over null-terskel.",
        ["Stille"] = "Anlegget er ikke i drift, men ikke på grunn av feil — ofte planlagt stopp eller vannmangel.",
        ["Nedetid"] = "Anlegget skulle produsert men gjør det ikke — typisk uplanlagt feil.",

        // ----- Import / dekningsmatrise -----
        // De spesifikke A/T/S/S-tooltips for status-matrisen ligger inline der —
        // ordlisten her er fallback for utvidet bruk.
        ["A"] = "Avregning — KAIA-eksport mottatt og parset.",
        ["T"] = "Tilsig — målinger av tilsig (vannmengde inn) for perioden.",
        ["S"] = "SCADA — drifts-signaler tilgjengelig.",

        // ----- Begreper i kategorier -----
        ["Mapping"] = "Kobling — antall årsakskoder fra operlog som mapper til denne nedetid-kategorien.",
        ["AarsaksKode"] = "Årsakskode — den rå koden fra operlog som beskriver hvorfor nedetiden oppsto.",
    };

    /// <summary>
    /// Slår opp forklaring for en nøkkel. Returnerer null hvis nøkkelen ikke
    /// finnes — call-sites kan da fallbacke til en lokal tekst eller bare
    /// hoppe over tooltip-en.
    /// </summary>
    public static string? Forklar(string noekkel)
        => _ordliste.TryGetValue(noekkel, out var v) ? v : null;

    /// <summary>
    /// Bygger en sammenkjedet "ordliste" for popup — én linje per nøkkel.
    /// Ukjente nøkler hoppes over stille. Brukes typisk i kort-popup som
    /// lister flere forkortelser samtidig.
    /// </summary>
    public static string Ordliste(params string[] noekler)
    {
        var lines = noekler
            .Select(k => Forklar(k) is { } v ? $"{k}: {v}" : null)
            .Where(s => s is not null);
        return string.Join("\n", lines);
    }
}
