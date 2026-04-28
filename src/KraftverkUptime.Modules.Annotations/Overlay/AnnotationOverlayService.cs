using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Annotations.Repositories;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Classification.Kpi;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Annotations.Overlay;

/// <summary>
/// Read-time merge: tar en lagret <see cref="UptimeReport"/> og en sekvens
/// med manuelle annoteringer, og returnerer en ny rapport hvor annoterte
/// timer er overstyrt og KPI-er rekonseptualisert.
///
/// Algoritme:
///   1. For hver <see cref="ClassifiedHourlyRow"/>: hvis en annotering dekker
///      timens StartUtc, overstyr State + CauseCode + Confidence + Rationale.
///   2. Re-kjør <see cref="UptimeKpiCalculator"/> på det justerte settet slik
///      at StateCounts og hele KPI-katalogen reflekterer overlayet.
///
/// Hvis ingen annoteringer overlapper rapportens periode, returneres
/// originalen uendret (referanselikhet) — en gratis fast path.
/// </summary>
public sealed class AnnotationOverlayService
{
    private readonly UptimeKpiCalculator _kpiCalculator;
    private readonly ILogger<AnnotationOverlayService> _logger;

    public AnnotationOverlayService(
        UptimeKpiCalculator kpiCalculator,
        ILogger<AnnotationOverlayService> logger)
    {
        _kpiCalculator = kpiCalculator;
        _logger = logger;
    }

    /// <summary>
    /// Anvender annoteringer på en rapport. Caller har ansvar for å laste
    /// både annoteringer og kategorier. Bruk <see cref="ApplyAsync"/> hvis
    /// du heller vil at tjenesten skal hente fra DB.
    /// </summary>
    public UptimeReport Apply(
        UptimeReport report,
        IReadOnlyList<DowntimeAnnotation> annotations,
        IReadOnlyDictionary<string, DowntimeCategory> categoryLookup)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(categoryLookup);

        if (annotations.Count == 0 || report.Classified.Count == 0)
        {
            return report;
        }

        // Filtrer til kun annoteringer som faktisk overlapper rapportens periode
        // og som har gyldig kategori.
        var relevant = annotations
            .Where(a => a.PlantId == report.PlantId
                && a.StartUtc < report.PeriodEndUtc.AddHours(1)
                && a.EndUtc > report.PeriodStartUtc
                && categoryLookup.ContainsKey(a.CategoryId))
            .ToList();

        if (relevant.Count == 0)
        {
            return report;
        }

        var overlaidRows = new List<ClassifiedHourlyRow>(report.Classified.Count);
        var overrideCount = 0;

        foreach (var row in report.Classified)
        {
            var hourStart = row.TimeUtc;
            var hourEnd = hourStart.AddHours(1);

            DowntimeAnnotation? cover = null;
            foreach (var annotation in relevant)
            {
                if (annotation.StartUtc <= hourStart && annotation.EndUtc >= hourEnd)
                {
                    cover = annotation;
                    break;
                }
            }

            if (cover is null)
            {
                overlaidRows.Add(row);
                continue;
            }

            var category = categoryLookup[cover.CategoryId];
            overrideCount++;

            overlaidRows.Add(row with
            {
                State = category.UnitStateOverride,
                CauseCode = $"annotation:{category.Id}",
                Confidence = 1.0,
                Rationale = string.IsNullOrWhiteSpace(cover.Comment)
                    ? $"Manuell annotering: {category.DisplayName}"
                    : $"Manuell annotering: {category.DisplayName} — {cover.Comment}"
            });
        }

        if (overrideCount == 0)
        {
            return report;
        }

        // Vi har bare PlantId fra rapporten — Compute leser kun PlantId fra
        // PlantClassificationConfig, så vi konstruerer en minimal config her.
        var minimalConfig = new PlantClassificationConfig
        {
            PlantId = report.PlantId,
            PlantType = PlantType.RunOfRiver,
            NominalPowerMw = 0.0
        };

        var newReport = _kpiCalculator.Compute(overlaidRows, minimalConfig);

        _logger.LogInformation(
            "Annotation overlay applied: plant {PlantId}, {Overridden}/{Total} hours overridden by {AnnotationCount} annotations.",
            report.PlantId, overrideCount, report.Classified.Count, relevant.Count);

        return newReport with
        {
            PeriodStartUtc = report.PeriodStartUtc,
            PeriodEndUtc = report.PeriodEndUtc,
            PeriodHours = report.PeriodHours
        };
    }

    /// <summary>Async-hjelper som henter annoteringer + kategorier fra DB og kaller <see cref="Apply"/>.</summary>
    public async Task<UptimeReport> ApplyAsync(
        UptimeReport report,
        IDowntimeAnnotationRepository annotationRepo,
        IDowntimeCategoryRepository categoryRepo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(annotationRepo);
        ArgumentNullException.ThrowIfNull(categoryRepo);

        var annotations = await annotationRepo
            .ListAsync(report.PlantId, report.PeriodStartUtc, report.PeriodEndUtc.AddHours(1), ct)
            .ConfigureAwait(false);
        if (annotations.Count == 0)
        {
            return report;
        }

        var categories = await categoryRepo.ListAllAsync(ct).ConfigureAwait(false);
        var lookup = categories.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);

        return Apply(report, annotations, lookup);
    }
}
