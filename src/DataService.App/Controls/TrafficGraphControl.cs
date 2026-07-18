using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DataService.App.Controls;

public sealed class TrafficGraphControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<TrafficGraphControl, IReadOnlyList<double>?>(nameof(Samples));

    public TrafficGraphControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SamplesProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        const double axisWidth = 76;
        var graphBounds = bounds.Deflate(new Thickness(10, 10, axisWidth, 18));
        if (graphBounds.Width <= 0 || graphBounds.Height <= 0)
        {
            return;
        }

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var background = new SolidColorBrush(Color.Parse(isDark ? "#0E141D" : "#F5F7FB"));
        var gridPen = new Pen(new SolidColorBrush(Color.Parse(isDark ? "#26313D" : "#E1E7EF")), 1);
        var linePen = new Pen(new SolidColorBrush(Color.Parse(isDark ? "#4ADE80" : "#16A34A")), 2);
        var fillBrush = new SolidColorBrush(Color.Parse(isDark ? "#4ADE80" : "#16A34A"), isDark ? 0.16 : 0.12);
        var axisBrush = new SolidColorBrush(Color.Parse(isDark ? "#9AA8B9" : "#5B6B7E"));
        var axisPen = new Pen(new SolidColorBrush(Color.Parse(isDark ? "#334155" : "#C9D3DF")), 1);

        context.DrawRectangle(background, null, new RoundedRect(bounds, 8));

        for (var i = 1; i < 4; i++)
        {
            var y = graphBounds.Y + graphBounds.Height * i / 4;
            context.DrawLine(gridPen, new Point(graphBounds.X, y), new Point(graphBounds.Right, y));
        }

        context.DrawLine(axisPen, new Point(graphBounds.Right, graphBounds.Y), new Point(graphBounds.Right, graphBounds.Bottom));

        var samples = Samples;
        if (samples is null || samples.Count == 0)
        {
            DrawAxisLabel(context, axisBrush, "0 MB/s", graphBounds.Right + 10, graphBounds.Bottom - 11);
            return;
        }

        var max = Math.Max(1, samples.Max());
        var axisMax = CalculateAxisMax(max);
        DrawAxisLabel(context, axisBrush, FormatMegabytesPerSecond(axisMax), graphBounds.Right + 10, graphBounds.Y - 1);
        DrawAxisLabel(context, axisBrush, FormatMegabytesPerSecond(axisMax / 2), graphBounds.Right + 10, graphBounds.Y + graphBounds.Height / 2 - 7);
        DrawAxisLabel(context, axisBrush, "0 MB/s", graphBounds.Right + 10, graphBounds.Bottom - 13);

        const double strokeInset = 5;
        var drawableHeight = Math.Max(1, graphBounds.Height - strokeInset * 2);
        var step = samples.Count == 1 ? graphBounds.Width : graphBounds.Width / (samples.Count - 1);
        var smoothedSamples = Smooth(samples);
        var points = smoothedSamples
            .Select((sample, index) => new Point(
                graphBounds.X + index * step,
                graphBounds.Bottom - strokeInset - (sample / axisMax * drawableHeight)))
            .ToArray();

        context.DrawGeometry(fillBrush, null, BuildAreaGeometry(points, graphBounds.Bottom));
        context.DrawGeometry(null, linePen, BuildLineGeometry(points));
    }

    private static StreamGeometry BuildLineGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var geometryContext = geometry.Open();
        geometryContext.BeginFigure(points[0], isFilled: false);
        if (points.Count == 1)
        {
            geometryContext.LineTo(points[0]);
        }
        else
        {
            for (var i = 0; i < points.Count - 1; i++)
            {
                var previous = i == 0 ? points[i] : points[i - 1];
                var current = points[i];
                var next = points[i + 1];
                var afterNext = i + 2 < points.Count ? points[i + 2] : next;
                var control1 = new Point(
                    current.X + (next.X - previous.X) / 6,
                    current.Y + (next.Y - previous.Y) / 6);
                var control2 = new Point(
                    next.X - (afterNext.X - current.X) / 6,
                    next.Y - (afterNext.Y - current.Y) / 6);
                geometryContext.CubicBezierTo(control1, control2, next);
            }
        }

        geometryContext.EndFigure(isClosed: false);
        return geometry;
    }

    private static StreamGeometry BuildAreaGeometry(IReadOnlyList<Point> points, double baseline)
    {
        var geometry = new StreamGeometry();
        using var geometryContext = geometry.Open();
        geometryContext.BeginFigure(new Point(points[0].X, baseline), isFilled: true);
        geometryContext.LineTo(points[0]);
        if (points.Count == 1)
        {
            geometryContext.LineTo(points[0]);
        }
        else
        {
            for (var i = 0; i < points.Count - 1; i++)
            {
                var previous = i == 0 ? points[i] : points[i - 1];
                var current = points[i];
                var next = points[i + 1];
                var afterNext = i + 2 < points.Count ? points[i + 2] : next;
                var control1 = new Point(
                    current.X + (next.X - previous.X) / 6,
                    current.Y + (next.Y - previous.Y) / 6);
                var control2 = new Point(
                    next.X - (afterNext.X - current.X) / 6,
                    next.Y - (afterNext.Y - current.Y) / 6);
                geometryContext.CubicBezierTo(control1, control2, next);
            }
        }

        geometryContext.LineTo(new Point(points[^1].X, baseline));
        geometryContext.EndFigure(isClosed: true);
        return geometry;
    }

    private static double[] Smooth(IReadOnlyList<double> samples)
    {
        if (samples.Count < 3)
        {
            return samples.ToArray();
        }

        var smoothed = new double[samples.Count];
        smoothed[0] = samples[0];
        smoothed[^1] = samples[^1];
        for (var i = 1; i < samples.Count - 1; i++)
        {
            smoothed[i] = samples[i - 1] * 0.25 + samples[i] * 0.5 + samples[i + 1] * 0.25;
        }

        return smoothed;
    }

    private static double CalculateAxisMax(double maxBytesPerSecond)
    {
        var maxMegabytes = Math.Max(maxBytesPerSecond / 1024 / 1024, 0.001);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maxMegabytes)));
        var normalized = maxMegabytes / magnitude;
        var niceNormalized = normalized <= 1
            ? 1
            : normalized <= 2
                ? 2
                : normalized <= 5
                    ? 5
                    : 10;

        return niceNormalized * magnitude * 1024 * 1024;
    }

    private static string FormatMegabytesPerSecond(double bytesPerSecond)
    {
        var value = bytesPerSecond / 1024 / 1024;
        var format = value switch
        {
            >= 10 => "0",
            >= 1 => "0.0",
            >= 0.1 => "0.00",
            _ => "0.000"
        };

        return string.Create(CultureInfo.InvariantCulture, $"{value.ToString(format, CultureInfo.InvariantCulture)} MB/s");
    }

    private static void DrawAxisLabel(
        DrawingContext context,
        IBrush brush,
        string text,
        double x,
        double y)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            brush);
        context.DrawText(formatted, new Point(x, y));
    }
}
