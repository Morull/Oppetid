using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using KraftverkUptime.Modules.Reporting.Portefolje;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF/blob-implementasjon av <see cref="IPortfolioQueryService"/>.
///
/// Strategi:
///   1. List alle anlegg i <c>core.plants</c>
///   2. For hvert anlegg: finn nyeste settlement-import som dekker perioden
///   3. Hent UptimeReport fra blob-storage via <see cref="IUptimeReportStore"/>
///   4. Mat KPI-listen fra rapporten direkte ut som dictionary
///
/// Anlegg uten import som dekker perioden utelates. Anlegg med InstalledCapacityMw=0
/// inkluderes (med 0-verdier på KPI-er som er kapasitets-avhengige) slik at
/// brukeren ser at anlegget mangler oppsett.
/// </summary>
public sealed class PortfolioQueryService : IPortfolioQueryService
{
    private readonly KraftverkDbContext _db;
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly ICaptureRateQueryService _captureRate;
    private readonly ILogger<PortfolioQueryService> _log;

    public PortfolioQueryService(
        KraftverkDbContext db,
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        ICaptureRateQueryService captureRate,
        ILogger<PortfolioQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _captureRate = captureRate ?? throw new ArgumentNullException(nameof(captureRate));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<PortfolioKpiResponse> GetKpisAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (toUtc <= fromUtc)
        {
            return new PortfolioKpiResponse(fromUtc, toUtc, 0, Array.Empty<PortfolioPlantKpi>());
        }

        var plants = await _db.Plants
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = new List<PortfolioPlantKpi>(plants.Count);

        foreach (var plant in plants)
        {
            // Bruker overlap-semantikk istedenfor "covers" fordi PeriodEndUtc
            // i settlement_imports er stempel for siste time (ikke eksklusiv
            // grense). Velg nyeste import som har overlapp med [from, to).
            var imports = await _imports
                .ListForPlantAsync(plant.Id, fromUtc, toUtc, limit: 1, ct)
                .ConfigureAwait(false);
            if (imports.Count == 0) continue;
            var import = imports[0];

            var report = await _reports
                .GetAsync(import.OwnerOrgId, import.PlantId, import.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null)
            {
                _log.LogDebug(
                    "Portefølje: import for {PlantId} eksisterer men rapport mangler i blob-store. Hopper over.",
                    plant.Id);
                continue;
            }

            var kpiDict = report.Kpis.ToDictionary(
                k => k.Name, k => k.Value, StringComparer.Ordinal);

            // Capture rate beregnes on-demand (er ikke en del av UptimeReport-blob).
            // Defensiv: hvis CR-kalkulatoren feiler (f.eks. mangler market_prices)
            // logger vi og fortsetter — KPI-listen viser bare 0 i UI istedenfor å
            // krasje hele portefølje-spørringen.
            try
            {
                var cr = await _captureRate
                    .GetForPlantAsync(plant.Id, fromUtc, toUtc, ct)
                    .ConfigureAwait(false);
                kpiDict["CapturePrice_NOK_MWh"] = cr.CapturePriceNokMwh;
                kpiDict["CaptureRate_Times"] = cr.TimesCr;
                kpiDict["CaptureRate_Dag"] = cr.DagCr;
                kpiDict["Merverdi_NOK"] = cr.MerverdiNok;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Portefølje: capture rate-kalkulasjon feilet for {PlantId}. Fortsetter uten CR-felter.",
                    plant.Id);
            }

            rows.Add(new PortfolioPlantKpi(
                PlantId: plant.Id,
                PlantName: plant.Name,
                InstalledCapacityMw: plant.InstalledCapacityMw,
                HourCount: report.PeriodHours,
                Kpis: kpiDict));
        }

        return new PortfolioKpiResponse(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PlantCount: rows.Count,
            Plants: rows);
    }
}
