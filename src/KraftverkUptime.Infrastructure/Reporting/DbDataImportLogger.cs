using KraftverkUptime.Core.DataCompleteness;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF-implementasjon av <see cref="IDataImportLogger"/>. Skriver én rad
/// til <c>core.data_imports</c>. Idempotens: vi tillater multiple rader
/// for samme (plant, source, period) — re-import og korrigering skal være
/// synlig i historikken.
///
/// Fail-loud: hvis DB-skriving feiler, kastes exception slik at importøren
/// kan rulle tilbake hvis den vil. I praksis logger callers (handlere)
/// feilen og fortsetter — completeness-matrisen blir litt unøyaktig, men
/// selve importen er allerede persistert.
/// </summary>
public sealed class DbDataImportLogger : IDataImportLogger
{
    private readonly KraftverkDbContext _db;
    private readonly ILogger<DbDataImportLogger> _log;

    public DbDataImportLogger(KraftverkDbContext db, ILogger<DbDataImportLogger> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task LogAsync(DataImportLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Defensiv normalisering (SPEC-IMPORT-KONSOLIDERT-15MIN Endring C):
        // «scada-fine» er avviklet som egen kilde — 15-min og hourly er samme
        // kilde i completeness. Normaliser her så INGEN kodevei kan gjenskape
        // den gamle kilden (den kunne verken administreres i PlantAdmin eller
        // vises pent i matrisen).
        var sourceType = string.Equals(entry.SourceType, "scada-fine", StringComparison.OrdinalIgnoreCase)
            ? "scada"
            : entry.SourceType;

        // Auto-aktiver expectation hvis det er første gang vi ser denne
        // kombinasjonen av (plant, source). Da bygger completeness-matrisen
        // seg opp basert på faktisk bruk uten at drifts-leder må toggle
        // manuelt via PlantAdmin.
        await EnsureExpectationAsync(entry.PlantId, sourceType, ct).ConfigureAwait(false);

        var record = new DataImport
        {
            ImportId = entry.ImportId ?? Guid.NewGuid(),
            PlantId = entry.PlantId,
            SourceType = sourceType,
            PeriodFromUtc = entry.PeriodFromUtc,
            PeriodToUtc = entry.PeriodToUtc,
            ImportedAtUtc = entry.ImportedAtUtc ?? DateTimeOffset.UtcNow,
            FileName = entry.FileName,
            FileHash = entry.FileHash,
            RowsImported = entry.RowsImported,
            CoveragePct = entry.CoveragePct,
            UserId = entry.UserId ?? "system",
            Notes = entry.Notes,
        };

        _db.DataImports.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _log.LogDebug(
                "data_imports logget: plant={Plant} source={Source} periode={From} ({Rows} rader, dekning={Coverage:P1})",
                entry.PlantId, entry.SourceType, entry.PeriodFromUtc,
                entry.RowsImported ?? 0, entry.CoveragePct ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Klarte ikke logge til data_imports for {Plant}/{Source}/{Period}",
                entry.PlantId, entry.SourceType, entry.PeriodFromUtc);
            throw;
        }
    }

    /// <summary>
    /// Idempotent: opprett <c>data_source_expectations</c>-rad hvis den
    /// ikke finnes for (plant, source). Default cadence = monthly, lag =
    /// 7 dager for settlement, 5 dager for SCADA/operlog. Aktivert nå.
    /// </summary>
    private async Task EnsureExpectationAsync(string plantId, string sourceType, CancellationToken ct)
    {
        var exists = await _db.DataSourceExpectations
            .AsNoTracking()
            .AnyAsync(e => e.PlantId == plantId && e.SourceType == sourceType, ct)
            .ConfigureAwait(false);
        if (exists) return;

        var defaultLag = sourceType switch
        {
            "settlement" => 7,
            "scada" => 5,
            "operlog" => 5,
            _ => 7,
        };

        // Forskjellige forventninger til dekning per kilde-type:
        //  - Settlement: KAIA leverer typisk 670/672 timer = 99.7 %
        //  - SCADA: snapshots og periodiske eksporter har naturlig hull
        //  - Operlog: events er per definisjon "kompletter når importert"
        var defaultThreshold = sourceType switch
        {
            "settlement" => 0.95,
            "scada" => 0.80,
            "operlog" => 0.95,
            _ => 0.95,
        };

        _db.DataSourceExpectations.Add(new DataSourceExpectation
        {
            PlantId = plantId,
            SourceType = sourceType,
            Cadence = "monthly",
            ExpectedLagDays = defaultLag,
            IsActive = true,
            // Sett activated_at til en konservativ start (2024-01-01) slik at
            // historiske importer regnes med i status-matrisen. Hvis vi setter
            // til "nå" filtreres tidligere perioder bort av query-en.
            ActivatedAtUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CompletionThresholdPct = defaultThreshold,
        });
        // SaveChanges skjer i hoved-LogAsync — vi unngår dobbel-roundtrip.
        _log.LogInformation(
            "Auto-aktivert expectation for {Plant}/{Source} (første import oppdaget).",
            plantId, sourceType);
    }
}
