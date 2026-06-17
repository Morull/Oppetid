using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Caching-dekorator over <see cref="IUptimeReportStore"/>. Samme rapport-blob
/// leses i dag 2–4× per anlegg innen ÉN forespørsel – settlement-KPI, capture-rate,
/// nedetid og datakvalitet går hver for seg rett på <see cref="GetAsync"/> – og på
/// tvers av nær-samtidige forespørsler. Blob-I/O (azurite/Azure) er flaskehalsen i
/// rapport-laget, så denne dekoratoren mellomlagrer <see cref="UptimeReport"/> per
/// nøkkel <c>(OwnerOrgId, PlantId, IdempotencyKey)</c> i en kort TTL og fjerner
/// dermed redundansen i ett grep – uten skjema-endring.
///
/// Konsistens:
/// <list type="bullet">
/// <item>Hver skriving (<see cref="SaveAsync"/>/<see cref="DeleteAsync"/>) invaliderer
/// nøkkelen sin, så re-import gir fersk data umiddelbart.</item>
/// <item>Bulk-slett (reset av anlegg/org) tømmer hele rapport-cachen via et delt
/// expiration-token.</item>
/// <item>TTL er en sikkerhetsnett-grense; <c>null</c> (manglende rapport) caches også,
/// så vi slutter å banke på manglende blober.</item>
/// </list>
/// Cachen er tråd-trygg (<see cref="IMemoryCache"/> + atomisk token-bytte), så den
/// fungerer også når anleggene senere kjøres i parallell.
///
/// Kjent grense: cachen er per-prosess. Klassifisering + <see cref="SaveAsync"/>
/// skjer i Worker-prosessen, mens rapporter leses i Api-prosessen, så en fersk
/// (re)import blir synlig i Api først når TTL utløper (≤ 2 min) – samme
/// eventual-consistency-vindu som de eksisterende cachene (settlement 30 min,
/// economy-DTO 3 min). En ekte distribuert cache (Redis) er v2-arbeid.
/// </summary>
public sealed class CachingUptimeReportStore : IUptimeReportStore, IDisposable
{
    // Kort nok til å begrense minnebruk, lang nok til å dekke alle lesningene i én
    // forespørsel + reload/nær-samtidige forespørsler. Skrivninger invaliderer
    // uansett eksplisitt, så TTL er bare en sikkerhetsnett-grense.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly IUptimeReportStore _inner;
    private readonly IMemoryCache _cache;

    // Delt token slik at bulk-slett kan evicte alle rapport-entries på én gang
    // (IMemoryCache har ingen "fjern etter prefiks").
    private readonly object _resetLock = new();
    private CancellationTokenSource _resetCts = new();

    public CachingUptimeReportStore(IUptimeReportStore inner, IMemoryCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    private static string CacheKey(string ownerOrgId, string plantId, string idempotencyKey)
        => $"uptimereport|{ownerOrgId}|{plantId}|{idempotencyKey}";

    public async Task<UptimeReport?> GetAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        CancellationToken ct)
    {
        var key = CacheKey(ownerOrgId, plantId, idempotencyKey);
        if (_cache.TryGetValue(key, out UptimeReport? cached))
        {
            return cached;
        }

        var report = await _inner
            .GetAsync(ownerOrgId, plantId, idempotencyKey, ct)
            .ConfigureAwait(false);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl };
        options.AddExpirationToken(new CancellationChangeToken(CurrentResetToken()));
        // Cacher også null bevisst: en manglende rapport skal ikke leses på nytt.
        _cache.Set<UptimeReport?>(key, report, options);
        return report;
    }

    public async Task SaveAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        UptimeReport report,
        CancellationToken ct)
    {
        await _inner
            .SaveAsync(ownerOrgId, plantId, idempotencyKey, report, ct)
            .ConfigureAwait(false);
        _cache.Remove(CacheKey(ownerOrgId, plantId, idempotencyKey));
    }

    public async Task DeleteAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        CancellationToken ct)
    {
        await _inner
            .DeleteAsync(ownerOrgId, plantId, idempotencyKey, ct)
            .ConfigureAwait(false);
        _cache.Remove(CacheKey(ownerOrgId, plantId, idempotencyKey));
    }

    public async Task<int> DeleteAllForPlantAsync(
        string ownerOrgId,
        string plantId,
        CancellationToken ct)
    {
        var deleted = await _inner
            .DeleteAllForPlantAsync(ownerOrgId, plantId, ct)
            .ConfigureAwait(false);
        ResetAll();
        return deleted;
    }

    public async Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct)
    {
        var deleted = await _inner.DeleteAllAsync(ownerOrgId, ct).ConfigureAwait(false);
        ResetAll();
        return deleted;
    }

    private CancellationToken CurrentResetToken()
    {
        lock (_resetLock)
        {
            return _resetCts.Token;
        }
    }

    private void ResetAll()
    {
        lock (_resetLock)
        {
            var old = _resetCts;
            _resetCts = new CancellationTokenSource();
            // Cancel evicter alle entries som henger på dette tokenet. Vi disposer
            // bevisst IKKE her: en samtidig cache-miss kan ha fanget tokenet og være
            // på vei til å registrere det på en ny entry – å disposere ville da kunne
            // kaste ObjectDisposedException. En cancellet CTS uten timer/registreringer
            // er trygt GC-bar. Den gjenværende CTS-en disposes i Dispose() ved shutdown.
            old.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_resetLock)
        {
            _resetCts.Dispose();
        }
    }
}
