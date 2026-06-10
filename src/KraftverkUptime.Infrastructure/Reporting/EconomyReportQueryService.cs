using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using KraftverkUptime.Modules.Reporting.DataQuality;
using KraftverkUptime.Modules.Reporting.Economy;
using KraftverkUptime.Modules.Reporting.KaiaCost;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Reporting.Portefolje;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Aggregator-implementasjon av <see cref="IEconomyReportQueryService"/>.
/// Bygger <see cref="EconomyReportDto"/> ved å kombinere tall fra eksisterende
/// per-plant og portefølje-tjenester. Tjenesten kjører to passeringer (valgt
/// periode + forrige periode fra <see cref="PreviousPeriodCalculator"/>) og
/// fyller trend-prosenter ferdig i DTO-en slik at UI/PDF ikke trenger logikk.
///
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22), del 4.
/// </summary>
public sealed class EconomyReportQueryService : IEconomyReportQueryService
{
    private const string VaktKostPortfolioPlantId = "_portfolio_";
    private const string VaktKostKey = "vakt_kost_nok_per_aar";
    private const double VaktKostDefaultNokPerAar = 360_000d;
    private const double DagerPerAar = 365.25d;

    private readonly KraftverkDbContext _db;
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly ICaptureRateQueryService _captureRate;
    private readonly IKaiaCostQueryService _kaia;
    private readonly IPortfolioVaktRoiQueryService _vaktRoi;
    private readonly INedetidQueryService _nedetid;
    private readonly IDataQualityQueryService _dataQuality;
    private readonly IPlantConfiguration _config;
    private readonly ILogger<EconomyReportQueryService> _log;

    public EconomyReportQueryService(
        KraftverkDbContext db,
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        ICaptureRateQueryService captureRate,
        IKaiaCostQueryService kaia,
        IPortfolioVaktRoiQueryService vaktRoi,
        INedetidQueryService nedetid,
        IDataQualityQueryService dataQuality,
        IPlantConfiguration config,
        ILogger<EconomyReportQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _captureRate = captureRate ?? throw new ArgumentNullException(nameof(captureRate));
        _kaia = kaia ?? throw new ArgumentNullException(nameof(kaia));
        _vaktRoi = vaktRoi ?? throw new ArgumentNullException(nameof(vaktRoi));
        _nedetid = nedetid ?? throw new ArgumentNullException(nameof(nedetid));
        _dataQuality = dataQuality ?? throw new ArgumentNullException(nameof(dataQuality));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<EconomyReportDto> GetAsync(
        IReadOnlyList<string> plantIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        PeriodKind kind,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plantIds);
        if (plantIds.Count == 0)
        {
            throw new ArgumentException("plantIds må inneholde minst ett anlegg.", nameof(plantIds));
        }
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("toUtc må være strengt etter fromUtc.", nameof(toUtc));
        }

        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(fromUtc, toUtc, kind);

        // Hent alle anlegg én gang — vi trenger hele porteføljen for GWh-andel-
        // nevneren (vakt-kost fordeles relativt til totalsummen, ikke bare det
        // valgte utvalget).
        var allPlants = await _db.Plants.AsNoTracking()
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var plantsById = allPlants.ToDictionary(p => p.Id, StringComparer.Ordinal);

        // "all" som sentinel-ID = hele porteføljen. Endepunktet sender dette
        // videre uten å eksplodere listen, siden den ikke kjenner plants-tabellen.
        var wantsAll = plantIds.Any(id => string.Equals(id, "all", StringComparison.OrdinalIgnoreCase));
        var selected = wantsAll
            ? allPlants.Select(p => p.Id).ToList()
            : plantIds.Distinct(StringComparer.Ordinal)
                .Where(plantsById.ContainsKey)
                .ToList();

        if (selected.Count == 0)
        {
            _log.LogWarning(
                "EconomyReport: Ingen av {Count} forespurte anleggs-ID-er finnes i porteføljen.",
                plantIds.Count);
            return EmptyReport(plantIds, fromUtc, toUtc, prevFrom, prevTo);
        }

        var vaktKostAarlig = await GetPortfolioVaktKostAsync(ct).ConfigureAwait(false);
        var totalGwhPortefolje = allPlants.Sum(p => p.NormalAarsproduksjonGwh ?? 0);

        // To passeringer sekvensielt — én current, én previous. DbContext er
        // ikke tråd-sikker, så vi kan ikke kjøre dem i parallel selv om det
        // ville gitt halvert latens. EF Core kaster "second operation was
        // started on this context" hvis to queries treffer samtidig.
        var current = await GatherSnapshotsAsync(
            selected, plantsById, fromUtc, toUtc,
            vaktKostAarlig, totalGwhPortefolje, ct).ConfigureAwait(false);
        var previous = await GatherSnapshotsAsync(
            selected, plantsById, prevFrom, prevTo,
            vaktKostAarlig, totalGwhPortefolje, ct).ConfigureAwait(false);

        var inntekter = BuildInntekter(current, previous);
        var kostnader = BuildKostnader(current, previous);
        var resultat = BuildResultat(current, previous);
        var perPlant = BuildPerPlantRows(selected, plantsById, current);

        return new EconomyReportDto(
            PlantIds: selected.ToArray(),
            From: fromUtc,
            To: toUtc,
            PrevFrom: prevFrom,
            PrevTo: prevTo,
            Inntekter: inntekter,
            Kostnader: kostnader,
            Resultat: resultat,
            PerPlant: perPlant);
    }

    // ---------------------------------------------------------------------
    // Snapshot-bygging — én pass per periode.
    // ---------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, PlantSnapshot>> GatherSnapshotsAsync(
        IReadOnlyList<string> plantIds,
        IReadOnlyDictionary<string, PlantRegistration> plantsById,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        double vaktKostAarlig,
        double totalGwhPortefolje,
        CancellationToken ct)
    {
        // Portefølje-tjenestene kalles sekvensielt fordi de begge bruker
        // KraftverkDbContext (Scoped) som ikke er tråd-sikker. Parallel
        // utførelse her trigger "second operation was started on this context".
        // KaiaCostQueryService returnerer én rad per (plant, måned) — et
        // YTD-query med 5 måneder gir 5 rader per plant. Summer per plant
        // for å få total KAIA-kostnad over hele perioden.
        var kaiaResults = await _kaia.GetForPortfolioAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        var kaiaByPlant = kaiaResults
            .GroupBy(k => k.PlantId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(k => k.TotalNok ?? 0), StringComparer.Ordinal);

        var vaktRoi = await _vaktRoi.GetAsync(fromUtc, toUtc, topN: 1, vaktOptions: null, ct)
            .ConfigureAwait(false);
        var vaktRoiByPlant = vaktRoi.PerPlant
            .ToDictionary(p => p.PlantId, StringComparer.Ordinal);

        // Datakvalitets-bulk: én EF-query for hele porteføljen istedenfor
        // N-kall per anlegg (Spec MASTERPLAN-CODE-2026-05-22 § «Sammendrag
        // reuser /economy», 2026-05-22). Brukes til DataQuality-kolonnen +
        // Sammendrag-banneret "Importen rekker til DD.MM.".
        IReadOnlyDictionary<string, DataQualitySummary> dqByPlant;
        try
        {
            var dqList = await _dataQuality.GetSummariesForAllPlantsAsync(fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            dqByPlant = dqList.ToDictionary(d => d.PlantId, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "EconomyReport: DataQuality-bulk feilet — fortsetter uten DQ-felt.");
            dqByPlant = new Dictionary<string, DataQualitySummary>(StringComparer.Ordinal);
        }

        var dagerIPeriode = (toUtc - fromUtc).TotalDays;
        var vaktKostIPeriode = vaktKostAarlig * (dagerIPeriode / DagerPerAar);

        // Plant-fetcher itereres sekvensielt av samme tråd-sikkerhets-grunn.
        // For 11 anlegg er totalkostnaden lav nok at vi ikke trenger
        // parallellisering — innenfor hver plant gjør GatherPlantAsync sine
        // egne sekvensielle await for blob/DB.
        var results = new Dictionary<string, PlantSnapshot>(plantIds.Count, StringComparer.Ordinal);
        foreach (var plantId in plantIds)
        {
            var plant = plantsById[plantId];
            var s = await GatherPlantAsync(plant, fromUtc, toUtc, ct).ConfigureAwait(false);

            var kaiaNok = kaiaByPlant.TryGetValue(plantId, out var k) ? k : 0;
            var vaktSummary = vaktRoiByPlant.TryGetValue(plantId, out var v) ? v : null;
            var reddetNok = vaktSummary?.ReddetNok ?? 0;
            var antallReddbare = vaktSummary?.ReddbareEvents ?? 0;
            var vaktKostNok = ComputePlantVaktKostShare(
                plant, vaktKostIPeriode, totalGwhPortefolje, plantsById.Count);

            var dq = dqByPlant.TryGetValue(plantId, out var d) ? d : null;
            var goodPct = dq?.GoodPct ?? 0;
            var manglerImportHours = dq?.ManglerImportHours ?? 0;

            results[plantId] = s with
            {
                KaiaKostnadNok = kaiaNok,
                ReddetAvVaktNok = reddetNok,
                AntallReddbareEvents = antallReddbare,
                VaktKostAndelNok = vaktKostNok,
                GoodHoursPct = goodPct,
                ManglerImportHours = manglerImportHours,
            };
        }
        return results;
    }

    private async Task<PlantSnapshot> GatherPlantAsync(
        PlantRegistration plant,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        // Settlement-baserte KPI-er (Spotomsetning, Oppgjør, Ubalansekost, MWh)
        // summeres over ALLE imports som overlapper perioden. Tidligere brukte
        // vi limit:1 (siste import) — det gjorde at YTD/Custom returnerte bare
        // siste måned istedenfor sum (Spec NESTE-CHAT-OKONOMI-OPPFOLGING.md
        // Funn 1, 2026-05-22).
        //
        // Reimport-/overlapp-deduplisering: kollaps importer som overlapper i
        // tid, ikke bare de med eksakt lik (PeriodStart, PeriodEnd). Ellers
        // dobbelttelles en re-import av samme måned med litt ulik datospenn
        // (f.eks. «mai 1–17» + «mai 1–20»). Se OverlappingImportResolver.
        var imports = await _imports
            .ListForPlantAsync(plant.Id, fromUtc, toUtc, limit: 240, ct)
            .ConfigureAwait(false);

        var distinctPerPeriod = OverlappingImportResolver.ResolveNonOverlapping(
            imports,
            i => i.PeriodStartUtc,
            i => i.PeriodEndUtc,
            i => i.ImportedAtUtc);

        // Drift-KPI-er (AF, FOR) er ratio og må MWh-vektes på tvers av imports
        // for at flermånedsperiode skal gi riktig snitt. Vekt = PeriodHours
        // (antall klassifiserte timer i hver import) siden ratioene rapporteres
        // pr time.
        double spot = 0, oppgjor = 0, ubalanse = 0, mwh = 0;
        double afWeightedSum = 0, forWeightedSum = 0, afIeeeWeightedSum = 0;
        double afWeightHoursSum = 0, forWeightHoursSum = 0, afIeeeWeightHoursSum = 0;
        foreach (var import in distinctPerPeriod)
        {
            var report = await _reports
                .GetAsync(import.OwnerOrgId, import.PlantId, import.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            // Behold nullbarhet: et KPI som ikke kunne beregnes (null) er noe
            // annet enn verdien 0. Summene under er additive — der er null→0
            // riktig — men for de MWh-vektede ratioene (AF/FOR) ville null→0
            // dratt snittet kunstig ned (FAGVURDERING-KPI-BEREGNINGER #3).
            var kpis = report.Kpis.ToDictionary(k => k.Name, k => k.Value, StringComparer.Ordinal);
            spot += kpis.GetValueOrDefault("Spotomsetning_NOK") ?? 0;
            oppgjor += kpis.GetValueOrDefault("Oppgjor_NOK") ?? 0;
            ubalanse += kpis.GetValueOrDefault("Ubalansekost_NOK") ?? 0;
            mwh += kpis.GetValueOrDefault("TotalProduction_MWh") ?? 0;

            // AF/FOR: vekt KUN inn de importene der ratioen faktisk er beregnet.
            // En datafattig måned (AF=null) skal ikke telle med full PeriodHours
            // i nevneren — ellers raseres porteføljesnittet. En genuin AF=0
            // (anlegget nede hele måneden) har HasValue=true og teller normalt.
            var weight = report.PeriodHours;
            var afVal = kpis.GetValueOrDefault("AvailabilityFactor_AF");
            if (afVal.HasValue)
            {
                afWeightedSum += afVal.Value * weight;
                afWeightHoursSum += weight;
            }
            var forVal = kpis.GetValueOrDefault("ForcedOutageRate_FOR");
            if (forVal.HasValue)
            {
                forWeightedSum += forVal.Value * weight;
                forWeightHoursSum += weight;
            }
            // Ekte IEEE-AF (FAGVURDERING #2) — samme null-bevisste vekting.
            var afIeeeVal = kpis.GetValueOrDefault("AvailabilityFactorIeee_AF");
            if (afIeeeVal.HasValue)
            {
                afIeeeWeightedSum += afIeeeVal.Value * weight;
                afIeeeWeightHoursSum += weight;
            }
        }
        var availabilityFactor = afWeightHoursSum > 0 ? afWeightedSum / afWeightHoursSum : 0;
        var forcedOutageRate = forWeightHoursSum > 0 ? forWeightedSum / forWeightHoursSum : 0;
        var availabilityFactorIeee = afIeeeWeightHoursSum > 0 ? afIeeeWeightedSum / afIeeeWeightHoursSum : 0;

        // Capture rate + merverdi kan feile (mangler market_prices etc.) —
        // konservativt: logg og fortsett med 0 istedenfor å krasje hele
        // rapporten.
        double cr = 0, merverdi = 0;
        try
        {
            var crResult = await _captureRate
                .GetForPlantAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            cr = crResult.TimesCr;
            merverdi = crResult.MerverdiNok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "EconomyReport: capture rate-kalkulasjon feilet for {PlantId}. Fortsetter med 0.",
                plant.Id);
        }

        double nedetidstap = 0;
        double nedetidTimer = 0;
        var antallEvents = 0;
        try
        {
            var events = await _nedetid
                .ListEventsAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            nedetidstap = events.Sum(e => e.TapNok);
            nedetidTimer = events.Sum(e => e.VarighetTimer);
            antallEvents = events.Count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "EconomyReport: nedetids-events feilet for {PlantId}. Fortsetter med 0.",
                plant.Id);
        }

        return new PlantSnapshot(
            SpotomsetningNok: spot,
            OppgjorNok: oppgjor,
            UbalansekostNok: ubalanse,
            TotalProductionMwh: mwh,
            CaptureRate: cr,
            MerverdiNok: merverdi,
            NedetidstapNok: nedetidstap,
            KaiaKostnadNok: 0,
            ReddetAvVaktNok: 0,
            VaktKostAndelNok: 0,
            AvailabilityFactor: availabilityFactor,
            AvailabilityFactorIeee: availabilityFactorIeee,
            ForcedOutageRate: forcedOutageRate,
            NedetidTimer: nedetidTimer,
            AntallEvents: antallEvents,
            AntallReddbareEvents: 0,
            GoodHoursPct: 0,
            ManglerImportHours: 0);
    }

    // ---------------------------------------------------------------------
    // Vakt-kost fordeling: NOK/år × dager-i-periode / 365.25 × GWh-andel.
    // Andelens nevner er hele porteføljens normal-GWh, ikke bare utvalget.
    // ---------------------------------------------------------------------

    private static double ComputePlantVaktKostShare(
        PlantRegistration plant,
        double vaktKostIPeriode,
        double totalGwhPortefolje,
        int plantsInPortefolje)
    {
        if (totalGwhPortefolje > 0)
        {
            var plantGwh = plant.NormalAarsproduksjonGwh ?? 0;
            if (plantGwh <= 0) return 0;
            return vaktKostIPeriode * plantGwh / totalGwhPortefolje;
        }

        // Fallback når ingen anlegg har GWh satt: lik fordeling 1/N.
        return plantsInPortefolje > 0 ? vaktKostIPeriode / plantsInPortefolje : 0;
    }

    private async Task<double> GetPortfolioVaktKostAsync(CancellationToken ct)
    {
        var stored = await _config
            .GetAsync<double?>(VaktKostPortfolioPlantId, VaktKostKey, ct)
            .ConfigureAwait(false);
        return stored ?? VaktKostDefaultNokPerAar;
    }

    // ---------------------------------------------------------------------
    // KPI-grupper — aggregering over utvalget + trend mot forrige periode.
    // ---------------------------------------------------------------------

    private static EconomyKpiGroupDto BuildInntekter(
        IReadOnlyDictionary<string, PlantSnapshot> current,
        IReadOnlyDictionary<string, PlantSnapshot> previous)
    {
        var spotNow = current.Values.Sum(s => s.SpotomsetningNok);
        var spotPrev = previous.Values.Sum(s => s.SpotomsetningNok);

        var crNow = MwhWeightedCaptureRate(current.Values);
        var crPrev = MwhWeightedCaptureRate(previous.Values);

        var merverdiNow = current.Values.Sum(s => s.MerverdiNok);
        var merverdiPrev = previous.Values.Sum(s => s.MerverdiNok);

        return new EconomyKpiGroupDto(
            Title: "Inntekter",
            Kpis: new[]
            {
                BuildKpi(EconomyKpiKeys.Spotomsetning, "Spotomsetning",
                    spotNow, spotPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
                BuildKpi(EconomyKpiKeys.CaptureRate, "Capture rate",
                    crNow, crPrev, EconomyKpiUnit.Ratio, EconomyKpiDirection.Up),
                BuildKpi(EconomyKpiKeys.MerverdiVsSpot, "Merverdi vs spot",
                    merverdiNow, merverdiPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
            });
    }

    private static EconomyKpiGroupDto BuildKostnader(
        IReadOnlyDictionary<string, PlantSnapshot> current,
        IReadOnlyDictionary<string, PlantSnapshot> previous)
    {
        // Ubalansekost-konvensjon i UptimeKpiCalculator: positiv = gevinst,
        // negativ = nettokostnad. Vi viser kostnaden som positivt tall i
        // Økonomi-fanen slik at "opp = rød" matcher intuisjonen.
        var ubalanseNow = -current.Values.Sum(s => s.UbalansekostNok);
        var ubalansePrev = -previous.Values.Sum(s => s.UbalansekostNok);

        var kaiaNow = current.Values.Sum(s => s.KaiaKostnadNok);
        var kaiaPrev = previous.Values.Sum(s => s.KaiaKostnadNok);

        var vaktNow = current.Values.Sum(s => s.VaktKostAndelNok);
        var vaktPrev = previous.Values.Sum(s => s.VaktKostAndelNok);

        return new EconomyKpiGroupDto(
            Title: "Kostnader",
            Kpis: new[]
            {
                BuildKpi(EconomyKpiKeys.Ubalansekost, "Ubalansekost",
                    ubalanseNow, ubalansePrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                BuildKpi(EconomyKpiKeys.KaiaKostnad, "KAIA-kostnad",
                    kaiaNow, kaiaPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                BuildKpi(EconomyKpiKeys.VaktKostAndel, "Vakt-kost-andel",
                    vaktNow, vaktPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
            });
    }

    private static EconomyKpiGroupDto BuildResultat(
        IReadOnlyDictionary<string, PlantSnapshot> current,
        IReadOnlyDictionary<string, PlantSnapshot> previous)
    {
        var oppgjorNow = current.Values.Sum(s => s.OppgjorNok);
        var oppgjorPrev = previous.Values.Sum(s => s.OppgjorNok);

        var nedetidNow = current.Values.Sum(s => s.NedetidstapNok);
        var nedetidPrev = previous.Values.Sum(s => s.NedetidstapNok);

        var reddetNow = current.Values.Sum(s => s.ReddetAvVaktNok);
        var reddetPrev = previous.Values.Sum(s => s.ReddetAvVaktNok);

        return new EconomyKpiGroupDto(
            Title: "Resultat / drift",
            Kpis: new[]
            {
                BuildKpi(EconomyKpiKeys.Oppgjor, "Oppgjør",
                    oppgjorNow, oppgjorPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
                BuildKpi(EconomyKpiKeys.Nedetidstap, "Nedetidstap",
                    nedetidNow, nedetidPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                BuildKpi(EconomyKpiKeys.ReddetAvVakt, "Reddet av vakt",
                    reddetNow, reddetPrev, EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
            });
    }

    private static PerPlantEconomyDto[] BuildPerPlantRows(
        IReadOnlyList<string> selected,
        IReadOnlyDictionary<string, PlantRegistration> plantsById,
        IReadOnlyDictionary<string, PlantSnapshot> current)
    {
        return selected.Select(id =>
        {
            var s = current[id];
            var p = plantsById[id];
            return new PerPlantEconomyDto(
                PlantId: id,
                PlantName: p.Name,
                OppgjorNok: s.OppgjorNok,
                SpotomsetningNok: s.SpotomsetningNok,
                // Vises som positiv kostnad i tabellen (samme konvensjon
                // som Kostnader-gruppen over).
                UbalansekostNok: -s.UbalansekostNok,
                KaiaKostnadNok: s.KaiaKostnadNok,
                CaptureRate: s.CaptureRate,
                InstalledCapacityMw: p.InstalledCapacityMw,
                TotalProductionMwh: s.TotalProductionMwh,
                MerverdiNok: s.MerverdiNok,
                AvailabilityFactor: s.AvailabilityFactor,
                AvailabilityFactorIeee: s.AvailabilityFactorIeee,
                ForcedOutageRate: s.ForcedOutageRate,
                NedetidTimer: s.NedetidTimer,
                NedetidstapNok: s.NedetidstapNok,
                ReddetAvVaktNok: s.ReddetAvVaktNok,
                AntallEvents: s.AntallEvents,
                AntallReddbareEvents: s.AntallReddbareEvents,
                NormalAarsproduksjonGwh: p.NormalAarsproduksjonGwh,
                GoodHoursPct: s.GoodHoursPct,
                ManglerImportHours: s.ManglerImportHours);
        }).ToArray();
    }

    // ---------------------------------------------------------------------
    // KPI- og trend-byggere.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Capture rate aggregeres MWh-vektet: Σ(CR × MWh) / Σ MWh. Snitt over
    /// anlegg uten produksjon (MWh = 0) gir CR = 0, ikke null, slik at
    /// UI-en viser "0,00 %" istedenfor "–".
    /// </summary>
    private static double MwhWeightedCaptureRate(IEnumerable<PlantSnapshot> snapshots)
    {
        double weightedSum = 0;
        double totalMwh = 0;
        foreach (var s in snapshots)
        {
            if (s.TotalProductionMwh <= 0) continue;
            weightedSum += s.CaptureRate * s.TotalProductionMwh;
            totalMwh += s.TotalProductionMwh;
        }
        return totalMwh > 0 ? weightedSum / totalMwh : 0;
    }

    private static EconomyKpiDto BuildKpi(
        string key, string label,
        double verdiNow, double verdiPrev,
        string enhet, string goodDirection)
    {
        var endring = ComputeRelativeChange(verdiNow, verdiPrev);
        return new EconomyKpiDto(
            Key: key,
            Label: label,
            Verdi: verdiNow,
            Enhet: enhet,
            VerdiForrige: verdiPrev,
            EndringProsent: endring,
            GoodDirection: goodDirection);
    }

    /// <summary>
    /// Relativ endring (1,0 = +100 %). Null hvis forrige periode er 0
    /// eller hvis tallet ikke gir mening — UI-en viser "–" da. Brukes
    /// kun for trend-pilen, ikke for selve verdien.
    /// </summary>
    private static double? ComputeRelativeChange(double now, double prev)
    {
        if (prev == 0) return null;
        if (double.IsNaN(prev) || double.IsNaN(now)) return null;
        return (now - prev) / Math.Abs(prev);
    }

    private static EconomyReportDto EmptyReport(
        IReadOnlyList<string> plantIds,
        DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset prevFrom, DateTimeOffset prevTo)
    {
        EconomyKpiGroupDto empty(string title, params (string key, string label, string unit, string dir)[] kpis) =>
            new(title, kpis.Select(k => new EconomyKpiDto(
                k.key, k.label, 0, k.unit, null, null, k.dir)).ToArray());

        return new EconomyReportDto(
            PlantIds: plantIds.ToArray(),
            From: from, To: to, PrevFrom: prevFrom, PrevTo: prevTo,
            Inntekter: empty("Inntekter",
                (EconomyKpiKeys.Spotomsetning, "Spotomsetning", EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
                (EconomyKpiKeys.CaptureRate, "Capture rate", EconomyKpiUnit.Ratio, EconomyKpiDirection.Up),
                (EconomyKpiKeys.MerverdiVsSpot, "Merverdi vs spot", EconomyKpiUnit.Nok, EconomyKpiDirection.Up)),
            Kostnader: empty("Kostnader",
                (EconomyKpiKeys.Ubalansekost, "Ubalansekost", EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                (EconomyKpiKeys.KaiaKostnad, "KAIA-kostnad", EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                (EconomyKpiKeys.VaktKostAndel, "Vakt-kost-andel", EconomyKpiUnit.Nok, EconomyKpiDirection.Down)),
            Resultat: empty("Resultat / drift",
                (EconomyKpiKeys.Oppgjor, "Oppgjør", EconomyKpiUnit.Nok, EconomyKpiDirection.Up),
                (EconomyKpiKeys.Nedetidstap, "Nedetidstap", EconomyKpiUnit.Nok, EconomyKpiDirection.Down),
                (EconomyKpiKeys.ReddetAvVakt, "Reddet av vakt", EconomyKpiUnit.Nok, EconomyKpiDirection.Up)),
            PerPlant: Array.Empty<PerPlantEconomyDto>());
    }

    // ---------------------------------------------------------------------
    // Per-plant snapshot — alle målte verdier som inngår i ett anleggs
    // bidrag til en KPI-gruppe for én periode. Verdier som ikke kunne
    // beregnes er 0 (ikke null) for å unngå spesialhåndtering i summer.
    // ---------------------------------------------------------------------

    private sealed record PlantSnapshot(
        double SpotomsetningNok,
        double OppgjorNok,
        double UbalansekostNok,
        double TotalProductionMwh,
        double CaptureRate,
        double MerverdiNok,
        double NedetidstapNok,
        double KaiaKostnadNok,
        double ReddetAvVaktNok,
        double VaktKostAndelNok,
        // Sammendrag-felt (Spec MASTERPLAN-CODE-2026-05-22 § «Sammendrag
        // reuser /economy», 2026-05-22).
        double AvailabilityFactor,
        double AvailabilityFactorIeee,
        double ForcedOutageRate,
        double NedetidTimer,
        int AntallEvents,
        int AntallReddbareEvents,
        double GoodHoursPct,
        int ManglerImportHours);
}
