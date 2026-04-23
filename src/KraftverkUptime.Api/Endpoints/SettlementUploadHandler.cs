using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Kjerne-logikk for opplastning av en settlement-fil. Isolert fra HTTP-lag slik
/// at den kan unit-testes uten <c>WebApplicationFactory</c>.
///
/// Flyt:
/// <list type="number">
///   <item>Bestem <c>OwnerOrgId</c> (claims i prod, dev-default for <see cref="Infrastructure.Security.SystemUserContext"/>).</item>
///   <item>Bygg kanonisk blob-sti <c>settlements/{org}/{plant}/{timestamp}-{filnavn}</c>.</item>
///   <item>Strøm filen inn i <see cref="IFileStorage"/> gjennom en <see cref="CryptoStream"/>
///         som beregner SHA256 av innholdet underveis (null ekstra buffring).</item>
///   <item>Bruk hex-encoded SHA256 som <c>IdempotencyKey</c> – samme fil gir samme nøkkel,
///         og unique-indeksen i <c>settlement_imports</c> hindrer dobbel import.</item>
///   <item>Enqueue <see cref="ParseSettlementJob"/> for asynkron parsing i Worker.</item>
/// </list>
/// </summary>
public sealed class SettlementUploadHandler
{
    private readonly IFileStorage _fileStorage;
    private readonly IJobQueue _jobQueue;
    private readonly ICurrentUser _currentUser;
    private readonly SettlementUploadOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SettlementUploadHandler> _logger;

    public SettlementUploadHandler(
        IFileStorage fileStorage,
        IJobQueue jobQueue,
        ICurrentUser currentUser,
        IOptions<SettlementUploadOptions> options,
        TimeProvider clock,
        ILogger<SettlementUploadHandler> logger)
    {
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validerer Content-Type mot konfigurert whitelist.
    /// </summary>
    public bool IsContentTypeAllowed(string? contentType)
    {
        if (_options.AllowedContentTypes.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        foreach (var allowed in _options.AllowedContentTypes)
        {
            if (string.Equals(mediaType, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tar imot en strøm med filinnhold, lagrer den, og legger en parse-jobb på kø.
    /// Strømmen leses én gang; caller må ikke seeke etter kall.
    /// </summary>
    /// <param name="plantId">Anleggs-ID fra rutenavnet.</param>
    /// <param name="fileContent">Rå Excel-bytes. Forventes å være seek-less.</param>
    /// <param name="fileName">Opprinnelig filnavn fra multipart-headeren (kan være null).</param>
    /// <param name="correlationId">Valgfri trace-/correlation-ID for sporing.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SettlementUploadResult> HandleAsync(
        string plantId,
        Stream fileContent,
        string? fileName,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentNullException.ThrowIfNull(fileContent);

        var ownerOrgId = ResolveOwnerOrgId();
        var timestamp = _clock.GetUtcNow().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var safeFileName = SanitizeFileName(fileName);
        var blobPath = $"settlements/{ownerOrgId}/{plantId}/{timestamp}-{safeFileName}";

        // CryptoStream i Read-modus leser fra fileContent og oppdaterer SHA256
        // etter hvert som IFileStorage konsumerer strømmen. Ingen dobbel buffring.
        using var sha256 = SHA256.Create();
        await using (var hashing = new CryptoStream(fileContent, sha256, CryptoStreamMode.Read, leaveOpen: true))
        {
            await _fileStorage.PutAsync(blobPath, hashing, ct).ConfigureAwait(false);
        }

        var idempotencyKey = Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>())
            .ToLowerInvariant();

        var trace = correlationId ?? Activity.Current?.TraceId.ToString();

        var job = new ParseSettlementJob(
            PlantId: plantId,
            OwnerOrgId: ownerOrgId,
            BlobPath: blobPath,
            IdempotencyKey: idempotencyKey,
            CorrelationId: trace);

        await _jobQueue.EnqueueAsync(job, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Settlement-fil kø-lagt for plant {PlantId} (org {OrgId}), blob {Blob}, hash {Hash}",
            plantId, ownerOrgId, blobPath, idempotencyKey);

        return new SettlementUploadResult(
            PlantId: plantId,
            OwnerOrgId: ownerOrgId,
            BlobPath: blobPath,
            IdempotencyKey: idempotencyKey,
            CorrelationId: trace);
    }

    private string ResolveOwnerOrgId()
    {
        // SystemUserContext (anonym authn, V1) har OrgId = "system". Da må vi
        // falle tilbake på dev-config. TODO(Steg 5): fjern når Entra ID er på plass.
        if (!string.Equals(_currentUser.OrgId, "system", StringComparison.Ordinal))
        {
            return _currentUser.OrgId;
        }

        if (string.IsNullOrWhiteSpace(_options.DevDefaultOwnerOrgId))
        {
            throw new InvalidOperationException(
                "Settlements:DevDefaultOwnerOrgId må være satt når anonym authn er aktiv "
                + "(ICurrentUser = SystemUserContext).");
        }

        return _options.DevDefaultOwnerOrgId;
    }

    [SuppressMessage("Design", "CA1308:Normalize strings to uppercase",
        Justification = "Filstier skal være lower-case etter konvensjon i prosjektet.")]
    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "settlement.xlsx";
        }

        // Fjern path-separatorer og ulovlige tegn – vi aksepterer bare basename.
        var span = Path.GetFileName(fileName.AsSpan());
        var buffer = new char[span.Length];
        var written = 0;
        foreach (var c in span)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            {
                buffer[written++] = c;
            }
            else
            {
                buffer[written++] = '_';
            }
        }

        var cleaned = new string(buffer, 0, written).ToLowerInvariant();
        return string.IsNullOrEmpty(cleaned) ? "settlement.xlsx" : cleaned;
    }
}

/// <summary>
/// Resultat av en vellykket opplasting. Returneres til klienten som 202-body.
/// </summary>
public sealed record SettlementUploadResult(
    string PlantId,
    string OwnerOrgId,
    string BlobPath,
    string IdempotencyKey,
    string? CorrelationId);
