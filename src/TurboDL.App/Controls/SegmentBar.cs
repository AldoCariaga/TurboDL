using System.Windows;
using System.Windows.Media;
using TurboDL.Core.Model;

namespace TurboDL.App.Controls;

/// <summary>
/// A thin progress bar that shows every connection's chunk of the file separately:
/// each segment fills from its own start offset, with a hairline gap where one chunk meets the next.
/// Falls back to a plain fill when there is nothing to split (unknown size, finished, single segment).
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentState>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long?), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1, used when there are no segments to draw (e.g. a finished download).</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Drawn in the hairline between chunks; set it to the surface the bar sits on.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentState>? Segments
    {
        get => (IReadOnlyList<SegmentState>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long? Total
    {
        get => (long?)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush? Gap
    {
        get => (Brush?)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public SegmentBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;
        var bounds = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, bounds, radius, radius);
        dc.PushClip(new RectangleGeometry(bounds, radius, radius));

        var segments = Segments;
        if (Total is long total && total > 0 && segments is { Count: > 1 })
        {
            var scale = w / total;
            foreach (var s in segments)
            {
                var x0 = s.Start * scale;
                var x1 = Math.Min(s.Position, total) * scale;
                if (x1 > x0) dc.DrawRectangle(Fill, null, new Rect(x0, 0, x1 - x0, h));
            }

            // Hairlines where chunks meet, only when chunks are wide enough to read as separate.
            if (Gap is { } gap && w / segments.Count >= 6)
            {
                foreach (var s in segments)
                {
                    if (s.Start == 0) continue;
                    var x = Math.Round(s.Start * scale);
                    dc.DrawRectangle(gap, null, new Rect(x - 0.5, 0, 1, h));
                }
            }
        }
        else
        {
            var f = Math.Clamp(Fraction, 0, 1);
            if (f > 0) dc.DrawRectangle(Fill, null, new Rect(0, 0, w * f, h));
        }

        dc.Pop();
    }
}
