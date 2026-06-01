using System.Globalization;
using SkiaSharp;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Tegner bardiagrammer for Økonomi-rapport-PDF og returnerer PNG-bytes
/// som QuestPDF embedder via <c>.Image(byte[])</c>. Bruker SkiaSharp slik at
/// rendering fungerer headless i Docker (samme native asset som SkiaSharp
/// allerede lastes for andre formål).
///
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md, del 6 (Graf-tegning, rute 1).
/// </summary>
public static class EconomyChartRenderer
{
    private const int Width = 720;
    private const int Height = 360;
    private const int MarginLeft = 60;
    private const int MarginRight = 16;
    private const int MarginTop = 36;
    private const int MarginBottom = 44;

    /// <summary>
    /// Ett vertikalt bardiagram. Hvert punkt blir én søyle med måneds-label
    /// under (eks. "Apr 26"). Y-aksen skalerer auto til datasettets maks
    /// med 5 horisontale grid-linjer. Negative verdier støttes — null-linjen
    /// tegnes som baseline midt i graferen.
    /// </summary>
    public static byte[] RenderBarChart(
        string title,
        IReadOnlyList<MonthBucket> points,
        string accentHex)
    {
        if (points.Count == 0)
        {
            return RenderEmpty(title);
        }

        var info = new SKImageInfo(Width, Height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        canvas.Clear(SKColor.Parse(EconomyPdfTheme.Background));

        DrawTitle(canvas, title);

        var max = points.Max(p => p.Value);
        var min = Math.Min(0, points.Min(p => p.Value));
        var range = max - min;
        if (range <= 0) range = 1;

        var plotLeft = MarginLeft;
        var plotRight = Width - MarginRight;
        var plotTop = MarginTop;
        var plotBottom = Height - MarginBottom;
        var plotW = plotRight - plotLeft;
        var plotH = plotBottom - plotTop;

        DrawGrid(canvas, plotLeft, plotRight, plotTop, plotBottom, min, max);

        var groupWidth = (float)plotW / points.Count;
        var barWidth = groupWidth * 0.62f;

        using var barPaint = new SKPaint
        {
            Color = SKColor.Parse(accentHex),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var labelPaint = new SKPaint
        {
            Color = SKColor.Parse(EconomyPdfTheme.TextMuted),
            IsAntialias = true,
        };
        using var labelFont = new SKFont { Size = 10 };

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var groupCenter = plotLeft + (i + 0.5f) * groupWidth;
            var barLeft = groupCenter - barWidth / 2f;

            var zeroY = plotTop + (float)((max - 0) / range) * plotH;
            var valueY = plotTop + (float)((max - p.Value) / range) * plotH;

            var top = Math.Min(zeroY, valueY);
            var bot = Math.Max(zeroY, valueY);
            canvas.DrawRect(new SKRect(barLeft, top, barLeft + barWidth, bot), barPaint);

            var monthLabel = $"{MonthShort(p.Month)} {(p.Year % 100):D2}";
            var labelWidth = labelFont.MeasureText(monthLabel);
            canvas.DrawText(monthLabel,
                groupCenter - labelWidth / 2f,
                plotBottom + 14,
                SKTextAlign.Left,
                labelFont, labelPaint);
        }

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    private static void DrawTitle(SKCanvas canvas, string title)
    {
        using var titlePaint = new SKPaint
        {
            Color = SKColor.Parse(EconomyPdfTheme.TextPrimary),
            IsAntialias = true,
        };
        using var titleFont = new SKFont { Size = 14, Embolden = true };
        canvas.DrawText(title, MarginLeft, 22, SKTextAlign.Left, titleFont, titlePaint);
    }

    private static void DrawGrid(
        SKCanvas canvas, int left, int right, int top, int bottom, double min, double max)
    {
        using var gridPaint = new SKPaint
        {
            Color = SKColor.Parse(EconomyPdfTheme.Border),
            IsAntialias = true,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };
        using var labelPaint = new SKPaint
        {
            Color = SKColor.Parse(EconomyPdfTheme.TextSubtle),
            IsAntialias = true,
        };
        using var labelFont = new SKFont { Size = 9 };

        const int Lines = 4;
        for (var i = 0; i <= Lines; i++)
        {
            var frac = (float)i / Lines;
            var y = top + frac * (bottom - top);
            canvas.DrawLine(left, y, right, y, gridPaint);

            var value = max - frac * (max - min);
            var label = FormatAxisValue(value);
            canvas.DrawText(label, left - 6, y + 3, SKTextAlign.Right, labelFont, labelPaint);
        }
    }

    private static byte[] RenderEmpty(string title)
    {
        var info = new SKImageInfo(Width, Height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(EconomyPdfTheme.Background));
        DrawTitle(canvas, title);

        using var paint = new SKPaint
        {
            Color = SKColor.Parse(EconomyPdfTheme.TextMuted),
            IsAntialias = true,
        };
        using var font = new SKFont { Size = 12 };
        canvas.DrawText("Ingen data for valgt periode",
            Width / 2f, Height / 2f, SKTextAlign.Center, font, paint);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    /// <summary>
    /// Kompakt aksetekst: 1.2M for million, 35k for tusen, ellers heltall.
    /// Norsk tusenskille brukes ikke i akser (for trange), engelsk K/M er
    /// internasjonal konvensjon.
    /// </summary>
    private static string FormatAxisValue(double value)
    {
        var abs = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        if (abs >= 1_000_000)
        {
            return $"{sign}{(abs / 1_000_000).ToString("F1", CultureInfo.InvariantCulture)}M";
        }
        if (abs >= 1_000)
        {
            return $"{sign}{(abs / 1_000).ToString("F0", CultureInfo.InvariantCulture)}k";
        }
        return value.ToString("F0", CultureInfo.InvariantCulture);
    }

    private static string MonthShort(int month) => month switch
    {
        1 => "Jan", 2 => "Feb", 3 => "Mar", 4 => "Apr",
        5 => "Mai", 6 => "Jun", 7 => "Jul", 8 => "Aug",
        9 => "Sep", 10 => "Okt", 11 => "Nov", 12 => "Des",
        _ => "?",
    };

    /// <summary>
    /// Én måned-bøtte med en metrisk verdi i NOK. Brukes som input til
    /// <see cref="RenderBarChart"/>. Holdes minimal slik at PDF-byggeren kan
    /// projisere fra Økonomi-rapport-DTO uten ytterligere strukturer.
    /// </summary>
    public sealed record MonthBucket(int Year, int Month, double Value);
}
