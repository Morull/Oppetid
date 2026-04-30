namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Whitelist for hvilke SCADA-tags som skal lagres per anlegg. Importeren
/// matcher CSV-kolonner mot denne tabellen og kaster bort alt som ikke står
/// her — gir 30-50 % datamengde-reduksjon på SCADA-eksporter.
///
/// <see cref="Role"/> klassifiserer signalets analyse-bruk slik at
/// klassifikator og virkningsgrad-beregner kan slå opp tags ved rolle
/// istedenfor ved navn (anlegg-uavhengig logikk).
/// </summary>
public sealed record SignalMap(
    string PlantId,
    string SignalId,
    string CsvColumn,
    string Unit,
    SignalRole Role,
    bool StoreSamples,
    bool IsActive,
    string? DamId = null);  // Kaskade-modell: knytter dam-relaterte tags til en spesifikk dam.
                            // NULL for generator-tags og andre per-anleggs-signaler.

/// <summary>
/// Roller signaler kan ha i analyse-pipeline. Mapping fra rolle til
/// konkret signal_id slås opp via SignalMap.
/// </summary>
public enum SignalRole
{
    /// <summary>Generator aktiv effekt (kW) — primær drifts-indikator.</summary>
    GeneratorActivePower,

    /// <summary>Generator turtall (rpm) — synkron-deteksjon.</summary>
    GeneratorRpm,

    /// <summary>Generator frekvens (Hz).</summary>
    GeneratorFrequency,

    /// <summary>Turbinens vannføring Q (m³/s).</summary>
    TurbineWaterFlow,

    /// <summary>Turbin-virkningsgrad rapportert av SCADA (%).</summary>
    TurbineEfficiency,

    /// <summary>Ledeapparat-posisjon (%).</summary>
    GuideVanePosition,

    /// <summary>Pådrag-settpunkt (%).</summary>
    TurbinePadrag,

    /// <summary>Hydraulikk-trykk (bar).</summary>
    HydraulicPressure,

    /// <summary>Magasin-kote oppstrøms (moh).</summary>
    UpstreamLevel,

    /// <summary>Vannstand nedstrøms (moh).</summary>
    DownstreamLevel,

    /// <summary>Magasin-fyllgrad (%).</summary>
    ReservoirFillFactor,

    /// <summary>LRV-referanse for vannmangel-deteksjon (moh).</summary>
    LowestRegulatedLevel,

    /// <summary>Falltap over inntaksrist (mm).</summary>
    GridFallLoss,

    /// <summary>
    /// Overløps-vannføring (m³/s). Verdi > 0 betyr at vann renner forbi turbinen
    /// uten å produsere kraft — produksjon som ikke skjer mens denne er aktiv
    /// kunne uansett ikke vært utnyttet.
    /// </summary>
    OverflowFlow,

    /// <summary>Tilstand-indikator (lager-temp, vikling-temp, olje-temp).</summary>
    ConditionTemperature,

    /// <summary>Elektrisk måling (strøm, spenning, cos φ).</summary>
    ElectricalMeasurement,

    /// <summary>Kommunikasjons-alarm — true betyr datahull.</summary>
    CommunicationAlarm,

    /// <summary>Ikke kategorisert / generelt målepunkt.</summary>
    Other,

    // ----- Kaskade-modell (Spec KASKADE-DAMMER) ---------------------------
    // Disse rollene er per-dam; SignalMap.DamId må settes når de er aktive.

    /// <summary>Vannføring gjennom dam-luke (m³/s).</summary>
    GateFlow,

    /// <summary>Lukens åpningsposisjon (cm eller %).</summary>
    GatePosition,

    /// <summary>Total vannføring ut av dam (m³/s) — sum av luke + overløp + turbin.</summary>
    TotalDamFlow,

    /// <summary>Magasinvolum (Mill.m³).</summary>
    ReservoirVolume,
}
