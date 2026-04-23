using System.ComponentModel.DataAnnotations;

namespace KraftverkUptime.Api.Options;

/// <summary>
/// Konfigurasjon for <c>POST /api/v{version}/plants/{plantId}/settlements</c>.
///
/// Verdiene bindes fra <c>Settlements</c>-seksjonen i appsettings, og
/// valideres ved oppstart. Grensen gjelder hele request body (multipart-header
/// medregnet), så reell fil-størrelse er noen kilobyte lavere.
/// </summary>
public sealed class SettlementUploadOptions
{
    public const string SectionName = "Settlements";

    /// <summary>
    /// Maks størrelse på request body i bytes. Default 25 MB.
    /// </summary>
    [Range(1_000_000, 104_857_600)]
    public long MaxUploadBytes { get; set; } = 26_214_400;

    /// <summary>
    /// Tillatte Content-Type for den opplastede filen. Kan være tom for å tillate alt.
    /// Matching er case-insensitiv og ser bort fra parametere (f.eks. charset).
    /// </summary>
    public IReadOnlyList<string> AllowedContentTypes { get; set; } = new[]
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel",
        "application/octet-stream",
    };

    /// <summary>
    /// Dev-default for OwnerOrgId når <see cref="Core.Security.ICurrentUser"/>
    /// er <see cref="Infrastructure.Security.SystemUserContext"/> (ingen authn).
    /// Må være satt i dev; i prod skal OwnerOrgId hentes fra claims.
    ///
    /// TODO(Steg 5): Fjern når Entra ID-integrasjonen er på plass og
    /// <c>ICurrentUser.OrgId</c> er autoritativ.
    /// </summary>
    public string? DevDefaultOwnerOrgId { get; set; }
}
