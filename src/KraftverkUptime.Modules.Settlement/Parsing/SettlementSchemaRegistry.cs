namespace KraftverkUptime.Modules.Settlement.Parsing;

/// <summary>
/// Skjemaversjon som en parsed fil ble klassifisert som. Brukes til å legge til
/// nye eksportformater additivt når portalen endrer layout.
///
/// v1 = eksport med 22 kolonner i verkfane og standard kolonnenavn per
/// april 2026.
/// </summary>
public enum SettlementSchemaVersion
{
    Unknown = 0,
    V1PortalMonthly = 1
}

/// <summary>
/// Oppdager hvilken skjemaversjon en fil tilhører basert på header-kolonnenavn.
/// Gir også brukbar feilmelding når ingen av de registrerte versjonene matcher.
///
/// Ny versjon legges til ved å opprette en ny <c>SettlementSchemaV2</c>-klasse
/// og registrere den her – ingen endring i parser-kode nødvendig.
/// </summary>
public interface ISettlementSchemaRegistry
{
    /// <summary>Detekter skjemaversjonen fra en rå header-rad. Returnerer
    /// <see cref="SettlementSchemaVersion.Unknown"/> hvis ingen match.</summary>
    SettlementSchemaVersion Detect(IReadOnlyList<string?> headerRow);
}

public sealed class SettlementSchemaRegistry : ISettlementSchemaRegistry
{
    // Kjerne-kolonnene som må være til stede for v1. Kolonne 17 (spacer) og
    // utvidelseskolonnene 18–22 er valgfrie – noen måneder har dem ikke.
    private static readonly string[] V1RequiredCanonicalNames =
    {
        SettlementColumnMapping.Time,
        SettlementColumnMapping.MwhElhub,
        SettlementColumnMapping.MwhESett,
        SettlementColumnMapping.Spotbud,
        SettlementColumnMapping.Spotpris,
        SettlementColumnMapping.Ubalanse,
        SettlementColumnMapping.Oppgjor,
    };

    public SettlementSchemaVersion Detect(IReadOnlyList<string?> headerRow)
    {
        ArgumentNullException.ThrowIfNull(headerRow);

        var canonicals = headerRow
            .Select(SettlementColumnMapping.Canonical)
            .Where(c => c is not null)
            .ToHashSet();

        if (V1RequiredCanonicalNames.All(r => canonicals.Contains(r)))
        {
            return SettlementSchemaVersion.V1PortalMonthly;
        }

        return SettlementSchemaVersion.Unknown;
    }
}
