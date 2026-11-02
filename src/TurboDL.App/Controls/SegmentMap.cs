using System.Windows;
using System.Windows.Media;
using TurboDL.Core.Model;

namespace TurboDL.App.Controls;

/// <summary>
/// The classic segment map: the file as a grid of small blocks, read left to right, top to bottom.
/// Downloaded blocks are filled, partly downloaded ones are filled in proportion (by opacity), and the block
/// each connection is writing right now is drawn in the "head" colour so you can watch the connections move.
/// </summary>
public sealed class SegmentMap : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentState>), typeof(SegmentMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long?), typeof(SegmentMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SegmentMap),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HeadProperty = DependencyProperty.Register(
        nameof(Head), typeof(Brush), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyProperty = DependencyProperty.Register(
        nameof(Empty), typeof(Brush), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

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

    /// <summary>Draw the write heads only while the download runs.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Head { get => (Brush)GetValue(HeadProperty); set => SetValue(HeadProperty, value); }
    public Brush Empty { get => (Brush)GetValue(EmptyProperty); set => SetValue(EmptyProperty, value); }

    public double CellSize { get; set; } = 9;
    public double CellGap { get; set; } = 2;

    public SegmentMap()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var pitch = CellSize + CellGap;
        var cols = Math.Max(1, (int)((ActualWidth + CellGap) / pitch));
        var rows = Math.Max(1, (int)((ActualHeight + CellGap) / pitch));
        var cells = cols * rows;
        // Centre the grid in the space it was given.
        var offsetX = Math.Floor((ActualWidth - (cols * pitch - CellGap)) / 2);

        var segments = Segments ?? [];
        var total = Total ?? 0;
        var bytesPerCell = total > 0 ? (double)total / cells : 0;

        var heads = new HashSet<int>();
        if (IsActive && bytesPerCell > 0)
            foreach (var s in segments)
                if (s.Position <= s.End && s.End != long.MaxValue)
                    heads.Add((int)Math.Min(cells - 1, s.Position / bytesPerCell));

        for (var i = 0; i < cells; i++)
        {
            var rect = new Rect(offsetX + (i % cols) * pitch, (i / cols) * pitch, CellSize, CellSize);
            dc.DrawRoundedRectangle(Empty, null, rect, 2, 2);
            if (bytesPerCell <= 0) continue;

            if (heads.Contains(i))
            {
                dc.DrawRoundedRectangle(Head, null, rect, 2, 2);
                continue;
            }

            var c0 = i * bytesPerCell;
            var c1 = c0 + bytesPerCell;
            double done = 0;
            foreach (var s in segments)
            {
                var lo = Math.Max(c0, s.Start);
                var hi = Math.Min(c1, Math.Min(s.Position, total));
                if (hi > lo) done += hi - lo;
            }
            var f = done / bytesPerCell;
            if (f >= 0.999) dc.DrawRoundedRectangle(Fill, null, rect, 2, 2);
            else if (f > 0.02)
            {
                dc.PushOpacity(0.25 + 0.5 * f);
                dc.DrawRoundedRectangle(Fill, null, rect, 2, 2);
                dc.Pop();
            }
        }
    }
}
