using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Greenlight.GalleryCapture;

/// <summary>
/// Taking the picture, and taking the video. Both produce a file the gallery's own checks
/// already accept — the limits live here as constants rather than as advice in a document
/// somebody has to remember.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Capture
{
    public const long PosterMaxBytes = 1 * 1024 * 1024;
    public const long ClipMaxBytes = 3 * 1024 * 1024;
    public const int ClipSeconds = 10;

    /// <summary>
    /// Grabs the framed region and writes a JPEG that fits the 1 MB limit.
    /// </summary>
    /// <remarks>
    /// Quality steps down until it fits rather than guessing once. A screenshot of a dark
    /// UI compresses very differently from one of a photograph, so a fixed quality is either
    /// wasteful or occasionally over the line — and over the line means the contributor
    /// finds out from CI instead of from here.
    /// </remarks>
    public static void Poster(PixelBox box, string path)
    {
        using var bitmap = Grab(box);
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");

        foreach (var quality in new[] { 92L, 85L, 78L, 70L, 60L, 50L })
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(path, encoder, parameters);
            if (new FileInfo(path).Length <= PosterMaxBytes) return;
        }
        // Six passes and still over 1 MB means the frame is enormous. Shrinking beats
        // shipping something the checks will reject.
        using var smaller = new Bitmap(bitmap, box.Width / 2, box.Height / 2);
        smaller.Save(path, ImageFormat.Jpeg);
    }

    /// <summary>
    /// Grabs the region straight through GDI rather than through
    /// <see cref="Graphics.CopyFromScreen(int,int,int,int,Size,CopyPixelOperation)"/>.
    /// </summary>
    /// <remarks>
    /// The flag that matters is CAPTUREBLT, which is what includes layered windows in the
    /// grab — and a Greenlight client is very often exactly that: the cars are a layered,
    /// click-through window laid over the taskbar, and without this they photograph as an
    /// empty strip of desktop.
    ///
    /// CopyFromScreen cannot express it. Its parameter is a validated enum, so OR-ing
    /// CAPTUREBLT into SourceCopy produces a value no enum member matches and it throws
    /// InvalidEnumArgumentException rather than passing the raster op through. Hence BitBlt
    /// directly, where the raster op is just an int.
    /// </remarks>
    private static Bitmap Grab(PixelBox box)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var memory = Native.CreateCompatibleDC(screen);
        var handle = Native.CreateCompatibleBitmap(screen, box.Width, box.Height);
        var previous = Native.SelectObject(memory, handle);
        try
        {
            Native.BitBlt(memory, 0, 0, box.Width, box.Height, screen, box.X, box.Y,
                Native.SrcCopy | Native.CaptureBlt);
            return Image.FromHbitmap(handle);
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(handle);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static bool HasFfmpeg => FindFfmpeg() is not null;

    private static string? FindFfmpeg()
    {
        foreach (var candidate in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (probe is null) continue;
                probe.WaitForExit(4000);
                if (probe.ExitCode == 0) return candidate;
            }
            catch (Exception)
            {
                // Not on PATH. The caller offers the winget line; the clip is optional.
            }
        }
        return null;
    }

    /// <summary>
    /// Records the framed region for <see cref="ClipSeconds"/> seconds into a silent mp4
    /// inside the 3 MB limit.
    /// </summary>
    /// <remarks>
    /// The bitrate is computed from the limit rather than picked, and capped once by
    /// maxrate/bufsize, so a busy scene cannot overshoot the way a pure CRF encode can. Ten
    /// seconds is chosen to sit under the fifteen the schema allows: the card loops this
    /// forever, and a loop wants to be short.
    /// </remarks>
    public static async Task<string?> Clip(PixelBox box, string path, CancellationToken cancellationToken)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return "ffmpeg is not on PATH";

        // 90% of the budget, in kbit/s, leaving room for the container's own overhead.
        var kbits = (int)(ClipMaxBytes * 8 * 0.90 / ClipSeconds / 1000);

        var arguments =
            $"-hide_banner -loglevel error -y " +
            $"-f gdigrab -framerate 30 -draw_mouse 0 " +
            $"-offset_x {box.X} -offset_y {box.Y} -video_size {box.Width}x{box.Height} " +
            $"-i desktop -t {ClipSeconds} " +
            $"-an -c:v libx264 -preset veryfast -pix_fmt yuv420p " +
            $"-b:v {kbits}k -maxrate {kbits}k -bufsize {kbits * 2}k " +
            $"-movflags +faststart \"{path}\"";

        using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process is null) return "ffmpeg would not start";

        var errors = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            return string.IsNullOrWhiteSpace(errors) ? "ffmpeg failed" : errors.Trim();
        if (!File.Exists(path))
            return "ffmpeg reported success but wrote nothing";
        if (new FileInfo(path).Length > ClipMaxBytes)
            return $"the clip came out at {new FileInfo(path).Length / 1024 / 1024.0:F1} MB, over the 3 MB limit";

        return null;
    }
}
