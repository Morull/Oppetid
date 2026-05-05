using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Idempotent backfill av kjente cause-koder. Drifts-leder kan endre disse
/// via /admin/kategorier — vi overstyrer ikke eksisterende rader, kun legger
/// til nye koder som mangler.
///
/// Code-listen kommer fra OperlogCsvParser, ScadaClassifier og
/// AnnotationOverlayService 2026-05-05. Nye koder kan legges til her ved
/// behov; system-defaults blir aldri slettet.
/// </summary>
public static class CauseAliasSeeder
{
    private const string OwnerOrgId = "dev-org";

    private static readonly (string CauseCode, string DisplayText)[] Defaults =
    [
        // Operlog-koder (fra OperlogCsvParser.MapEvent + DowntimeEventAggregator)
        ("operlog:nodstopp",        "Nødstopp utløst"),
        ("operlog:hurtigstopp",     "Hurtigstopp utløst"),
        ("operlog:fault",           "Feil/havari (operlog)"),
        ("operlog:start",           "Oppstart (operlog)"),
        ("operlog:stop",            "Manuell stopp (operlog)"),
        ("operlog:rist-falltap",    "Rist falltap"),
        ("operlog:turb-feil",       "Turbinfeil"),
        ("operlog:hoy-hoy-alarm",   "Høy-høy alarm"),
        ("operlog:lav-lav-alarm",   "Lav-lav alarm"),
        ("operlog:annen-alarm",     "Annen alarm"),
        ("operlog:manual_stop",     "Manuell stopp"),
        ("operlog:planned",         "Planlagt vedlikehold"),
        ("operlog:event",           "Hendelse uten kode"),

        // SCADA-tilstand-koder (fra ScadaClassifier)
        ("scada:com_alarm",         "Kommunikasjons-alarm"),
        ("scada:running",           "I drift"),
        ("scada:in_service",        "I drift"),
        ("scada:partial_derating",  "Delvis derating"),
        ("scada:strong_derating",   "Sterk derating"),
        ("scada:resource_unavailable", "Ressursmangel"),
        ("scada:committed_no_delivery", "Forpliktet uten levering"),
        ("scada:reserve_shutdown",  "Reserve-stopp"),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CauseAliasSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var existing = await db.CauseAliases
                .Select(x => x.CauseCode)
                .ToListAsync(ct).ConfigureAwait(false);
            var set = existing.ToHashSet(StringComparer.Ordinal);

            var added = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var (code, text) in Defaults)
            {
                if (set.Contains(code)) continue;
                db.CauseAliases.Add(new CauseAliasEntry
                {
                    CauseCode = code,
                    OwnerOrgId = OwnerOrgId,
                    DisplayText = text,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                added++;
            }
            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation("CauseAliasSeeder: la til {Count} default cause-aliaser.", added);
            }
            else
            {
                logger.LogDebug("CauseAliasSeeder: alle defaults eksisterer.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "CauseAliasSeeder feilet — UI vil falle tilbake til intern cause-kode.");
        }
    }
}
