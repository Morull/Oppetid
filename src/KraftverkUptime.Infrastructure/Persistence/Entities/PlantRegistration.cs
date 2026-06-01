using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// Anleggsregistrering. Minimal i v1 – utvides med enheter, settlement-mapping osv. i Prompt 2.
/// </summary>
public sealed class PlantRegistration : IOwnedEntity, ISoftDeletable
{
    public string Id { get; set; } = string.Empty;     // plantId
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId => Id;
    public string Name { get; set; } = string.Empty;
    public PlantType Type { get; set; }
    public double InstalledCapacityMw { get; set; }
    public string TimeZone { get; set; } = "Europe/Oslo";

    /// <summary>
    /// Day-ahead prisområde (NO1–NO5). Brukes til å plukke ut riktig spotpris-
    /// baseline for capture rate og ubalanse-beregninger. Default NO2 dekker
    /// hele Dalane Kraft-porteføljen (Sokndal/Egersund-området).
    /// </summary>
    public string PriceArea { get; set; } = "NO2";

    /// <summary>
    /// Strategi for hvordan Vakt-ROI bestemmer overløp i counterfactual-vinduet.
    /// Default <see cref="Core.Domain.OverflowMode.NativeTag"/> = bruk SCADA
    /// overflow-tag. <see cref="Core.Domain.OverflowMode.LevelProxy"/> = utled
    /// fra terminal-damens nivå vs HRV. <see cref="Core.Domain.OverflowMode.ProductionStateProxy"/> =
    /// utled fra produksjonshistorikk (for drikkevannskraftverk uten magasin).
    /// </summary>
    public OverflowMode OverflowMode { get; set; } = OverflowMode.NativeTag;

    /// <summary>
    /// KAIAs faste årsavgift for forvaltning av dette anlegget, i NOK.
    /// Default 4 000 (avtalt sats per 2026). Per anlegg slik at enkeltanlegg
    /// kan ha avvikende sats uten kodeendring. Brukes av
    /// <c>KaiaCostQueryService</c> til pro-rata-beregning av rapportperiodens
    /// andel av årsavgiften (dagbasert: <c>avgift × dager_i_periode / dager_i_året</c>).
    /// </summary>
    public double KaiaAnnualFeeNok { get; set; } = 4000;

    /// <summary>
    /// Forventet produksjon i et gjennomsnittlig (normalt) år, i GWh.
    /// Nullable — null = ikke satt ennå. Brukes til to ting (begge senere
    /// oppgaver):
    ///   1. Sammenligne faktisk produksjon mot normalåret (over/under snitt).
    ///   2. Fordele felleskostnader (eks. samlet vaktkost) etter GWh-andel
    ///      av porteføljen: <c>andel = anlegg_GWh / Σ alle_anlegg_GWh</c>.
    /// Drifts-leder oppgir verdien i GWh; resten av appen regner i MWh fra
    /// Elhub, så konvertering (1 GWh = 1000 MWh) gjøres i forbruks-koden.
    /// </summary>
    public double? NormalAarsproduksjonGwh { get; set; }

    /// <summary>
    /// Turbin-type — typisk "Francis", "Kaplan" eller "Pelton". Lagres som
    /// fri streng for å tillate fremtidige varianter uten kode-endring.
    /// Null = ikke satt.
    /// </summary>
    public string? TurbineType { get; set; }

    /// <summary>
    /// Fallhøyde i meter — vertikal forskjell mellom inntak og turbin.
    /// Brukes (sammen med <see cref="EnergyEquivalentKwhPerM3"/>) som basis
    /// for vannverdi-beregninger. Null = ikke satt.
    /// </summary>
    public double? HeadM { get; set; }

    /// <summary>
    /// Energiekvivalent — hvor mye energi en kubikkmeter vann gir gjennom
    /// dette anlegget, i kWh/m³. Funksjon av fallhøyde × virkningsgrad × g.
    /// Brukes til å konvertere SCADA-vannføring til potensiell energi.
    /// Null = ikke satt.
    /// </summary>
    public double? EnergyEquivalentKwhPerM3 { get; set; }

    /// <summary>
    /// Året anlegget ble satt i drift. Eldste i porteføljen er Øgreyfoss
    /// (1905); nyeste er Liavatn (2024). Kontekstuell info — eldre anlegg
    /// har typisk flere drifts-utfordringer. Null = ikke satt.
    /// </summary>
    public int? CommissioningYear { get; set; }

    /// <summary>
    /// Antall start/stopp-sykler per år OEM tillater før akselerert slitasje.
    /// Brukes til budsjett-progressbar i Rapport-detalj sin Start/stopp-KPI.
    /// Null = ikke satt; UI viser lenke til admin for å fylle inn.
    /// Spec NESTE-CHAT-START-STOPP-KPI.md (2026-05-22).
    /// </summary>
    public int? StartStoppBudsjettPerAar { get; set; }

    /// <summary>
    /// Estimert slitasje-kostnad per syklus (NOK). Settes per anlegg basert
    /// på OEM-vedlikeholdsplan eller egen erfaring. Brukes til NOK-anslag
    /// på Start/stopp-KPI-kortet. Null = ikke satt.
    /// </summary>
    public double? StartStoppKostnadPerSyklusNok { get; set; }

    /// <summary>
    /// Fritekst-felt for kildehenvisning til OEM-dokumentasjonen eller
    /// internt anslag (eks. «Voith OEM-manual 2018, kap. 6.3»). Vises i
    /// info-popoveren på KPI-kortet hvis satt.
    /// </summary>
    public string? StartStoppKilde { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
