using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Reporting;

/// <summary>
/// Rendrer en <see cref="UptimeReport"/> til .xlsx.
///
/// Rapporten består av:
/// <list type="bullet">
///   <item><b>Sammendrag</b>: periode, plant, state-tellinger, nøkkel-KPI-er</item>
///   <item><b>KPI-katalog</b>: alle KPI-er med verdi, enhet, basis, confidence, definisjon</item>
///   <item><b>3-veis-serie</b>: time-for-time Plan / Spotbud / Elhub – grunnlag for figur</item>
///   <item><b>Tilstandsfordeling</b>: hourly state + cause code + rationale for drill-down</item>
/// </list>
///
/// Renderer er registrert med <c>Format = "xlsx"</c> og velges av composition
/// root når forespørselen har samme format. Flere renderers kan registreres
/// parallelt (f.eks. HTML-renderer i en senere fase) uten å endre denne.
/// </summary>
public sealed class UptimeReportRenderer : IReportRenderer
{
    public string Format => "xlsx";

    [SuppressMessage("Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed",
        Justification = "The returned MemoryStream is owned by the caller, who is responsible for disposing it. " +
                        "The XLWorkbook is disposed before the Task is returned.")]
    public Task<Stream> RenderAsync(object report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report is not UptimeReport uptime)
        {
            throw new ArgumentException(
                $"Forventet UptimeReport, fikk {report.GetType().FullName}", nameof(report));
        }

        var memory = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            BuildSummarySheet(workbook, uptime);
            BuildKpiSheet(workbook, uptime);
            BuildThreeWaySheet(workbook, uptime);
            BuildHourlySheet(workbook, uptime);
            workbook.SaveAs(memory);
        }
        memory.Position = 0;
        return Task.FromResult<Stream>(memory);
    }

    // ------------------------------------------------------------------
    private static void BuildSummarySheet(XLWorkbook wb, UptimeReport r)
    {
        var s = wb.Worksheets.Add("Sammendrag");
        var row = 1;

        s.Cell(row, 1).Value = "Oppetidsanalyse";
        s.Cell(row, 1).Style.Font.Bold = true;
        s.Cell(row, 1).Style.Font.FontSize = 16;
        row += 2;

        s.Cell(row, 1).Value = "Anlegg:";
        s.Cell(row, 2).Value = r.PlantId;
        row++;

        s.Cell(row, 1).Value = "Periode:";
        s.Cell(row, 2).Value = ToLocalString(r.PeriodStartUtc) + " – " + ToLocalString(r.PeriodEndUtc);
        row++;

        s.Cell(row, 1).Value = "Timer totalt:";
        s.Cell(row, 2).Value = r.PeriodHours;
        row += 2;

        // Tilstandsfordeling
        s.Cell(row, 1).Value = "Tilstandsfordeling";
        s.Cell(row, 1).Style.Font.Bold = true;
        row++;
        s.Cell(row, 1).Value = "State";
        s.Cell(row, 2).Value = "Timer";
        s.Cell(row, 3).Value = "Andel";
        s.Range(row, 1, row, 3).Style.Font.Bold = true;
        row++;
        foreach (var (state, count) in r.StateCounts.OrderByDescending(kv => kv.Value))
        {
            s.Cell(row, 1).Value = state.ToString();
            s.Cell(row, 2).Value = count;
            s.Cell(row, 3).Value = r.PeriodHours == 0 ? 0 : (double)count / r.PeriodHours;
            s.Cell(row, 3).Style.NumberFormat.Format = "0.0%";
            row++;
        }
        row++;

        // Nøkkel-KPI-er — matcher den forenklede katalogen i UptimeKpiCalculator
        var highlights = new[]
        {
            "ServiceHours_SH", "ForcedOutageHours_FOH", "OutOfServiceHours",
            "AvailabilityFactor_AF", "BidDelivery", "BidVolume_MWh",
            "TotalProduction_MWh", "Spotomsetning_NOK",
            "Ubalansekost_NOK", "RkSalgVsSpot_NOK", "RkKjopVsSpot_NOK",
            "RkNetto_NOK", "Ubalanseresultat_NOK", "Oppgjor_NOK",
        };
        s.Cell(row, 1).Value = "Nøkkel-KPI-er";
        s.Cell(row, 1).Style.Font.Bold = true;
        row++;
        s.Cell(row, 1).Value = "KPI";
        s.Cell(row, 2).Value = "Verdi";
        s.Cell(row, 3).Value = "Enhet";
        s.Range(row, 1, row, 3).Style.Font.Bold = true;
        row++;
        foreach (var name in highlights)
        {
            var kpi = r.Kpis.FirstOrDefault(k => k.Name == name);
            if (kpi is null) continue;
            s.Cell(row, 1).Value = kpi.Name;
            if (kpi.Value.HasValue)
            {
                s.Cell(row, 2).Value = kpi.Value.Value;
                s.Cell(row, 2).Style.NumberFormat.Format = kpi.Unit switch
                {
                    "ratio" => "0.00%",
                    "MWh" => "0.00",
                    "NOK" => "#,##0",
                    "hours" => "0.00",
                    "events" => "0",
                    _ => "0.0000"
                };
            }
            else
            {
                s.Cell(row, 2).Value = "–";
            }
            s.Cell(row, 3).Value = kpi.Unit;
            row++;
        }

        s.Column(1).AdjustToContents();
        s.Column(2).AdjustToContents();
        s.Column(3).AdjustToContents();
    }

    private static void BuildKpiSheet(XLWorkbook wb, UptimeReport r)
    {
        var s = wb.Worksheets.Add("KPI-katalog");
        s.Cell(1, 1).Value = "KPI";
        s.Cell(1, 2).Value = "Verdi";
        s.Cell(1, 3).Value = "Enhet";
        s.Cell(1, 4).Value = "Timer-basis";
        s.Cell(1, 5).Value = "Confidence";
        s.Cell(1, 6).Value = "Kategori";
        s.Cell(1, 7).Value = "Definisjon";
        s.Range(1, 1, 1, 7).Style.Font.Bold = true;

        var row = 2;
        foreach (var k in r.Kpis)
        {
            s.Cell(row, 1).Value = k.Name;
            if (k.Value.HasValue)
            {
                s.Cell(row, 2).Value = k.Value.Value;
                if (k.Unit == "ratio")
                {
                    s.Cell(row, 2).Style.NumberFormat.Format = "0.0000%";
                }
                else
                {
                    s.Cell(row, 2).Style.NumberFormat.Format = "0.0000";
                }
            }
            else
            {
                s.Cell(row, 2).Value = "–";
            }
            s.Cell(row, 3).Value = k.Unit;
            s.Cell(row, 4).Value = k.HoursBasis;
            s.Cell(row, 5).Value = k.Confidence;
            s.Cell(row, 5).Style.NumberFormat.Format = "0.00";
            s.Cell(row, 6).Value = k.Category;
            s.Cell(row, 7).Value = k.Definition;
            row++;
        }

        s.Columns(1, 7).AdjustToContents();
    }

    private static void BuildThreeWaySheet(XLWorkbook wb, UptimeReport r)
    {
        var s = wb.Worksheets.Add("3-veis");
        s.Cell(1, 1).Value = "Merknad: Produksjonplan / Spotbud / Elhub per time. Brukes til visuell avviks­sammenligning.";
        s.Cell(1, 1).Style.Font.Italic = true;

        s.Cell(3, 1).Value = "Time (Europe/Oslo)";
        s.Cell(3, 2).Value = "Produksjonplan_MWh";
        s.Cell(3, 3).Value = "Spotbud_MWh";
        s.Cell(3, 4).Value = "MWh_Elhub";
        s.Cell(3, 5).Value = "Plan_minus_Elhub";
        s.Cell(3, 6).Value = "Bud_minus_Elhub";
        s.Range(3, 1, 3, 6).Style.Font.Bold = true;

        var row = 4;
        foreach (var c in r.Classified.OrderBy(x => x.TimeUtc))
        {
            s.Cell(row, 1).Value = ToLocalString(c.TimeUtc);
            if (c.Row.ProduksjonplanMwh.HasValue) s.Cell(row, 2).Value = c.Row.ProduksjonplanMwh.Value;
            if (c.Row.SpotbudMwh.HasValue) s.Cell(row, 3).Value = c.Row.SpotbudMwh.Value;
            if (c.Row.MwhElhub.HasValue) s.Cell(row, 4).Value = c.Row.MwhElhub.Value;

            var plan = c.Row.ProduksjonplanMwh ?? 0;
            var bud = c.Row.SpotbudMwh ?? 0;
            var elhub = c.Row.MwhElhub ?? 0;
            s.Cell(row, 5).Value = plan - elhub;
            s.Cell(row, 6).Value = bud - elhub;
            row++;
        }

        s.Columns(1, 6).AdjustToContents();
    }

    private static void BuildHourlySheet(XLWorkbook wb, UptimeReport r)
    {
        var s = wb.Worksheets.Add("Klassifisering");
        s.Cell(1, 1).Value = "Time";
        s.Cell(1, 2).Value = "State";
        s.Cell(1, 3).Value = "Confidence";
        s.Cell(1, 4).Value = "CauseCode";
        s.Cell(1, 5).Value = "MWh-Elhub";
        s.Cell(1, 6).Value = "Plan";
        s.Cell(1, 7).Value = "Spotpris";
        s.Cell(1, 8).Value = "Rationale";
        s.Range(1, 1, 1, 8).Style.Font.Bold = true;

        var row = 2;
        foreach (var c in r.Classified.OrderBy(x => x.TimeUtc))
        {
            s.Cell(row, 1).Value = ToLocalString(c.TimeUtc);
            s.Cell(row, 2).Value = c.State.ToString();
            s.Cell(row, 3).Value = c.Confidence;
            s.Cell(row, 3).Style.NumberFormat.Format = "0.00";
            s.Cell(row, 4).Value = c.CauseCode;
            if (c.MwhElhub.HasValue) s.Cell(row, 5).Value = c.MwhElhub.Value;
            if (c.ProduksjonplanMwh.HasValue) s.Cell(row, 6).Value = c.ProduksjonplanMwh.Value;
            if (c.SpotprisNokMwh.HasValue) s.Cell(row, 7).Value = c.SpotprisNokMwh.Value;
            s.Cell(row, 8).Value = c.Rationale;
            row++;
        }

        s.Columns(1, 8).AdjustToContents();
    }

    private static string ToLocalString(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, TimeZones.Norway);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
