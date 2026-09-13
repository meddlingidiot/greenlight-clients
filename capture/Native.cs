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

    /// <summary>Resizes about the centre, keeping 16:9 and an even pixel count — h.264
    /// wants even dimensions, and a poster that is 16:9 matches the card it will sit in.</summary>
    public PixelBox ScaledTo(int width)
    {
        var w = Math.Max(320, width) / 2 * 2;
        var h = (int)Math.Round(w * 9.0 / 16.0) / 2 * 2;
        return new PixelBox(X + (Width - w) / 2, Y + (Height - h) / 2, w, h);
    }
}
