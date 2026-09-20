using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Greenlight.Sdk;
using Greenlight.Sdk.Protocol;

namespace Greenlight.GalleryCapture;

/// <summary>
/// Live / Grey / Green / Amber / Red / Blinking / Auto, floating in the top-left corner of the
/// screen for the whole session — the developer page's buttons, sent through the SDK as
/// <c>hold_indicators</c>.
/// </summary>
/// <remarks>
/// <para>
/// Its own window rather than a row on the panel because the panel hides itself for a
/// capture whenever it overlaps the frame, and these are the buttons you press <i>during</i>
/// the capture: go red before the still, start the blink partway through the clip, come
/// back to green for the last second. A clip of a client sitting green is a clip of nothing
/// happening.
/// </para>
/// <para>
/// Pinned to the corner, not to the frame, so it starts in the same place every time — and
/// excluded from capture, so it stays clickable while sitting inside a whole-screen shot
/// without appearing in it. Where Windows is too old for that exclusion it photographs like
/// anything else, which is still better than hiding it: that would take the buttons away at
/// the one moment they matter. It is draggable by the grip, or by any other part of it that
/// is not a pad, for when that corner already has something in it.
/// </para>
/// <para>
/// The order and the colours are the TestStrip's, deliberately. The two tools get reached for
/// in the same half hour and often sit on the same screen, and a Red that is the same red in
/// both is one less thing to translate.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StateStrip : Window
{
    /// <summary>Physical height, so the panel knows where the corner stops being spoken for.</summary>
    public const int PixelHeight = 64;

    // ── the TestStrip's colours ───────────────────────────────────────────────
    // Copied rather than referenced: this tool does not depend on the TestStrip, and four hex
    // values are a cheaper coupling than a package would be. If they ever drift, these are the
    // ones to bring back into line.

    private static readonly Color LiveColour = Color.FromRgb(0x7C, 0x88, 0x96);
    private static readonly Color GreyColour = Color.FromRgb(0x6A, 0x70, 0x79);
    private static readonly Color GreenColour = Color.FromRgb(0x4B, 0xFF, 0x86);
    private static readonly Color AmberColour = Color.FromRgb(0xFF, 0xCE, 0x42);
    private static readonly Color RedColour = Color.FromRgb(0xFF, 0x4E, 0x3C);

    /// <summary>The TestStrip's BUILDING toggle, which is not a status and so is not one of the four.</summary>
    private static readonly Color BlinkColour = Color.FromRgb(0x4F, 0xC3, 0xF7);

    /// <summary>Auto has no counterpart on the TestStrip, so it gets a colour none of them uses.</summary>
    private static readonly Color AutoColour = Color.FromRgb(0xB0, 0x84, 0xF5);

    /// <summary>The chassis an unlit pad is sunk into.</summary>
    private static readonly Color Chassis = Color.FromRgb(0x23, 0x2C, 0x27);

    /// <summary>Dark text on a lit pad, the way the TestStrip does it.</summary>
    private static readonly IBrush LitInk = new SolidColorBrush(Color.FromArgb(235, 0x0C, 0x0E, 0x12));

    private static readonly IBrush UnlitInk = new SolidColorBrush(Color.FromArgb(190, 0xE6, 0xF2, 0xEA));

    /// <summary>
    /// The cycle Auto runs: green, amber, red, round again.
    /// </summary>
    /// <remarks>
    /// Ten seconds all told, which is <see cref="Capture.ClipSeconds"/> exactly — one pass of
    /// this fills one clip at its default cap, whenever in the cycle the recording starts.
    /// </remarks>
    private static readonly (GreenlightStatus Status, double Seconds)[] AutoCycle =
    [
        (GreenlightStatus.Green, 3),
        (GreenlightStatus.Yellow, 3),
        (GreenlightStatus.Red, 4),
    ];

    private readonly GreenlightClient _greenlight;
    private readonly TextBlock _why = new()
    {
        FontSize = 11, TextWrapping = TextWrapping.NoWrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
    };

    private readonly Button _live;
    private readonly Button _grey;
    private readonly Button _green;
    private readonly Button _amber;
    private readonly Button _red;
    private readonly Button _blink;
    private readonly Button _auto;

    private GreenlightStatus? _held;
    private bool _pretendBuilding;

    private CancellationTokenSource? _cycling;
    private GreenlightStatus _phase = GreenlightStatus.Green;

    public StateStrip(GreenlightClient greenlight)
    {
        _greenlight = greenlight;

        Title = "Greenlight gallery capture";
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x17));

        _live = Chip("Live");
        _grey = Chip("Grey");
        _green = Chip("Green");
        _amber = Chip("Amber");
        _red = Chip("Red");
        _blink = Chip("Blinking");
        _auto = Chip("Auto");

        _live.Click += async (_, _) =>
        {
            StopCycling();
            var result = await _greenlight.ReleaseIndicatorsAsync();
            if (result.IsOk) { _held = null; _pretendBuilding = false; }
            Paint();
            Report(result, "Back to the real build state.");
        };

        Holds(_grey, GreenlightStatus.Unknown, "Grey");
        Holds(_green, GreenlightStatus.Green, "Green");
        Holds(_amber, GreenlightStatus.Yellow, "Amber");
        Holds(_red, GreenlightStatus.Red, "Red");

        _blink.Click += async (_, _) =>
        {
            // Pressing the blinking pad while Auto is running means "stop that" — Auto owns the
            // blink for as long as it runs, and stopping it puts the blink out. Toggling from
            // here as well would turn it straight back on.
            if (StopCycling())
            {
                var stopped = await Send();
                Paint();
                Report(stopped, "Auto stopped. The colour stays where the cycle left it.");
                return;
            }

            _pretendBuilding = !_pretendBuilding;
            var result = await Send();
            if (!result.IsOk) _pretendBuilding = !_pretendBuilding;
            Paint();
            Report(result, _pretendBuilding ? "Blinking as though a build were running." : "Blink off.");
        };

        _auto.Click += async (_, _) => await ToggleAuto();

        // Seven pads and a line of text leave nothing obvious to take hold of, and this
        // does have to be movable: the corner it starts in is the right corner most of the
        // time and in the way the rest of it — a client that lives up there, or a
        // whole-screen shot where the strip is over the subject rather than beside it.
        var grip = new TextBlock
        {
            Text = "⋮⋮", FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0x5E)),
        };
        ToolTip.SetTip(grip, "Drag to move the strip");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { grip, _live, _grey, _green, _amber, _red, _blink, _auto },
        };

        var chassis = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD7, 0x7F)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            // Painted in the window's own colour so there is something for the pointer to
            // land on. An unpainted border is a hole to hit testing, and the grip is only the
            // advertisement — the padding and the status line drag it too.
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x17)),
            Child = new StackPanel { Spacing = 4, Children = { row, _why } },
        };

        Content = chassis;
        DragToMove.By(chassis, this);

        // Both events arrive on a background thread.
        _greenlight.AvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _greenlight.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    /// <summary>Whether the Auto cycle is running, for anyone who needs to say so.</summary>
    public bool IsCycling => _cycling is not null;

    // ── the buttons ───────────────────────────────────────────────────────────

    private void Holds(Button button, GreenlightStatus status, string label) =>
        button.Click += async (_, _) =>
        {
            // Taking manual control ends the cycle. The blink goes with it, so that one command
            // below leaves the indicators in a state the pads actually agree with.
            StopCycling();

            var was = _held;
            _held = status;
            var result = await Send();
            if (!result.IsOk) _held = was;
            Paint();
            Report(result, $"Holding every indicator at {label}.");
        };

    /// <summary>
    /// Start the cycle, or stop it.
    /// </summary>
    /// <remarks>
    /// It starts green and blinking — a build running — then amber, then red, and round again,
    /// which is the whole of what a build indicator has to say in the length of one clip. It
    /// loops rather than running once so that a recording started at any point in it still
    /// catches all three.
    /// </remarks>
    private async Task ToggleAuto()
    {
        if (StopCycling())
        {
            var stopped = await Send();
            Paint();
            Report(stopped, "Auto stopped. The colour stays where the cycle left it.");
            return;
        }

        var cycling = new CancellationTokenSource();
        _cycling = cycling;
        _pretendBuilding = true;
        Paint();
        _why.Text = "Auto: green, amber, red, round again — ten seconds, which is one clip.";

        try
        {
            while (true)
            {
                foreach (var (status, seconds) in AutoCycle)
                {
                    _held = _phase = status;
                    var result = await Send();

                    // Checked after the await and not only at the top: a press that lands while
                    // that command is in flight has already set the state it wants, and the pads
                    // would otherwise be repainted from the phase this cycle was abandoning.
                    if (cycling.IsCancellationRequested) return;

                    // A refusal mid-cycle is worth stopping for: the rest of the loop would be
                    // ten seconds of sending commands nobody is acting on.
                    if (!result.IsOk)
                    {
                        StopCycling();
                        Paint();
                        Report(result, string.Empty);
                        return;
                    }

                    Paint();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cycling.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Somebody pressed something. Whoever cancelled has already sent, or is about to.
        }
        finally
        {
            cycling.Dispose();
        }
    }

    /// <summary>
    /// End the cycle if one is running, and say whether there was one.
    /// </summary>
    /// <remarks>
    /// The blink goes out here rather than in the caller because Auto is what turned it on, and
    /// a blink left running under a pad that is no longer lit would be the strip lying about
    /// what the machine is doing. The indicators are not touched: whatever asked for the stop
    /// sends its own command, so the whole change reaches Greenlight in one go.
    /// </remarks>
    private bool StopCycling()
    {
        if (_cycling is null) return false;

        var cycling = _cycling;
        _cycling = null;
        _pretendBuilding = false;

        // The token source is not disposed here. The loop is very likely sitting inside a
        // Task.Delay registered against it, and disposing it out from under that throws
        // ObjectDisposedException on a thread with nobody to catch it. Cancellation is what
        // ends the loop; the source goes when it does.
        cycling.Cancel();
        return true;
    }

    private Task<CommandResult> Send() =>
        _greenlight.HoldIndicatorsAsync(_held, _pretendBuilding ? true : null);

    // ── how they look ─────────────────────────────────────────────────────────

    /// <summary>
    /// Light the pad that is currently true and sink the rest into the chassis.
    /// </summary>
    /// <remarks>
    /// Which one is lit is the only thing on this strip that says what the machine is being
    /// told, and before the colours went on there was nothing saying it at all — six identical
    /// chips and a line of prose underneath.
    /// </remarks>
    private void Paint()
    {
        Light(_live, LiveColour, _held is null && _cycling is null);
        Light(_grey, GreyColour, _held == GreenlightStatus.Unknown);
        Light(_green, GreenColour, _held == GreenlightStatus.Green);
        Light(_amber, AmberColour, _held == GreenlightStatus.Yellow);
        Light(_red, RedColour, _held == GreenlightStatus.Red);
        Light(_blink, BlinkColour, _pretendBuilding);

        // Auto wears the colour of the phase it is in, so the cycle can be read off the strip
        // without watching the thing it is driving.
        Light(_auto, _cycling is null ? AutoColour : Colour(_phase), _cycling is not null);
        _auto.Content = _cycling is null ? "Auto" : "Stop auto";
        _blink.Content = _pretendBuilding && _cycling is null ? "Stop blink" : "Blinking";
    }

    private static void Light(Button button, Color colour, bool lit)
    {
        button.Background = new SolidColorBrush(lit ? colour : Blend(colour, Chassis, 0.74));
        button.Foreground = lit ? LitInk : UnlitInk;
    }

    private static Color Colour(GreenlightStatus status) => status switch
    {
        GreenlightStatus.Green => GreenColour,
        GreenlightStatus.Yellow => AmberColour,
        GreenlightStatus.Red => RedColour,
        _ => GreyColour,
    };

    /// <summary>Pull a colour towards another. Used to sink an unlit pad into the chassis.</summary>
    private static Color Blend(Color colour, Color towards, double amount)
    {
        var k = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(colour.R + (towards.R - colour.R) * k),
            (byte)(colour.G + (towards.G - colour.G) * k),
            (byte)(colour.B + (towards.B - colour.B) * k));
    }

    private static Button Chip(string text) => new()
    {
        Content = text, Padding = new Thickness(12, 5), FontSize = 12,
        MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center,
        Foreground = UnlitInk,
        Background = new SolidColorBrush(Chassis),
    };

    private void Refresh()
    {
        var offered = _greenlight.Availability == GreenlightAvailability.Connected
                      && _greenlight.SupportedCommands.Contains(CommandName.HoldIndicators);

        foreach (var button in new[] { _live, _grey, _green, _amber, _red, _blink, _auto })
            button.IsEnabled = offered;

        // A cycle left running against a Greenlight that has gone away would go on sending
        // commands into nothing, and the pads would go on claiming it was working.
        if (!offered && StopCycling()) Paint();

        _why.Text = offered
            ? "Every indicator Greenlight drives follows these. It lets go when this tool closes."
            : _greenlight.Availability != GreenlightAvailability.Connected
                ? "Greenlight is not running, so its state cannot be driven from here."
                : _greenlight.SupportedCommands.Count == 0
                    ? "Greenlight's local API is read-only. Settings → Local API → allow commands."
                    : $"This Greenlight ({_greenlight.HostVersion}) predates hold_indicators; 1.0.20 or later has it.";

        Paint();
    }

    private void Report(CommandResult result, string didWhat)
    {
        if (result.IsOk)
        {
            if (didWhat.Length > 0) _why.Text = didWhat;
            return;
        }

        _why.Text = $"Greenlight refused: {result.Message ?? result.Error ?? result.Outcome.ToString()}";
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Native.MakeToolWindow(handle);
        Native.ExcludeFromCapture(handle);

        // The corner of the primary screen. Not the frame's screen: the frame moves, and the
        // point of the strip is to be in the same place every time.
        var primary = Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        Position = new PixelPoint(primary.X + 16, primary.Y + 16);
        RaiseWithoutFocus();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCycling();
        base.OnClosed(e);
    }

    /// <summary>Back to the top of the topmost band, without taking focus from anything.</summary>
    public void RaiseWithoutFocus() =>
        Native.RaiseWithoutFocus(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
}
