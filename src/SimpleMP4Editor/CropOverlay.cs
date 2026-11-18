using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleMP4Editor;

public sealed class CropOverlay : FrameworkElement
{
    private static readonly Brush Accent = Brushes.Turquoise;
    private static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(160, 8, 12, 18));
    private Clip? clip;
    private Rect frame;
    private Grip selected, hover, drag;
    private PixelRect original;
    private Point origin;
    internal bool Mirror { get; set; }
    internal event Action? Changed;

    public CropOverlay()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
        System.Windows.Automation.AutomationProperties.SetName(this, "切り抜き枠");
    }

    internal void SetFrame(Clip? current, Rect video)
    {
        if (!ReferenceEquals(current, clip))
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            selected = hover = Grip.None;
        }
        clip = current;
        frame = video;
        InvalidateVisual();
    }

    internal void ResetSelection() { selected = hover = Grip.None; InvalidateVisual(); }

    private Rect ViewRect(PixelRect p)
    {
        double sx = frame.Width / clip!.Media.Width, sy = frame.Height / clip.Media.Height;
        return new(frame.X + p.Left * sx, frame.Y + p.Top * sy, p.Width * sx, p.Height * sy);
    }

    private Point Pixel(Point point) => new(
        Math.Round((point.X - frame.Left) * clip!.Media.Width / frame.Width),
        Math.Round((point.Y - frame.Top) * clip.Media.Height / frame.Height));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (clip == null || frame.IsEmpty) return;
        Rect r = ViewRect(clip.Crop);
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(frame));
        mask.Children.Add(new RectangleGeometry(r));
        dc.DrawGeometry(Shade, null, mask);
        void Line(Grip edge, Point a, Point b)
        {
            bool active = (selected & edge) != 0 || (hover & edge) != 0;
            dc.DrawLine(new Pen(Brushes.Black, active ? 4 : 2.5), a, b);
            dc.DrawLine(new Pen(active ? Accent : Brushes.White, active ? 2.8 : 1.3), a, b);
        }
        Line(Grip.Top, r.TopLeft, r.TopRight);
        Line(Grip.Left, r.TopLeft, r.BottomLeft);
        Line(Grip.Bottom, r.BottomLeft, r.BottomRight);
        Line(Grip.Right, r.TopRight, r.BottomRight);
        foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(p.X - 3, p.Y - 3, 6, 6), 1, 1);
    }

    private Grip Hit(Point p)
    {
        if (clip == null || frame.IsEmpty) return Grip.None;
        Rect r = ViewRect(clip.Crop);
        const int margin = 8;
        Rect near = r; near.Inflate(margin, margin);
        if (near.Contains(p))
        {
            double left = Math.Abs(p.X - r.Left), right = Math.Abs(p.X - r.Right);
            double top = Math.Abs(p.Y - r.Top), bottom = Math.Abs(p.Y - r.Bottom);
            Grip x = Math.Min(left, right) <= margin ? (left <= right ? Grip.Left : Grip.Right) : Grip.None;
            Grip y = Math.Min(top, bottom) <= margin ? (top <= bottom ? Grip.Top : Grip.Bottom) : Grip.None;
            if ((x | y) != Grip.None) return x | y;
            if (r.Contains(p)) return Grip.Move;
        }
        return frame.Contains(p) ? Grip.Draw : Grip.None;
    }

    private void SetHover(Grip grip)
    {
        hover = grip;
        Cursor = grip switch
        {
            Grip.Left or Grip.Right => Cursors.SizeWE,
            Grip.Top or Grip.Bottom => Cursors.SizeNS,
            (Grip.Left | Grip.Top) or (Grip.Right | Grip.Bottom) => Cursors.SizeNWSE,
            (Grip.Left | Grip.Bottom) or (Grip.Right | Grip.Top) => Cursors.SizeNESW,
            Grip.Move => Cursors.SizeAll, Grip.Draw => Cursors.Cross, _ => Cursors.Arrow
        };
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point view = e.GetPosition(this);
        if (!IsMouseCaptured || clip == null) { SetHover(Hit(view)); return; }
        Point p = Pixel(view);
        if (drag == Grip.Draw)
        {
            int w = clip.Media.Width, h = clip.Media.Height;
            int l = Math.Clamp((int)Math.Min(origin.X, p.X), 0, w - 1), t = Math.Clamp((int)Math.Min(origin.Y, p.Y), 0, h - 1);
            clip.Crop = new(l, t, Math.Clamp((int)Math.Max(origin.X, p.X), l + 1, w), Math.Clamp((int)Math.Max(origin.Y, p.Y), t + 1, h));
        }
        else clip.Crop = original.Adjust(drag, (int)(p.X - origin.X), (int)(p.Y - origin.Y), clip.Media.Width, clip.Media.Height, Mirror);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (clip == null) return;
        var p = e.GetPosition(this);
        drag = Hit(p);
        if (drag == Grip.None) return;
        Focus();
        original = clip.Crop;
        origin = Pixel(p);
        selected = drag;
        CaptureMouse();
        SetHover(drag);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        SetHover(Hit(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!IsMouseCaptured) SetHover(Grip.None);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (clip == null || selected is Grip.None or Grip.Draw) return;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return;
        clip.Crop = clip.Crop.Adjust(selected, dx, dy, clip.Media.Width, clip.Media.Height, Mirror);
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }
}
