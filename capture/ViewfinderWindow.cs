using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Greenlight.GalleryCapture;

/// <summary>
/// The viewfinder: everything outside the frame is dimmed, and the frame itself is a real
/// hole in the window, so what you see through it is exactly what gets captured — and you
/// can still drive your client through it while you line the shot up.
/// </summary>
/// <remarks>
/// Same trick as the cars: a transparent, undecorated, always-on-top window laid over the
/// desktop. The difference is <see cref="Native.CutHole"/>. The cars make the whole window
/// click-through because nothing on it is interactive; here the dimmed surround has to stay
/// draggable, so only the frame's interior is removed from the window.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ViewfinderWindow : Window
{
    private const int HandleSize = 22;
    private const int SmallestWidth = 480;

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(150, 8, 12, 10));
    private static readonly Color Accent = Color.FromRgb(0x3F, 0xD7, 0x7F);

    private PixelBox _origin;          // the window's own top-left, in physical pixels
    private PixelBox _region;          // the capture area, in absolute physical pixels
    private Point _dragFrom;
    private PixelBox _dragStart;
    private int _dragCorner = -1;
    private bool _dragging;

    public ViewfinderWindow()
    {
        Title = "Greenlight gallery capture";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>The capture area, in physical screen pixels. What the grab and the recorder use.</summary>
    public PixelBox Region => _region;

    public event EventHandler? RegionChanged;
    public event EventHandler? Accepted;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Native.MakeToolWindow(handle);

        CoverEveryScreen();
        RecutHole();
    }

    /// <summary>
    /// Spans the whole virtual desktop, so the frame can be dragged onto a second monitor
    /// — which is where a build-indicator client usually lives, being the thing you want in
    /// the corner of your eye rather than in front of you.
    /// </summary>
    private void CoverEveryScreen()
    {
        var screens = Screens.All;
        var left = screens.Min(s => s.Bounds.X);
        var top = screens.Min(s => s.Bounds.Y);
        var right = screens.Max(s => s.Bounds.X + s.Bounds.Width);
        var bottom = screens.Max(s => s.Bounds.Y + s.Bounds.Height);

        _origin = new PixelBox(left, top, right - left, bottom - top);

        Position = new PixelPoint(left, top);
        Width = _origin.Width / RenderScaling;
        Height = _origin.Height / RenderScaling;

        var primary = Screens.Primary ?? screens[0];
        var start = new PixelBox(0, 0, 0, 0) with
        {
            X = primary.Bounds.X + primary.Bounds.Width / 2,
            Y = primary.Bounds.Y + primary.Bounds.Height / 2,
        };
        _region = new PixelBox(start.X, start.Y, 1280, 720).ScaledTo(1280);
        _region = _region with { X = _region.X - 640, Y = _region.Y - 360 };
    }

    private void RecutHole()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var relative = _region.MovedBy(-_origin.X, -_origin.Y);
        Native.CutHole(handle, _origin.Width, _origin.Height, relative);
        InvalidateVisual();
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRegion(PixelBox box)
    {
        // Keep it on the desktop. A frame dragged off the edge captures black, and finding
        // that out after recording is a wasted ten seconds.
        var x = Math.Clamp(box.X, _origin.X, _origin.Right - box.Width);
        var y = Math.Clamp(box.Y, _origin.Y, _origin.Bottom - box.Height);
        _region = box with { X = x, Y = y };
        RecutHole();
    }

    // --- drawing ---------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var scale = RenderScaling;
        var hole = new Rect(
            (_region.X - _origin.X) / scale, (_region.Y - _origin.Y) / scale,
            _region.Width / scale, _region.Height / scale);
        var full = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // Everything but the hole, dimmed. Drawn as four bands rather than an even-odd
        // geometry: the hole is already gone from the window region, so anything painted
        // there would be discarded anyway, and four rectangles are cheaper to reason about.
        context.FillRectangle(Dim, new Rect(full.X, full.Y, full.Width, hole.Y));
        context.FillRectangle(Dim, new Rect(full.X, hole.Bottom, full.Width, full.Bottom - hole.Bottom));
        context.FillRectangle(Dim, new Rect(full.X, hole.Y, hole.X, hole.Height));
        context.FillRectangle(Dim, new Rect(hole.Right, hole.Y, full.Right - hole.Right, hole.Height));

        var pen = new Pen(new SolidColorBrush(Accent), 2);
        context.DrawRectangle(null, pen, hole.Inflate(1));

        var handle = HandleSize / scale;
        var corner = new SolidColorBrush(Accent);
        foreach (var point in new[]
                 {
                     new Point(hole.X, hole.Y), new Point(hole.Right, hole.Y),
                     new Point(hole.X, hole.Bottom), new Point(hole.Right, hole.Bottom),
                 })
        {
            var x = point.X <= hole.X ? point.X - handle : point.X;
            var y = point.Y <= hole.Y ? point.Y - handle : point.Y;
            context.FillRectangle(corner, new Rect(x, y, handle, handle));
        }

        var readout = new FormattedText(
            $"{_region.Width} × {_region.Height}    16:9    drag to move · corners to resize · wheel to zoom · Enter when it looks right · Esc to cancel",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xEA)));

        var labelY = hole.Y - 26 > 0 ? hole.Y - 26 : hole.Bottom + 8;
        context.DrawText(readout, new Point(hole.X, labelY));
    }

    // --- interaction -----------------------------------------------------------------

    private int CornerAt(Point position)
    {
        var scale = RenderScaling;
        var hole = new Rect(
            (_region.X - _origin.X) / scale, (_region.Y - _origin.Y) / scale,
            _region.Width / scale, _region.Height / scale);
        var handle = HandleSize / scale;

        var corners = new[]
        {
            new Rect(hole.X - handle, hole.Y - handle, handle, handle),
            new Rect(hole.Right, hole.Y - handle, handle, handle),
            new Rect(hole.X - handle, hole.Bottom, handle, handle),
            new Rect(hole.Right, hole.Bottom, handle, handle),
        };
        for (var i = 0; i < corners.Length; i++)
            if (corners[i].Contains(position)) return i;
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        _dragStart = _region;
        _dragCorner = CornerAt(_dragFrom);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        var now = e.GetPosition(this);
        var dx = (int)Math.Round((now.X - _dragFrom.X) * RenderScaling);
        var dy = (int)Math.Round((now.Y - _dragFrom.Y) * RenderScaling);

        if (_dragCorner < 0)
        {
            SetRegion(_dragStart.MovedBy(dx, dy));
            return;
        }

        // Resizing stays 16:9, so the width is the only thing a corner actually decides.
        // Left-hand corners grow leftwards, which is what the hand expects.
        var widthDelta = _dragCorner is 0 or 2 ? -dx : dx;
        var width = Math.Max(SmallestWidth, _dragStart.Width + widthDelta);
        SetRegion(_dragStart.ScaledTo(width));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _dragCorner = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetRegion(_region.ScaledTo(_region.Width + (e.Delta.Y > 0 ? 80 : -80)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;

        switch (e.Key)
        {
            case Key.Left: SetRegion(_region.MovedBy(-step, 0)); break;
            case Key.Right: SetRegion(_region.MovedBy(step, 0)); break;
            case Key.Up: SetRegion(_region.MovedBy(0, -step)); break;
            case Key.Down: SetRegion(_region.MovedBy(0, step)); break;
            case Key.Enter: Accepted?.Invoke(this, EventArgs.Empty); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }
}
