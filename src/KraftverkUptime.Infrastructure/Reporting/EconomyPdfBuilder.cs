using System.Globalization;
using KraftverkUptime.Modules.Reporting.Economy;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Bygger Økonomi-rapport-PDF via QuestPDF. Tar et ferdig
/// <see cref="EconomyReportDto"/> + valgfrie 12-måneders trend-bøtter for
/// Spotomsetning og Oppgjør, og returnerer ferdig PDF som byte[].
///
/// Side-rekkefølge (spec NESTE-CHAT-OKONOMI-FANE-PDF.md del 6):
///   1. Forside
///   2. Sammendrag — 9 KPI-kort gruppert i 3 seksjoner
///   3. Måneds-trend — bardiagram for Spotomsetning og Oppgjør
///   4. Per anlegg — kun ved multi-anleggs-utvalg
/// Vedlegg-siden (nedetid + KAIA-breakdown) ligger ikke inne i v1; den
/// krever ekstra dataaggregering og kan komme som en utvidelse.
/// </summary>
public static class EconomyPdfBuilder
{
    private static readonly NumberFormatInfo NorskTall = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 },
    };

    public static byte[] Build(
        EconomyReportDto report,
        IReadOnlyList<EconomyChartRenderer.MonthBucket> spotTrend,
        IReadOnlyList<EconomyChartRenderer.MonthBucket> oppgjorTrend,
        IReadOnlyDictionary<string, string> plantNamesById,
        int totalPlantsInPortefolje,
        DateTimeOffset generatedAtUtc)
    {
        // Settings registreres normalt i Program.cs, men idempotent her så
        // testkjøring og isolerte kall fungerer uten DI-bootstrapping.
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(doc =>
        {
            doc.Page(page => ComposeFrontPage(page, report, plantNamesById, totalPlantsInPortefolje, generatedAtUtc));
            doc.Page(page => ComposeSummaryPage(page, report));
            doc.Page(page => ComposeTrendPage(page, spotTrend, oppgjorTrend));
            if (report.PerPlant.Length > 1)
            {
                doc.Page(page => ComposePerPlantPage(page, report));
            }
        }).GeneratePdf();
    }

    // ---------------------------------------------------------------------
    // Sider
    // ---------------------------------------------------------------------

    private static void ComposeFrontPage(
        PageDescriptor page,
        EconomyReportDto report,
        IReadOnlyDictionary<string, string> plantNamesById,
        int totalPlantsInPortefolje,
        DateTimeOffset generatedAtUtc)
    {
        ApplyPageDefaults(page);

        page.Content().Column(col =>
        {
            col.Spacing(24);

            col.Item().PaddingTop(80).Text("Dalane Kraft").FontSize(14).FontColor(EconomyPdfTheme.TextMuted);
            col.Item().Text("Økonomi-rapport").FontSize(36).Bold().FontColor(EconomyPdfTheme.TextPrimary);

            col.Item().PaddingTop(40).Column(meta =>
            {
                meta.Spacing(8);
                meta.Item().Text(t =>
                {
                    t.Span("Periode: ").FontColor(EconomyPdfTheme.TextMuted);
                    t.Span(FormatPeriode(report.From, report.To)).FontColor(EconomyPdfTheme.TextPrimary).Bold();
                });
                meta.Item().Text(t =>
                {
                    t.Span("Anlegg: ").FontColor(EconomyPdfTheme.TextMuted);
                    t.Span(FormatScope(report.PlantIds, plantNamesById, totalPlantsInPortefolje))
                        .FontColor(EconomyPdfTheme.TextPrimary).Bold();
                });
                meta.Item().Text(t =>
                {
                    t.Span("Sammenligning vs: ").FontColor(EconomyPdfTheme.TextMuted);
                    t.Span(FormatPeriode(report.PrevFrom, report.PrevTo)).FontColor(EconomyPdfTheme.TextPrimary);
                });
                meta.Item().PaddingTop(12).Text(t =>
                {
                    t.Span("Generert: ").FontColor(EconomyPdfTheme.TextMuted);
                    t.Span(generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture))
                        .FontColor(EconomyPdfTheme.TextPrimary);
                });
            });
        });

        ComposeFooter(page);
    }

    private static void ComposeSummaryPage(PageDescriptor page, EconomyReportDto report)
    {
        ApplyPageDefaults(page);
        ComposeHeader(page, "Sammendrag");

        page.Content().Column(col =>
        {
            col.Spacing(18);
            col.Item().Element(c => ComposeKpiGroup(c, report.Inntekter));
            col.Item().Element(c => ComposeKpiGroup(c, report.Kostnader));
            col.Item().Element(c => ComposeKpiGroup(c, report.Resultat));
        });

        ComposeFooter(page);
    }

    private static void ComposeTrendPage(
        PageDescriptor page,
        IReadOnlyList<EconomyChartRenderer.MonthBucket> spotTrend,
        IReadOnlyList<EconomyChartRenderer.MonthBucket> oppgjorTrend)
    {
        ApplyPageDefaults(page);
        ComposeHeader(page, "Måneds-trend (12 måneder)");

        var spotPng = EconomyChartRenderer.RenderBarChart(
            "Spotomsetning per måned", spotTrend, EconomyPdfTheme.AccentGood);
        var oppgjorPng = EconomyChartRenderer.RenderBarChart(
            "Oppgjør per måned", oppgjorTrend, EconomyPdfTheme.AccentNeutral);

        page.Content().Column(col =>
        {
            col.Spacing(16);
            col.Item().Image(spotPng).FitWidth();
            col.Item().Image(oppgjorPng).FitWidth();
        });

        ComposeFooter(page);
    }

    private static void ComposePerPlantPage(PageDescriptor page, EconomyReportDto report)
    {
        ApplyPageDefaults(page);
        ComposeHeader(page, "Per anlegg");

        page.Content().Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);    // Anlegg
                c.RelativeColumn(1.4f); // Oppgjør
                c.RelativeColumn(1.4f); // Spotomsetning
                c.RelativeColumn(1.4f); // Ubalansekost
                c.RelativeColumn(1.4f); // KAIA
                c.RelativeColumn(1.0f); // CR
            });

            table.Header(h =>
            {
                HeaderCell(h, "Anlegg", alignLeft: true);
                HeaderCell(h, "Oppgjør (NOK)");
                HeaderCell(h, "Spotomsetning (NOK)");
                HeaderCell(h, "Ubalansekost (NOK)");
                HeaderCell(h, "KAIA (NOK)");
                HeaderCell(h, "Capture rate");
            });

            foreach (var row in report.PerPlant.OrderByDescending(p => p.OppgjorNok))
            {
                BodyCell(table, row.PlantName, alignLeft: true);
                BodyCell(table, FormatNok(row.OppgjorNok));
                BodyCell(table, FormatNok(row.SpotomsetningNok));
                BodyCell(table, FormatNok(row.UbalansekostNok));
                BodyCell(table, FormatNok(row.KaiaKostnadNok));
                BodyCell(table, FormatRatio(row.CaptureRate));
            }
        });

        ComposeFooter(page);
    }

    // ---------------------------------------------------------------------
    // KPI-gruppe-rendering (3 kort per rad)
    // ---------------------------------------------------------------------

    private static void ComposeKpiGroup(IContainer container, EconomyKpiGroupDto group)
    {
        container.Column(col =>
        {
            col.Spacing(8);
            col.Item().Text(group.Title).FontSize(14).Bold().FontColor(EconomyPdfTheme.TextPrimary);
            col.Item().Row(row =>
            {
                foreach (var kpi in group.Kpis)
                {
                    row.RelativeItem().Padding(4).Element(c => ComposeKpiCard(c, kpi));
                }
            });
        });
    }

    private static void ComposeKpiCard(IContainer container, EconomyKpiDto kpi)
    {
        container
            .Background(EconomyPdfTheme.Surface)
            .BorderLeft(3).BorderColor(TrendBorderColor(kpi))
            .Padding(12)
            .Column(col =>
            {
                col.Spacing(4);
                col.Item().Text(kpi.Label).FontSize(9).FontColor(EconomyPdfTheme.TextMuted);
                col.Item().Text(FormatVerdi(kpi)).FontSize(18).Bold().FontColor(EconomyPdfTheme.TextPrimary);

                if (kpi.EndringProsent.HasValue)
                {
                    var isGood = IsGoodChange(kpi);
                    var arrow = kpi.EndringProsent.Value >= 0 ? "▲" : "▼";
                    var pct = (kpi.EndringProsent.Value * 100).ToString("F1", NorskTall);
                    col.Item().Text(t =>
                    {
                        t.Span($"{arrow} {pct} %")
                            .FontSize(10).Bold()
                            .FontColor(isGood ? EconomyPdfTheme.AccentGood : EconomyPdfTheme.AccentBad);
                        t.Span(" vs forrige").FontSize(10).FontColor(EconomyPdfTheme.TextMuted);
                    });
                }
                else
                {
                    col.Item().Text("– ingen sammenligning").FontSize(10).FontColor(EconomyPdfTheme.TextSubtle);
                }
            });
    }

    private static string TrendBorderColor(EconomyKpiDto kpi)
    {
        if (!kpi.EndringProsent.HasValue) return EconomyPdfTheme.Border;
        return IsGoodChange(kpi) ? EconomyPdfTheme.AccentGood : EconomyPdfTheme.AccentBad;
    }

    private static bool IsGoodChange(EconomyKpiDto kpi)
    {
        if (!kpi.EndringProsent.HasValue) return true;
        var up = kpi.EndringProsent.Value >= 0;
        var upIsGood = string.Equals(kpi.GoodDirection, "up", StringComparison.OrdinalIgnoreCase);
        return up == upIsGood;
    }

    // ---------------------------------------------------------------------
    // Tabell-celler — bruker QuestPDF sin TableCellDescriptor som er
    // delegat-parameter både i Header() og når man kaller table.Cell().
    // ---------------------------------------------------------------------

    private static void HeaderCell(TableCellDescriptor header, string label, bool alignLeft = false)
    {
        var cell = header.Cell()
            .Background(EconomyPdfTheme.SurfaceAlt)
            .Padding(6);
        var aligned = alignLeft ? cell : cell.AlignRight();
        aligned.Text(label).FontSize(10).Bold().FontColor(EconomyPdfTheme.TextPrimary);
    }

    private static void BodyCell(TableDescriptor table, string value, bool alignLeft = false)
    {
        var cell = table.Cell()
            .Padding(6)
            .BorderBottom(0.5f).BorderColor(EconomyPdfTheme.Border);
        var aligned = alignLeft ? cell : cell.AlignRight();
        aligned.Text(value).FontSize(10).FontColor(EconomyPdfTheme.TextPrimary);
    }

    // ---------------------------------------------------------------------
    // Felles side-elementer
    // ---------------------------------------------------------------------

    private static void ApplyPageDefaults(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.MarginVertical(36);
        page.MarginHorizontal(36);
        page.PageColor(EconomyPdfTheme.Background);
        page.DefaultTextStyle(t => t.FontFamily(EconomyPdfTheme.FontFamily).FontColor(EconomyPdfTheme.TextPrimary));
    }

    private static void ComposeHeader(PageDescriptor page, string title)
    {
        page.Header().PaddingBottom(12).Row(row =>
        {
            row.RelativeItem().Text(title).FontSize(18).Bold().FontColor(EconomyPdfTheme.TextPrimary);
            row.ConstantItem(140).AlignRight().Text("Dalane Kraft · Økonomi").FontSize(9).FontColor(EconomyPdfTheme.TextMuted);
        });
    }

    private static void ComposeFooter(PageDescriptor page)
    {
        page.Footer().PaddingTop(12).Row(row =>
        {
            row.RelativeItem().Text("Dalane Kraft").FontSize(9).FontColor(EconomyPdfTheme.TextSubtle);
            row.ConstantItem(100).AlignRight().Text(t =>
            {
                t.Span("Side ").FontSize(9).FontColor(EconomyPdfTheme.TextSubtle);
                t.CurrentPageNumber().FontSize(9).FontColor(EconomyPdfTheme.TextSubtle);
                t.Span(" / ").FontSize(9).FontColor(EconomyPdfTheme.TextSubtle);
                t.TotalPages().FontSize(9).FontColor(EconomyPdfTheme.TextSubtle);
            });
        });
    }

    // ---------------------------------------------------------------------
    // Formatering — speil av Web/Services/NumberFormat.cs men minimal her
    // siden Infrastructure-prosjektet ikke skal avhenge av Web.
    // ---------------------------------------------------------------------

    private static string FormatVerdi(EconomyKpiDto kpi) => kpi.Enhet switch
    {
        EconomyKpiUnit.Nok => $"{FormatNok(kpi.Verdi)} NOK",
        EconomyKpiUnit.Ratio => FormatRatio(kpi.Verdi),
        EconomyKpiUnit.Mwh => $"{kpi.Verdi.ToString("F1", NorskTall)} MWh",
        _ => FormatNok(kpi.Verdi),
    };

    private static string FormatNok(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "–" : v.ToString("N0", NorskTall);

    private static string FormatRatio(double v) =>
        v == 0 ? "–" : (v * 100).ToString("F1", NorskTall) + " %";

    private static string FormatPeriode(DateTimeOffset from, DateTimeOffset to) =>
        $"{from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} – {to.AddSeconds(-1).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

    private static string FormatScope(
        IReadOnlyList<string> plantIds,
        IReadOnlyDictionary<string, string> plantNamesById,
        int totalPlantsInPortefolje)
    {
        if (plantIds.Count == 0) return "–";
        if (plantIds.Count == totalPlantsInPortefolje)
        {
            return $"Hele porteføljen ({totalPlantsInPortefolje} anlegg)";
        }
        if (plantIds.Count == 1)
        {
            return plantNamesById.TryGetValue(plantIds[0], out var name) ? name : plantIds[0];
        }
        var names = plantIds
            .Select(id => plantNamesById.TryGetValue(id, out var n) ? n : id)
            .ToList();
        return $"Utvalg ({plantIds.Count} anlegg): {string.Join(", ", names)}";
    }
}
