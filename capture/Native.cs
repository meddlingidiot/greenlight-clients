using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Greenlight.GalleryCapture;

/// <summary>
/// The Win32 the viewfinder is made of: a real hole in the window, and a screen grab.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Native
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private const int RgnDiff = 4;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int GwlpHwndParent = -8;

    /// <summary>
    /// Makes <paramref name="owner"/> the Win32 owner of <paramref name="hwnd"/>.
    /// </summary>
    /// <remarks>
    /// An owned window is always above its owner, whatever was clicked last. That is the one
    /// guarantee Win32 gives about z-order between two topmost windows — re-raising after
    /// every click is a race, and this tool lost it often enough. Set at this level rather
    /// than through Avalonia's <c>Show(owner)</c> so hiding the frame on the details page
    /// stays a plain hide and the panel is not taken with it.
    /// </remarks>
    public static void SetOwner(IntPtr hwnd, IntPtr owner)
    {
        if (hwnd == IntPtr.Zero || owner == IntPtr.Zero) return;
        SetWindowLongPtrW(hwnd, GwlpHwndParent, owner);
    }

    /// <summary>
    /// Breaks the owner link <see cref="SetOwner"/> made, leaving the window ownerless.
    /// </summary>
    /// <remarks>
    /// Win32 destroys an owner's owned windows along with it, and Avalonia does not know
    /// this ownership exists — it was set behind its back, so the toolkit's own
    /// <c>Owner</c> is still null. Closing the frame therefore destroyed the panel and the
    /// strip underneath Avalonia, part-way through the close it was already running, and the
    /// frame's own close never finished. The lifetime kept it in its window list, saw a
    /// window still open, and cancelled the shutdown it had just started: every window gone
    /// from the screen, the message loop still spinning, and the process only killable from
    /// Task Manager. Letting go of the link before anything closes keeps each window's
    /// teardown Avalonia's own to finish.
    /// </remarks>
    public static void ClearOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowLongPtrW(hwnd, GwlpHwndParent, IntPtr.Zero);
    }

    /// <summary>
    /// Puts the window at the top of the topmost band without giving it focus.
    /// </summary>
    /// <remarks>
    /// Both of our windows are topmost, and among topmost windows Win32 orders by whichever
    /// was activated last — so every drag on the dim pulls the frame over the panel and
    /// buries the wizard. The frame calls this for the panel whenever it is touched, and
    /// NOACTIVATE is what keeps focus where it was: the drag in progress, or the text box
    /// being typed into.
    /// </remarks>
    public static void RaiseWithoutFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public const int SrcCopy = 0x00CC0020;
    public const int CaptureBlt = 0x40000000;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h,
        IntPtr source, int sx, int sy, int rop);

    // ── the pointer, for a still that asked for it ─────────────────────────────

    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Colour;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon,
        int width, int height, int step, IntPtr brush, int flags);

    /// <summary>
    /// Draws the pointer, as it is right now, into a grab whose top-left corner was
    /// (<paramref name="originX"/>, <paramref name="originY"/>) on the desktop.
    /// </summary>
    /// <remarks>
    /// BitBlt never includes it — the pointer is a hardware overlay, not part of the desktop
    /// it is sitting on — so it has to be put back by hand. The position GetCursorInfo gives
    /// is the hotspot, not the image's corner, hence the offset. A pointer outside the grab
    /// simply draws off the edge of the bitmap, which clips it; nothing to check.
    /// </remarks>
    public static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero)
            return;

        var hotspotX = 0;
        var hotspotY = 0;
        if (GetIconInfo(info.Cursor, out var icon))
        {
            hotspotX = icon.HotspotX;
            hotspotY = icon.HotspotY;
            // GetIconInfo hands over copies of both bitmaps, and they are ours to free.
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Colour != IntPtr.Zero) DeleteObject(icon.Colour);
        }

        DrawIconEx(hdc, info.X - hotspotX - originX, info.Y - hotspotY - originY,
            info.Cursor, 0, 0, 0, IntPtr.Zero, DiNormal);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>
    /// Cuts <paramref name="hole"/> out of the window, in physical pixels relative to the
    /// window's own top-left corner.
    /// </summary>
    /// <remarks>
    /// This is what makes the cutout real rather than merely transparent. A transparent
    /// pixel is still part of the window and still swallows the click; a pixel outside the
    /// window region does not exist, so the mouse lands on whatever is underneath. That is
    /// why you can drive your client through the hole while the frame is still on top of it
    /// — and it is why the frame, which is outside the hole, stays draggable.
    ///
    /// The region becomes the window's property once set, so the combined region is not
    /// deleted here. The two rectangles that built it are.
    /// </remarks>
    public static void CutHole(IntPtr hwnd, int windowWidth, int windowHeight, PixelBox hole)
    {
        if (hwnd == IntPtr.Zero) return;

        var whole = CreateRectRgn(0, 0, windowWidth, windowHeight);
        var inner = CreateRectRgn(hole.X, hole.Y, hole.Right, hole.Bottom);
        var combined = CreateRectRgn(0, 0, 0, 0);

        CombineRgn(combined, whole, inner, RgnDiff);
        SetWindowRgn(hwnd, combined, true);

        DeleteObject(whole);
        DeleteObject(inner);
    }

    /// <summary>
    /// Asks Windows to leave this window out of every screen capture, and reports whether
    /// it agreed.
    /// </summary>
    /// <remarks>
    /// The one thing the region viewfinder cannot solve by moving out of the way: a capture
    /// of the whole screen has no beside. WDA_EXCLUDEFROMCAPTURE removes the window from
    /// what BitBlt and the desktop duplication API see — ffmpeg's gdigrab included — while
    /// leaving it perfectly visible to the person using it. So the shutter button can sit in
    /// the middle of the shot and still not be in it.
    ///
    /// Windows 10 2004 is where this arrived; older builds fail the call, and the caller
    /// falls back to hiding the window for the length of the capture, which is what the tool
    /// did everywhere before.
    /// </remarks>
    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

    // ── hotkeys, for a shutter that works while something else has the keyboard ────

    public const int WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;

    /// <summary>Held down is one press. Without it a leant-on key is a burst of them.</summary>
    public const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Keeps the window out of Alt+Tab. It is furniture, not a destination.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var current = (long)GetWindowLongPtrW(hwnd, GwlExStyle);
        SetWindowLongPtrW(hwnd, GwlExStyle, (IntPtr)(current | (long)(uint)WsExToolWindow));
    }
}

/// <summary>A rectangle in physical screen pixels. Avalonia thinks in DIPs; Win32 and every
/// capture API here do not, and mixing the two silently is how you get a blurry, offset
/// screenshot on a scaled display.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelBox MovedBy(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Trims to even dimensions. A region chosen with <see cref="ScaledTo"/> is
    /// already even; a whole screen is whatever the monitor happens to be, and h.264 will
    /// not encode an odd one.</summary>
    public PixelBox Evened() => this with { Width = Width / 2 * 2, Height = Height / 2 * 2 };

    /// <summary>Resizes about the centre, keeping 16:9 and an even pixel count — h.264
    /// wants even dimensions, and a poster that is 16:9 matches the card it will sit in.</summary>
    public PixelBox ScaledTo(int width)
    {
        var w = Math.Max(320, width) / 2 * 2;
        var h = (int)Math.Round(w * 9.0 / 16.0) / 2 * 2;
        return new PixelBox(X + (Width - w) / 2, Y + (Height - h) / 2, w, h);
    }
}
