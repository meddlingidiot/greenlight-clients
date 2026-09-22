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

    /// <summary>
    /// What a clip is capped at unless somebody asks for otherwise.
    /// </summary>
    /// <remarks>
    /// Ten rather than the fifteen the schema allows: the card loops it forever, and a loop
    /// wants to be short. It is also exactly one pass of the state strip's Auto cycle.
    /// </remarks>
    public const int ClipSeconds = 10;

    /// <summary>
    /// The longest clip the gallery schema will take. Past this a recording is still a
    /// perfectly good file — it is just not a listing's clip, and the tool says so rather
    /// than letting it fail validation later.
    /// </summary>
    public const int SchemaClipSeconds = 15;

    /// <summary>The caps the length button offers, once it has been unlocked.</summary>
    public static readonly int[] ClipLengths = [ClipSeconds, SchemaClipSeconds, 30, 60, 120];

    /// <summary>
    /// Whether the pointer goes into the picture — the still and the clip alike.
    /// </summary>
    /// <remarks>
    /// Off unless asked. A listing's poster is a picture of the client, and a pointer parked
    /// wherever the last click left it is noise in it. The occasional client that is
    /// <i>about</i> the pointer is the exception, and the state strip has the switch.
    /// </remarks>
    public static bool IncludeCursor { get; set; }

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
            if (IncludeCursor) Native.DrawCursor(memory, box.X, box.Y);
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
    /// Starts recording the framed region into a silent mp4 that fits the 3 MB limit, and
    /// hands back the recording so it can be stopped when the interesting bit is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bitrate is computed from the limit rather than picked, and capped once by
    /// maxrate/bufsize, so a busy scene cannot overshoot the way a pure CRF encode can. The
    /// cap is passed to ffmpeg as well as offered as a button: whatever the person does, the
    /// file cannot run past the length that was asked for.
    /// </para>
    /// <para>
    /// That bitrate is a rate and not a division of the budget by whatever length was asked
    /// for. Spreading 3 MB across a two-minute take would not produce a longer clip, it would
    /// produce a worse-looking one — and a take that long is not going into a listing anyway,
    /// so the thing worth holding constant is how it looks.
    /// </para>
    /// <para>
    /// The failure string is what the caller shows; a null one with a null recording cannot
    /// happen, and a non-null recording means ffmpeg is already grabbing frames.
    /// </para>
    /// </remarks>
    /// <param name="box">The region to record.</param>
    /// <param name="path">Where the mp4 goes.</param>
    /// <param name="seconds">The cap. Past <see cref="SchemaClipSeconds"/> the size limit stops applying.</param>
    public static (Recording? Recording, string? Failure) StartClip(PixelBox box, string path, int seconds)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return (null, "ffmpeg is not on PATH");

        // 90% of the budget over a default-length clip, in kbit/s, leaving room for the
        // container's own overhead.
        var kbits = (int)(ClipMaxBytes * 8 * 0.90 / ClipSeconds / 1000);

        var arguments =
            $"-hide_banner -loglevel error -y " +
            $"-f gdigrab -framerate 30 -draw_mouse {(IncludeCursor ? 1 : 0)} " +
            $"-offset_x {box.X} -offset_y {box.Y} -video_size {box.Width}x{box.Height} " +
            $"-i desktop -t {seconds} " +
            $"-an -c:v libx264 -preset veryfast -pix_fmt yuv420p " +
            $"-b:v {kbits}k -maxrate {kbits}k -bufsize {kbits * 2}k " +
            $"-movflags +faststart \"{path}\"";

        var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });

        // A take that could still be a listing's clip is held to the listing's size; one that
        // could not is held to nothing, because failing it for being 8 MB would be refusing to
        // do the thing that was explicitly asked for.
        return process is null
            ? (null, "ffmpeg would not start")
            : (new Recording(process, path, seconds <= SchemaClipSeconds ? ClipMaxBytes : null), null);
    }
}

/// <summary>
/// A recording that is running now: stoppable, and awaitable for the verdict on the file it
/// leaves behind.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Recording
{
    private readonly Process _process;
    private readonly string _path;
    private readonly long? _maxBytes;
    private bool _stopped;

    internal Recording(Process process, string path, long? maxBytes)
    {
        _process = process;
        _path = path;
        _maxBytes = maxBytes;
        StartedAt = DateTime.UtcNow;
        Finished = Wait();
    }

    public DateTime StartedAt { get; }

    private DateTime? _ended;

    /// <summary>How long it has been running, and once it is over, how long it ran.</summary>
    public TimeSpan Elapsed => (_ended ?? DateTime.UtcNow) - StartedAt;

    /// <summary>How big the file came out, once <see cref="Finished"/> has completed.</summary>
    public long Bytes { get; private set; }

    /// <summary>Null when the clip is usable, otherwise what went wrong with it.</summary>
    public Task<string?> Finished { get; }

    /// <summary>
    /// Ends the recording now, leaving a playable file.
    /// </summary>
    /// <remarks>
    /// A q on stdin is ffmpeg's own "stop and close the file properly". Killing the process
    /// would be faster and would leave an mp4 with no moov atom, which is to say no mp4 —
    /// the whole point of stopping early is to keep what was recorded.
    /// </remarks>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            _process.StandardInput.Write('q');
            _process.StandardInput.Flush();
        }
        catch (Exception)
        {
            // Already finished on its own: the -t cap got there first, which is a stop too.
        }
    }

    private async Task<string?> Wait()
    {
        try
        {
            var errors = await _process.StandardError.ReadToEndAsync();
            await _process.WaitForExitAsync();
            _ended = DateTime.UtcNow;

            if (_process.ExitCode != 0)
                return string.IsNullOrWhiteSpace(errors) ? "ffmpeg failed" : errors.Trim();
            if (!File.Exists(_path))
                return "ffmpeg reported success but wrote nothing";

            Bytes = new FileInfo(_path).Length;
            if (_maxBytes is { } limit && Bytes > limit)
                return $"the clip came out at {Bytes / 1024 / 1024.0:F1} MB, over the 3 MB limit";

            return null;
        }
        finally
        {
            _process.Dispose();
        }
    }
}
