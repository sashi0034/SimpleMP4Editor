using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleMP4Editor;

public sealed class RangeTrack : FrameworkElement
{
    private enum Handle { Playhead, Start, End }
    private Handle drag;
    internal Clip? CurrentClip { get; set; }
    internal double Position { get; set; }
    internal event Action<double>? Seek;
    internal event Action<bool, double>? Boundary;
    private const double MarginX = 12;
    private double Span => Math.Max(1, ActualWidth - 2 * MarginX);
    private double X(double time) => MarginX + Span * time / CurrentClip!.Media.Duration;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (CurrentClip == null) return;
        double l = X(CurrentClip.Start), r = X(CurrentClip.End), pos = X(Position);
        dc.DrawRoundedRectangle(Brushes.SlateGray, null, new Rect(MarginX, 13, Span, 6), 3, 3);
        dc.DrawRectangle(Brushes.LightSeaGreen, null, new Rect(l, 12, r-l, 8));
        for (int i = 0; i <= 20; i++)
        {
            double x = MarginX + Span * i / 20;
            dc.DrawLine(new Pen(Brushes.SlateGray, 1), new(x, 30), new(x, i % 5 == 0 ? 36 : 33));
        }
        foreach (double x in new[] { l, r })
            dc.DrawRoundedRectangle(Brushes.LightSeaGreen, null, new Rect(x - 4, 8, 8, 17), 2, 2);
        dc.DrawLine(new Pen(Brushes.White, 3), new(pos, 0), new(pos, 25));
        dc.DrawLine(new Pen(Brushes.MidnightBlue, 1.2), new(pos, 0), new(pos, 25));
    }

    private Handle Hit(Point p)
    {
        double l = Math.Abs(p.X - X(CurrentClip!.Start)), r = Math.Abs(p.X - X(CurrentClip.End));
        return p.Y >= 7 && p.Y <= 26 && Math.Min(l, r) <= 8 ? (l <= r ? Handle.Start : Handle.End) : Handle.Playhead;
    }

    private void Apply(Point p)
    {
        double time = Math.Clamp((p.X - MarginX) / Span, 0, 1) * CurrentClip!.Media.Duration;
        if (drag == Handle.Playhead) Seek?.Invoke(time);
        else Boundary?.Invoke(drag == Handle.Start, time);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (CurrentClip == null) return;
        Point p = e.GetPosition(this);
        drag = Hit(p);
        CaptureMouse();
        Apply(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (CurrentClip == null) return;
        Point p = e.GetPosition(this);
        if (IsMouseCaptured) Apply(p);
        else Cursor = Hit(p) == Handle.Playhead ? Cursors.Hand : Cursors.SizeWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
