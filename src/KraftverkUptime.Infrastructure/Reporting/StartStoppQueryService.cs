using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.StartStopp;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF/blob-implementasjon av <see cref="IStartStoppQueryService"/>. Plukker
/// time-serien (MwhElhub) fra UptimeReport-blobben for hver settlement-import
/// som overlapper perioden, og kjører <see cref="StartStoppCalculator"/>.
///
/// Tre passeringer:
/// <list type="number">
///   <item>Hovedperiode [from, to) — gir <c>AntallStarter</c>.</item>
///   <item>Samme lengde umiddelbart før [prevFrom, from) — gir
///     <c>AntallStarterForrige</c>. Bruker Custom-semantikk (lik-lengde) for
///     å unngå avhengighet til PeriodKind på endepunktet.</item>
///   <item>ÅTD [1.jan, to) — gir <c>BudsjettBruktAtd</c>. Hopper hvis
///     anlegget ikke har budsjett satt.</item>
/// </list>
/// </summary>
public sealed class StartStoppQueryService : IStartStoppQueryService
{
    private readonly KraftverkDbContext _db;
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly ILogger<StartStoppQueryService> _log;

    public StartStoppQueryService(
        KraftverkDbContext db,
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        ILogger<StartStoppQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<StartStoppDto?> BuildAsync(
        string plantId,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken ct)
    {
        var plant = await _db.Plants.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == plantId, ct)
            .ConfigureAwait(false);
        if (plant is null) return null;

        var installedMw = plant.InstalledCapacityMw;

        var antallStarter = await CountStartsAsync(plantId, periodStartUtc, periodEndUtc, installedMw, ct)
            .ConfigureAwait(false);

        // Forrige periode = samme lengde umiddelbart før hovedperioden
        // (Custom-semantikk). Hvis settlement-data mangler i det vinduet
        // får vi 0, som UI tolker som "ingen sammenligning" via null-sjekk
        // på EndringProsent.
        var lengthTicks = periodEndUtc.Ticks - periodStartUtc.Ticks;
        var prevFrom = periodStartUtc.AddTicks(-lengthTicks);
        var prevTo = periodStartUtc;
        int? antallStarterForrige = null;
        try
        {
            antallStarterForrige = await CountStartsAsync(plantId, prevFrom, prevTo, installedMw, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "StartStopp: forrige-periode-telling feilet for {PlantId} — trend skjules.",
                plantId);
        }

        double? endringProsent = ComputeRelativeChange(antallStarter, antallStarterForrige);

        // ÅTD-telling kjøres kun hvis budsjettet er satt — ellers er det
        // ingen UI-element som forbruker tallet.
        int? budsjettBruktAtd = null;
        double? budsjettBruktProsent = null;
        if (plant.StartStoppBudsjettPerAar.HasValue && plant.StartStoppBudsjettPerAar.Value > 0)
        {
            try
            {
                var aaretStart = new DateTimeOffset(periodEndUtc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var atd = await CountStartsAsync(plantId, aaretStart, periodEndUtc, installedMw, ct)
                    .ConfigureAwait(false);
                budsjettBruktAtd = atd;
                budsjettBruktProsent = 100.0 * atd / plant.StartStoppBudsjettPerAar.Value;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex,
                    "StartStopp: ÅTD-telling feilet for {PlantId} — progressbar skjules.",
                    plantId);
            }
        }

        double? kostnadTotal = plant.StartStoppKostnadPerSyklusNok.HasValue
            ? antallStarter * plant.StartStoppKostnadPerSyklusNok.Value
            : (double?)null;

        return new StartStoppDto(
            AntallStarter: antallStarter,
            AntallStarterForrige: antallStarterForrige,
            EndringProsent: endringProsent,
            BudsjettPerAar: plant.StartStoppBudsjettPerAar,
            BudsjettBruktAtd: budsjettBruktAtd,
            BudsjettBruktProsent: budsjettBruktProsent,
            KostnadPerSyklus: plant.StartStoppKostnadPerSyklusNok,
            KostnadTotalNok: kostnadTotal,
            Kilde: plant.StartStoppKilde);
    }

    /// <summary>
    /// Plukker MwhElhub fra alle UptimeReport-blobs som overlapper
    /// [from, to), dedupliserer per (PeriodStart, PeriodEnd) (reimport-vinner),
    /// og kjører <see cref="StartStoppCalculator"/> på den sammenslåtte
    /// time-serien. Returnerer 0 hvis ingen rapporter dekker perioden.
    /// </summary>
    private async Task<int> CountStartsAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        double installedMw,
        CancellationToken ct)
    {
        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 240, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0) return 0;

        var distinct = imports
            .GroupBy(i => (i.PeriodStartUtc, i.PeriodEndUtc))
            .Select(g => g.OrderByDescending(i => i.ImportedAtUtc).First())
            .ToList();

        var hours = new List<HourlyProduction>(distinct.Count * 720);
        foreach (var import in distinct)
        {
            var report = await _reports
                .GetAsync(import.OwnerOrgId, import.PlantId, import.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            foreach (var c in report.Classified)
            {
                // Klipp til [fromUtc, toUtc) for å unngå at lange imports
                // som strekker seg utenfor perioden teller med.
                if (c.TimeUtc < fromUtc || c.TimeUtc >= toUtc) continue;
                hours.Add(new HourlyProduction(c.TimeUtc, c.Row.MwhElhub));
            }
        }

        return StartStoppCalculator.CountStarts(hours, installedMw);
    }

    private static double? ComputeRelativeChange(int now, int? prev)
    {
        if (!prev.HasValue) return null;
        if (prev.Value == 0) return null;
        return (now - prev.Value) / (double)Math.Abs(prev.Value);
    }
}
