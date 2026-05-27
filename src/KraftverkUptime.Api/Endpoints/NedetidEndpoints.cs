using System.Globalization;
using System.Text;
using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Contracts;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Endepunkter for nedetids-analyse og vakt-ROI:
///
///   GET /api/v1/plants/{plantId}/nedetid?from=&amp;to=&amp;format=json|csv
///   GET /api/v1/plants/{plantId}/vakt-roi?from=&amp;to=&amp;format=json|csv
///
/// Begge er anonyme i v1 (samme nivå som settlements). Bruker felles
/// <see cref="INedetidQueryService"/> for event-aggregering, og
/// <see cref="VaktRoiCalculator"/> for counterfactual-analysen.
/// </summary>
public static class NedetidEndpoints
{
    public static IEndpointRouteBuilder MapNedetidV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}")
            .WithTags("Nedetid")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/nedetid", GetNedetidAsync)
            .WithName("GetNedetid")
            .WithSummary("Henter aggregerte nedetids-events for et anlegg i gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<NedetidResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/vakt-roi", GetVaktRoiAsync)
            .WithName("GetVaktRoi")
            .WithSummary("Beregner Vakt-ROI: hvor mye produksjons-tap reddet vakten i perioden.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<VaktRoiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetNedetidAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? format,
        INedetidQueryService nedetid,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }

        var plant = await queryContext.Apply(db.Plants.AsQueryable())
            .FirstOrDefaultAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (plant is null)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant {plantId} eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var events = await nedetid.ListEventsAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        var response = BuildNedetidResponse(plantId, fromUtc, toUtc, events);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Results.File(
                fileContents: Encoding.UTF8.GetBytes(BuildNedetidCsv(response)),
                contentType: "text/csv",
                fileDownloadName: $"{plantId}-nedetid-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.csv");
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> GetVaktRoiAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? format,
        string? vaktStartLokal,
        string? vaktSluttLokal,
        string? oppmoteLokal,
        INedetidQueryService nedetid,
        IOverflowQueryService overflow,
        VaktRoiCalculator calculator,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }

        if (!TryParseVaktOptions(vaktStartLokal, vaktSluttLokal, oppmoteLokal,
            out var vaktOptions, out var vaktProblem))
        {
            return vaktProblem!;
        }

        var plant = await queryContext.Apply(db.Plants.AsQueryable())
            .FirstOrDefaultAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (plant is null)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        var events = await nedetid.ListEventsAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);

        // Beregn snitt-spotpris fra klassifiserte rader vi alt har lest. For å
        // unngå dobbel-spørring, bruker vi en enkel proxy: sum tap_nok / sum tap_mwh
        // over events. Hvis tap_mwh = 0 (alle events har plan=0), faller vi tilbake til
        // 500 NOK/MWh som forsiktig anslag.
        double snittSpot = 500;
        var sumTapMwh = events.Sum(e => e.TapMwh);
        var sumTapNok = events.Sum(e => e.TapNok);
        if (sumTapMwh > 0 && sumTapNok > 0)
        {
            snittSpot = sumTapNok / sumTapMwh;
        }

        // Overløps-justering: vakt-ROI gjelder kun timer der det var overløp i
        // magasinet. Hent settet av overløps-timer + data-coverage for hele
        // perioden i én spørring; calculator filtrerer per event mot settet.
        // DataAvailable=false (tag mangler eller ingen samples i perioden)
        // gir konservativt ROI=0 + flagger eventene som missing data.
        var dataset = await overflow.GetOverflowDatasetAsync(plantId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        // Ubalanse-komponent (v3): gjennomsnittlig RK-spot-spread for perioden.
        // Vakt-ROI redder ubalanse-gebyret i counterfactual-vinduet uavhengig
        // av magasinstand — så lenge producent var Spotbud-forpliktet.
        var snittUbalansetillegg = await nedetid
            .GetAvgImbalancePremiumAsync(plantId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        // Produksjonsplan-per-time fra Hydrogrid-plan (settlement-import).
        // Tjenesten utvider vinduet bakover (4 uker proxy) og fremover
        // (3 dager counterfactual-buffer) automatisk.
        var planResult = await nedetid
            .GetProduksjonplanByHourAsync(plantId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        // Manuelle overrides for vakt-events i perioden — drifts-leder kan
        // tvinge "HaddeOverlop"/"IkkeOverlop"-klassifisering OG/eller korrigere
        // faktisk slutt-tidspunkt når SCADA/operlog er feil. Henter hele raden
        // siden vi trenger begge feltene.
        var overrideRows = await db.VaktEventOverrides
            .Where(o => o.PlantId == plantId
                && o.EventStartUtc >= fromUtc
                && o.EventStartUtc < toUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Anvend ActualEndOverrideUtc OPPSTRØMS for VaktRoiCalculator: bytt ut
        // EndUtc på matchende events før ROI-beregning. Calculator forblir en
        // ren funksjon av events — ingen ny parameter trengs. Records er
        // immutable, så vi bygger en ny liste med 'with'-syntaks.
        if (overrideRows.Any(o => o.ActualEndOverrideUtc.HasValue))
        {
            var endOverrides = overrideRows
                .Where(o => o.ActualEndOverrideUtc.HasValue)
                .ToDictionary(o => o.EventStartUtc, o => o.ActualEndOverrideUtc!.Value);

            events = events
                .Select(e => endOverrides.TryGetValue(e.StartUtc, out var endOv)
                    ? e with { EndUtc = endOv }
                    : e)
                .ToList();
        }

        // Klassifiserings-dict (kun ikke-Auto) til Calculator.
        var overrides = overrideRows
            .Where(o => o.Classification != "Auto")
            .ToDictionary(o => o.EventStartUtc, o => o.Classification);

        // U2-PlanDeviation-filter (spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md,
        // 2026-05-22): bygg settet av events hvor EffectiveGuardResponse == false.
        // Disse passeres til Calculator som excludeFromReddbar slik at de blir
        // klassifisert som IkkeReddbar (ingen ROI), men fortsatt teller som outage-
        // tid. Logikken sitter i Core.Domain.EffectiveGuardResponseEvaluator —
        // kalkulatoren forblir en ren funksjon av events + plan-data.
        var guardOverridesByEventStart = overrideRows
            .ToDictionary(o => o.EventStartUtc, o => o.GuardResponseOverride);
        var excludeFromReddbar = events
            .Where(e =>
            {
                var ovr = guardOverridesByEventStart.TryGetValue(e.StartUtc, out var g)
                    ? (GuardResponseOverride?)g
                    : null;
                return !EffectiveGuardResponseEvaluator.ShouldCount(e, ovr);
            })
            .Select(e => e.StartUtc)
            .ToHashSet();

        var roi = calculator.Calculate(
            events, snittSpot, planResult.PlanByHour,
            dataset.OverflowHours, overflowDataAvailable: dataset.DataAvailable,
            snittUbalansetillegg_NokMwh: snittUbalansetillegg,
            overrides: overrides,
            proxyHours: planResult.ProxyHours,
            vaktOptions: vaktOptions,
            excludeFromReddbar: excludeFromReddbar);
        var effectiveVakt = vaktOptions ?? VaktTidsmodellOptions.Default;
        var response = BuildVaktRoiResponse(plantId, fromUtc, toUtc, plant.InstalledCapacityMw,
            snittSpot, snittUbalansetillegg, effectiveVakt, roi, guardOverridesByEventStart);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Results.File(
                fileContents: Encoding.UTF8.GetBytes(BuildVaktRoiCsv(response)),
                contentType: "text/csv",
                fileDownloadName: $"{plantId}-vakt-roi-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.csv");
        }

        return Results.Ok(response);
    }

    // ---- helpers --------------------------------------------------------------

    private static bool TryValidatePeriod(
        DateTimeOffset? from, DateTimeOffset? to,
        out DateTimeOffset fromUtc, out DateTimeOffset toUtc,
        out IResult? problem)
    {
        fromUtc = default;
        toUtc = default;
        problem = null;

        if (!from.HasValue || !to.HasValue)
        {
            problem = Results.Problem(
                title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        fromUtc = from.Value.ToUniversalTime();
        toUtc = to.Value.ToUniversalTime();

        if (toUtc <= fromUtc)
        {
            problem = Results.Problem(
                title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        // Maks 2 år for å unngå at noen ber om all data ved et uhell.
        if ((toUtc - fromUtc).TotalDays > 730)
        {
            problem = Results.Problem(
                title: "Periode for lang",
                detail: "Maks 2 år per spørring (730 dager).",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        return true;
    }

    private static NedetidResponse BuildNedetidResponse(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        IReadOnlyList<DowntimeEvent> events)
    {
        var dtos = events.Select(MapEvent).ToList();

        var grouped = events
            .GroupBy(e => e.Category)
            .Select(g => new NedetidKategoriSummary(
                Kategori: g.Key.ToString(),
                Antall: g.Count(),
                TotalTimer: g.Sum(e => e.VarighetTimer),
                TotalTapNok: g.Sum(e => e.TapNok)))
            .OrderByDescending(s => s.TotalTapNok)
            .ToList();

        return new NedetidResponse(
            PlantId: plantId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            AntallEvents: events.Count,
            TotalNedetidTimer: events.Sum(e => e.VarighetTimer),
            TotalTapMwh: events.Sum(e => e.TapMwh),
            TotalTapNok: events.Sum(e => e.TapNok),
            KategoriSummaries: grouped,
            Events: dtos);
    }

    private static VaktRoiResponse BuildVaktRoiResponse(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        double effektMw, double snittSpot,
        double snittUbalansetillegg,
        VaktTidsmodellOptions vaktOptions,
        IReadOnlyList<VaktRoiResultat> roi,
        IReadOnlyDictionary<DateTimeOffset, GuardResponseOverride> guardOverrides)
    {
        var dtos = roi.Select(r => new VaktRoiEventDto(
            Event: MapEvent(r.Event),
            ErInnenforVakt: r.ErInnenforVakt,
            ErReddbar: r.ErReddbar,
            CounterfactualEndUtc: r.CounterfactualEndUtc,
            EkstraTimerSpart: r.EkstraTimerSpart,
            ReddetMwh: r.ReddetMwh,
            ReddetNok: r.ReddetNok,
            ReddetProduksjon_NOK: r.ReddetProduksjon_NOK,
            ReddetUbalanse_NOK: r.ReddetUbalanse_NOK,
            OverflowTimerInCounterfactual: r.OverflowTimerInCounterfactual,
            OverflowDataMissing: r.OverflowDataMissing,
            PlanDataPartial: r.PlanDataPartial,
            Forklaring: r.Forklaring,
            GuardResponseOverride: guardOverrides.TryGetValue(r.Event.StartUtc, out var g)
                ? g : GuardResponseOverride.Auto)).ToList();

        var reddbareInnenfor = roi.Count(r => r.ErInnenforVakt && r.ErReddbar);
        var totalReddetMwh = roi.Sum(r => r.ReddetMwh);
        var totalReddetNok = roi.Sum(r => r.ReddetNok);
        var totalReddetProduksjon = roi.Sum(r => r.ReddetProduksjon_NOK);
        var totalReddetUbalanse = roi.Sum(r => r.ReddetUbalanse_NOK);
        var snittEkstra = reddbareInnenfor == 0 ? 0
            : roi.Where(r => r.ErInnenforVakt && r.ErReddbar).Average(r => r.EkstraTimerSpart);

        return new VaktRoiResponse(
            PlantId: plantId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            InstallertEffektMw: effektMw,
            SnittSpotprisNokMwh: snittSpot,
            SnittUbalansetilleggNokMwh: snittUbalansetillegg,
            VaktStartLokal: FormatHm(vaktOptions.EttermiddagStart),
            VaktSluttLokal: FormatHm(vaktOptions.MorgenCutoff),
            OppmoteLokal: FormatHm(vaktOptions.OppmoteTidspunkt),
            AntallEventsTotalt: roi.Count,
            AntallReddbareInnenforVakt: reddbareInnenfor,
            TotalReddetMwh: totalReddetMwh,
            TotalReddetNok: totalReddetNok,
            TotalReddetProduksjon_NOK: totalReddetProduksjon,
            TotalReddetUbalanse_NOK: totalReddetUbalanse,
            SnittEkstraTimerPerEvent: snittEkstra,
            Events: dtos);
    }

    private static string FormatHm(TimeSpan ts) =>
        $"{ts.Hours:D2}:{ts.Minutes:D2}";

    /// <summary>
    /// Parser de tre vakt-vindu-query-paramene til <see cref="VaktTidsmodellOptions"/>.
    /// Hvis ingen er satt returneres null = bruk default (15:00/07:00/08:00).
    /// Krever HH:mm-format. Returnerer 400 Bad Request via <paramref name="problem"/>
    /// hvis én er ugyldig.
    /// </summary>
    private static bool TryParseVaktOptions(
        string? vaktStartLokal, string? vaktSluttLokal, string? oppmoteLokal,
        out VaktTidsmodellOptions? options, out IResult? problem)
    {
        options = null;
        problem = null;

        if (vaktStartLokal is null && vaktSluttLokal is null && oppmoteLokal is null)
        {
            return true; // ingen overstyring → null = default
        }

        var def = VaktTidsmodellOptions.Default;
        if (!TryParseHm(vaktStartLokal, def.EttermiddagStart, out var start)
            || !TryParseHm(vaktSluttLokal, def.MorgenCutoff, out var slutt)
            || !TryParseHm(oppmoteLokal, def.OppmoteTidspunkt, out var oppmote))
        {
            problem = Results.Problem(
                title: "Ugyldig vakt-vindu",
                detail: "Forventer HH:mm-format (eks. 15:00, 07:00, 08:00).",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        options = def with
        {
            EttermiddagStart = start,
            MorgenCutoff = slutt,
            OppmoteTidspunkt = oppmote,
        };
        return true;
    }

    private static bool TryParseHm(string? raw, TimeSpan fallback, out TimeSpan parsed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = fallback;
            return true;
        }
        if (TimeSpan.TryParseExact(raw.Trim(), [@"hh\:mm", @"h\:mm"],
            CultureInfo.InvariantCulture, out var ts))
        {
            parsed = ts;
            return true;
        }
        parsed = default;
        return false;
    }

    private static NedetidEventDto MapEvent(DowntimeEvent e) => new(
        PlantId: e.PlantId,
        StartUtc: e.StartUtc,
        EndUtc: e.EndUtc,
        VarighetTimer: e.VarighetTimer,
        State: e.State.ToString(),
        Kategori: e.Category.ToString(),
        CauseCode: e.CauseCode,
        TapMwh: e.TapMwh,
        TapNok: e.TapNok,
        TimerSettlement: e.TimerSettlement,
        HarOperlogMatch: e.HarOperlogMatch,
        Rationale: e.Rationale);

    private static string BuildNedetidCsv(NedetidResponse r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("start_utc;end_utc;varighet_t;state;kategori;cause_code;tap_mwh;tap_nok;timer_settlement;har_operlog;rationale");
        foreach (var e in r.Events)
        {
            sb.Append(e.StartUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(e.EndUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(e.VarighetTimer.ToString("F2", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(e.State).Append(';');
            sb.Append(e.Kategori).Append(';');
            sb.Append(EscapeCsv(e.CauseCode)).Append(';');
            sb.Append(e.TapMwh.ToString("F3", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(e.TapNok.ToString("F0", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(e.TimerSettlement).Append(';');
            sb.Append(e.HarOperlogMatch ? "true" : "false").Append(';');
            sb.AppendLine(EscapeCsv(e.Rationale));
        }
        return sb.ToString();
    }

    private static string BuildVaktRoiCsv(VaktRoiResponse r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("start_utc;end_utc;varighet_t;state;kategori;cause_code;innenfor_vakt;reddbar;counterfactual_end;ekstra_timer;overflow_timer;overflow_data_missing;reddet_mwh;reddet_produksjon_nok;reddet_ubalanse_nok;reddet_nok;forklaring");
        foreach (var x in r.Events)
        {
            sb.Append(x.Event.StartUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.Event.EndUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.Event.VarighetTimer.ToString("F2", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.Event.State).Append(';');
            sb.Append(x.Event.Kategori).Append(';');
            sb.Append(EscapeCsv(x.Event.CauseCode)).Append(';');
            sb.Append(x.ErInnenforVakt ? "true" : "false").Append(';');
            sb.Append(x.ErReddbar ? "true" : "false").Append(';');
            sb.Append(x.CounterfactualEndUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) ?? "").Append(';');
            sb.Append(x.EkstraTimerSpart.ToString("F2", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.OverflowTimerInCounterfactual).Append(';');
            sb.Append(x.OverflowDataMissing ? "true" : "false").Append(';');
            sb.Append(x.ReddetMwh.ToString("F2", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.ReddetProduksjon_NOK.ToString("F0", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.ReddetUbalanse_NOK.ToString("F0", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(x.ReddetNok.ToString("F0", CultureInfo.InvariantCulture)).Append(';');
            sb.AppendLine(EscapeCsv(x.Forklaring));
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
