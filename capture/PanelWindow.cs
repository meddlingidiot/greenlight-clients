using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Greenlight.Sdk;
using Greenlight.Sdk.Protocol;

namespace Greenlight.GalleryCapture;

/// <summary>
/// Everything that is not the viewfinder: take the shot, answer five questions, get a
/// directory you can commit.
/// </summary>
/// <remarks>
/// A separate window from the frame because the frame has a hole in it — a control placed
/// over the hole would be clipped out of existence along with it. Keeping the chrome in its
/// own window also keeps it out of the shot, since the capture area is exactly the hole.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PanelWindow : Window
{
    private readonly ViewfinderWindow _viewfinder;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly Border _shell;

    private string? _posterPath;
    private Action? _shoot;

    /// <summary>
    /// Start or stop a recording, when there is a shoot page for it to mean something on.
    /// </summary>
    /// <remarks>
    /// The same door the Record button goes through, kept here so the hotkey can use it
    /// without holding on to buttons that are rebuilt every time the page is.
    /// </remarks>
    private Action? _roll;

    private Hotkeys? _hotkeys;

    /// <summary>The shoot page's status line, for the few things said from outside it.</summary>
    private TextBlock? _status;
    private string? _clipPath;
    private string _greenlightVersion = "";
    private string? _sdkVersion;

    /// <summary>Whole-screen mode: no frame, no dim, just the two buttons.</summary>
    private bool _fullscreen;
    private int _screenIndex;
    private PixelBox _framed;
    private Recording? _recording;

    /// <summary>What the next recording is capped at.</summary>
    private int _clipSeconds = Capture.ClipSeconds;

    /// <summary>
    /// Whether the length button is on show.
    /// </summary>
    /// <remarks>
    /// Hidden until somebody holds the Record button down, because a listing's clip is capped
    /// at fifteen seconds and wants to be shorter than that — a length control sitting there
    /// by default would read as an invitation to record forty seconds of a build light, which
    /// is the wrong thing to put on a card. The people who want a longer take want it for
    /// something else, and they can be expected to go and find it.
    /// </remarks>
    private bool _longTakes;

    /// <summary>
    /// Whether Windows agreed to keep this window out of screen captures. When it did, the
    /// panel can sit anywhere — including inside the shot — and the capture still only sees
    /// the client underneath. When it did not, the panel gets out of the way the old way.
    /// </summary>
    private bool _invisibleToCapture;

    /// <summary>
    /// Whether somebody has put this window somewhere themselves.
    /// </summary>
    /// <remarks>
    /// Once they have, <see cref="FollowFrame"/> stops placing it: a panel that springs back
    /// beside the frame the next time the frame is nudged is a panel that cannot be moved at
    /// all. Re-aiming the capture — whole screen, a different monitor — hands the placement
    /// back, because where the bar goes is part of what those two do.
    /// </remarks>
    private bool _placedByHand;

    /// <summary>Set the moment this window's close is certain, and never unset.</summary>
    private bool _closing;
    private bool _torndown;

    /// <summary>
    /// One connection for the life of the tool. Kept open rather than opened per question
    /// because a hold placed through it is released by Greenlight the moment it closes —
    /// so quitting this window, however it is quit, puts the user's real light back.
    /// </summary>
    private readonly GreenlightClient _greenlight = new();
    private readonly StateStrip _states;
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), "greenlight-capture", Guid.NewGuid().ToString("n")[..8]);

    public PanelWindow(ViewfinderWindow viewfinder)
    {
        _viewfinder = viewfinder;
        Directory.CreateDirectory(_workingDirectory);

        Title = "Greenlight gallery capture";
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x17));

        Content = _shell = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD7, 0x7F)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
            // Painted rather than left unpainted, in the window's own colour, so the shell
            // is something the pointer can land on: an unpainted border is a hole as far as
            // hit testing goes, and a handle nobody can hit is not a handle.
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x17)),
            Child = _body,
        };

        // No title bar, so the chassis is the handle. Moving it by hand also takes the
        // placement over from FollowFrame, which would otherwise put it straight back.
        DragToMove.By(_shell, this, () => _placedByHand = true);

        _viewfinder.RegionChanged += (_, _) => Dispatcher.UIThread.Post(FollowFrame);
        // The panel is sized by its content, so a longer status line makes it taller after
        // it has already been placed — and a bar placed by its bottom edge would drift down
        // over the taskbar. Re-placing on resize costs nothing: this only moves the window.
        SizeChanged += (_, _) => FollowFrame();
        // Enter on the frame is the poster button, so it also lights up "Next".
        _viewfinder.Accepted += (_, _) => Dispatcher.UIThread.Post(() => _shoot?.Invoke());
        // Esc on the frame ends the whole thing. Closing only the frame would leave this
        // panel up with no frame, no shot, and no way out.
        _viewfinder.Cancelled += (_, _) => Dispatcher.UIThread.Post(Close);
        // Every click on the dim activates the frame, which would pull it over us.
        _viewfinder.Touched += (_, _) => Dispatcher.UIThread.Post(StayAboveTheFrame);

        _states = new StateStrip(_greenlight);
        _states.Show();

        ShowShootPage();
        _ = AskGreenlightWhatVersionItIs();
    }

    // --- chrome helpers ---------------------------------------------------------------

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xEA)),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 430,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
    };

    private static Button Action(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 7) };
        button.Foreground = primary
            ? new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x0E))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xEA));
        button.Background = primary
            ? new SolidColorBrush(Color.FromRgb(0x3F, 0xD7, 0x7F))
            : new SolidColorBrush(Color.FromRgb(0x23, 0x2C, 0x27));
        return button;
    }

    private (StackPanel Row, TextBox Box) Field(string label, string placeholder, string value = "", int lines = 1)
    {
        var box = new TextBox
        {
            Text = value, PlaceholderText = placeholder, Width = 430,
            AcceptsReturn = lines > 1, Height = lines > 1 ? 66 : double.NaN,
            TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
        });
        row.Children.Add(box);
        return (row, box);
    }

    private void Say(string what)
    {
        if (_status is not null) _status.Text = what;
    }

    private void StayAboveTheFrame()
    {
        Native.RaiseWithoutFocus(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        _states.RaiseWithoutFocus();
    }

    /// <summary>
    /// A close control on every page, because the window has no title bar to put one on —
    /// and the row it sits in is the title bar's other job, which is being somewhere to
    /// grab the window by.
    /// </summary>
    /// <remarks>
    /// The whole shell drags; this row is only where it is advertised, with the cursor a
    /// title bar would show. The close button opts back out of that cursor: Avalonia inherits
    /// it down the tree, and a button that looks draggable reads as one that will not click.
    /// </remarks>
    private Control Chrome(string heading)
    {
        var quit = QuitButton();
        quit.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        DockPanel.SetDock(quit, Dock.Right);

        var row = new DockPanel
        {
            Width = 430,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll),
            Children = { quit, Heading(heading) },
        };
        ToolTip.SetTip(row, "Drag to move this window");
        return row;
    }

    private Button QuitButton()
    {
        var quit = new Button
        {
            Content = "✕", Padding = new Thickness(8, 2), FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
        };
        ToolTip.SetTip(quit, "Quit (Esc)");
        quit.Click += (_, _) => Close();
        return quit;
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape) Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _invisibleToCapture = Native.ExcludeFromCapture(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        // Posted to this window, so they arrive whatever has focus — and whether or not this
        // window is on screen at the time. Both go through the same doors the buttons do, so
        // a press with no shoot page under it is a press that does nothing.
        _hotkeys = new Hotkeys(this, () => _shoot?.Invoke(), () => _roll?.Invoke());
        if (_hotkeys.Trouble is { } trouble) Dispatcher.UIThread.Post(() => Say(trouble));

        AdoptByTheFrame();
        StayAboveTheFrame();
    }

    /// <summary>
    /// The frame owns this window and the state strip, so both sit above it no matter what
    /// was clicked. Owner and owned must both exist, so this runs once each is open.
    /// </summary>
    private void AdoptByTheFrame()
    {
        // Once the close is under way the links are being let go of, not remade: a fresh one
        // here would hand the frame another window to destroy on its way out.
        if (_closing) return;

        var frame = _viewfinder.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Native.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, frame);
        Native.SetOwner(_states.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, frame);
    }

    /// <summary>
    /// Sits beside the frame — below, above, right or left, whichever fits on the screen —
    /// and never inside it, until somebody picks it up and puts it somewhere else.
    /// </summary>
    /// <remarks>
    /// The first version tried below and then above and otherwise gave up, which put the
    /// panel's own buttons in the top-left of the very first poster ever taken. Inside the
    /// frame is the one place this window must not be, so when the frame is so large that
    /// nothing fits beside it, <see cref="OutOfShot"/> hides the panel for the capture.
    /// </remarks>
    private void FollowFrame()
    {
        // The frame's first region change is it finishing opening — the earliest moment it
        // has a handle to own anything with.
        AdoptByTheFrame();
        StayAboveTheFrame();

        // Placed by hand, so the only thing left to do is make sure it can still be reached:
        // this runs on every resize, and the details page is a good deal taller than the
        // shutter bar it replaces.
        if (_placedByHand)
        {
            KeepOnScreen();
            return;
        }

        var region = _viewfinder.Region;
        var width = (int)(Bounds.Width * RenderScaling);
        var height = (int)(Bounds.Height * RenderScaling);
        const int gap = 16;

        var found = Screens.ScreenFromPoint(new PixelPoint(region.X, region.Y));
        var screen = found?.Bounds
                     ?? new PixelRect(region.X, region.Y, region.Width, region.Height);

        // Whole-screen mode has no beside to sit in, so the bar sits along the bottom of the
        // shot, clear of the taskbar — where a shutter button belongs, and where the capture
        // exclusion means it costs the picture nothing.
        if (_fullscreen)
        {
            // Clear of the taskbar by more than the gap used elsewhere: the window's shadow
            // is not part of the bounds measured here, and a bar that overlaps the taskbar
            // is a bar that covers the thing people click next.
            var usable = found?.WorkingArea ?? screen;
            Position = new PixelPoint(
                region.X + (region.Width - width) / 2,
                Math.Max(usable.Y, usable.Y + usable.Height - height - 3 * gap));
            return;
        }

        var candidates = new[]
        {
            new PixelPoint(region.X, region.Bottom + gap),
            new PixelPoint(region.X, region.Y - height - gap),
            new PixelPoint(region.Right + gap, region.Y),
            new PixelPoint(region.X - width - gap, region.Y),
        };
        foreach (var candidate in candidates)
        {
            var box = new PixelRect(candidate, new PixelSize(width, height));
            if (screen.Contains(box.TopLeft) && screen.Contains(box.BottomRight))
            {
                Position = candidate;
                return;
            }
        }

        // Nothing fits beside it. Park under the state strip in the screen's corner; the
        // capture hides us.
        Position = new PixelPoint(screen.X + gap, screen.Y + StateStrip.PixelHeight + 2 * gap);
    }

    /// <summary>
    /// Pulls the window back inside its screen's working area.
    /// </summary>
    /// <remarks>
    /// The only placement left to do once somebody has placed it themselves. It is a resize
    /// that needs it rather than a move: the window is sized by its content, so answering the
    /// five questions grows a bar that was dropped at the bottom of the screen straight down
    /// past the edge of it — and this window has no title bar to grab a window back by.
    /// </remarks>
    private void KeepOnScreen()
    {
        var width = (int)(Bounds.Width * RenderScaling);
        var height = (int)(Bounds.Height * RenderScaling);

        var screen = (Screens.ScreenFromPoint(Position) ?? Screens.Primary)?.WorkingArea;
        if (screen is not { } usable) return;

        Position = new PixelPoint(
            Math.Clamp(Position.X, usable.X, Math.Max(usable.X, usable.Right - width)),
            Math.Clamp(Position.Y, usable.Y, Math.Max(usable.Y, usable.Bottom - height)));
    }

    /// <summary>True when any of this window overlaps the capture area <i>and</i> would be
    /// photographed — a window Windows keeps out of captures can overlap all it likes.</summary>
    private bool InShot()
    {
        if (_invisibleToCapture) return false;

        var region = _viewfinder.Region;
        var mine = new PixelRect(Position, new PixelSize(
            (int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)));
        return mine.Intersects(new PixelRect(region.X, region.Y, region.Width, region.Height));
    }

    /// <summary>
    /// Runs a capture with this window guaranteed out of it. Normally that costs nothing —
    /// the panel sits beside the frame — but a frame that fills the screen leaves nowhere
    /// to sit, and then the only honest answer is to get out of the way for a moment.
    /// </summary>
    private async Task OutOfShot(Func<Task> capture)
    {
        var hidden = await StepOutOfShot();
        try { await capture(); }
        finally { StepBackIn(hidden); }
    }

    /// <summary>Gets out of the way if being in it would show, and says whether it had to.</summary>
    private async Task<bool> StepOutOfShot()
    {
        if (!InShot()) return false;
        Hide();
        // Let the compositor actually remove us before the first frame is grabbed.
        await Task.Delay(250);
        return true;
    }

    private void StepBackIn(bool hidden)
    {
        if (!hidden) return;
        Show();
        StayAboveTheFrame();
    }

    // --- page one: the shot -----------------------------------------------------------

    /// <summary>
    /// The shot, in whichever of the two shapes is wanted: the wizard page with a frame to
    /// drag, or — in whole-screen mode — a shutter bar along the bottom of the screen it is
    /// about to photograph.
    /// </summary>
    private void ShowShootPage()
    {
        if (!_fullscreen) _viewfinder.Show();
        StayAboveTheFrame();
        _body.Children.Clear();

        // The keys are the only part of this tool with nothing on screen to give them away,
        // so the line that is otherwise empty until something happens says them.
        var status = Note($"{Hotkeys.SnapshotChord} takes the still, {Hotkeys.RecordChord} starts and "
                          + "stops a clip — from anywhere, so the app being photographed can keep the focus.");
        _status = status;
        var poster = Action("Snapshot", primary: true);
        var record = Action(RecordLabel);
        var length = Action(LengthLabel);
        var next = Action(_fullscreen ? "Next →" : "Next: the details");
        // A poster taken before the mode was switched is still a poster.
        next.IsEnabled = _posterPath is not null;

        length.IsVisible = _longTakes;
        ToolTip.SetTip(length, "How long the next recording may run before it stops itself");
        length.Click += (_, _) =>
        {
            var lengths = Capture.ClipLengths;
            var at = Array.IndexOf(lengths, _clipSeconds);
            _clipSeconds = lengths[(at + 1) % lengths.Length];
            length.Content = LengthLabel;
            status.Text = _clipSeconds <= Capture.SchemaClipSeconds
                ? $"The next take stops itself at {_clipSeconds} seconds."
                : $"The next take stops itself at {_clipSeconds} seconds — past the "
                  + $"{Capture.SchemaClipSeconds} a listing's clip may be, so it will be kept as a file "
                  + "rather than attached to the listing.";
        };

        // The keys are worth saying on the buttons themselves: the whole point of them is to
        // be pressed while this window is somewhere else, or hidden, and nobody goes looking
        // for a hotkey they have not been told about.
        ToolTip.SetTip(poster, $"Take the still ({Hotkeys.SnapshotChord}, from anywhere)");
        ToolTip.SetTip(record, $"Start or stop recording ({Hotkeys.RecordChord}, from anywhere). "
                               + "Hold to unlock longer takes.");

        HoldToUnlock(record, length, status);

        _shoot = async () =>
        {
            if (_recording is not null) return;
            await OutOfShot(() => { TakePoster(); return Task.CompletedTask; });
            status.Text = $"Poster saved. {new FileInfo(_posterPath!).Length / 1024} KB, comfortably inside the 1 MB limit.";
            next.IsEnabled = true;
        };
        _roll = async void () => await Record(record, poster, next, status);
        poster.Click += (_, _) => _shoot?.Invoke();
        record.Click += (_, _) => _roll?.Invoke();
        next.Click += (_, _) => ShowDetailsPage();

        var mode = Action(_fullscreen ? "Frame a region" : "Whole screen");
        ToolTip.SetTip(mode, _fullscreen
            ? "Back to a draggable 16:9 frame"
            : "Capture an entire monitor instead of a frame");
        mode.Click += (_, _) => UseFullscreen(!_fullscreen);

        if (_fullscreen) ShowShutterBar(poster, record, length, next, mode, status);
        else ShowFramingPage(poster, record, length, next, mode, status);

        // The bar is positioned from its own size, which is not known until this has been
        // measured — so the move waits for the layout it depends on.
        Dispatcher.UIThread.Post(FollowFrame, DispatcherPriority.Loaded);
    }

    private string RecordLabel => _fullscreen ? "● Record" : "● Record a clip";

    private string LengthLabel => $"{_clipSeconds}s";

    /// <summary>
    /// Reveal the length button — or put it away again — on a three-second press of Record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A press and not a modifier key or a menu, because the shutter bar has neither: in
    /// whole-screen mode this window is one row of buttons over the taskbar, and the thing
    /// being hidden is one button that belongs next to Record anyway.
    /// </para>
    /// <para>
    /// The press that unlocks must not also start a recording. The timer fires while the
    /// button is still down, so the flag it sets is already there when Avalonia raises Click
    /// on the way back up — which is the one ordering this relies on.
    /// </para>
    /// </remarks>
    private void HoldToUnlock(Button record, Button length, TextBlock status)
    {
        var held = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        held.Tick += (_, _) =>
        {
            held.Stop();
            _unlockedByHold = true;
            _longTakes = !_longTakes;

            length.IsVisible = _longTakes;
            if (!_longTakes) _clipSeconds = Capture.ClipSeconds;
            length.Content = LengthLabel;

            status.Text = _longTakes
                ? $"Longer takes unlocked. {LengthLabel} is the cap; press it to change, and hold Record "
                  + "again to put it away."
                : $"Back to the usual {Capture.ClipSeconds} seconds.";
        };

        record.AddHandler(PointerPressedEvent, (_, _) =>
        {
            // Cleared here rather than only where it is read: a hold that ended with the
            // pointer dragged off the button never becomes a Click, and the flag would
            // otherwise still be set when somebody pressed Record for real a minute later.
            _unlockedByHold = false;
            held.Start();
        }, RoutingStrategies.Tunnel);

        record.AddHandler(PointerReleasedEvent, (_, _) => held.Stop(), RoutingStrategies.Tunnel);

        // Direct, because that is how Avalonia raises this one — a tunnelling handler for it
        // is never called, and the timer would go on running after the pointer was gone.
        record.AddHandler(PointerCaptureLostEvent, (_, _) => held.Stop(), RoutingStrategies.Direct);
    }

    /// <summary>Set by the hold, and eaten by the Click it would otherwise have turned into.</summary>
    private bool _unlockedByHold;

    /// <summary>Whole-screen mode: one row, no prose, parked over the taskbar.</summary>
    private void ShowShutterBar(Button poster, Button record, Button length, Button next, Button mode,
        TextBlock status)
    {
        _shell.Padding = new Thickness(10, 8);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { poster, record, length },
        };

        // Only worth the width when there is a second monitor to send it to.
        if (Screens.All.Count > 1)
        {
            var screen = Action($"Screen {_screenIndex + 1}/{Screens.All.Count}");
            ToolTip.SetTip(screen, "Capture the next monitor along");
            screen.Click += (_, _) => NextScreen();
            row.Children.Add(screen);
        }

        row.Children.Add(mode);
        row.Children.Add(next);
        row.Children.Add(QuitButton());

        status.MaxWidth = 520;
        // The keys are worth repeating here above anywhere else: this is the mode where the
        // bar hides itself mid-capture, and a hidden bar is a bar with nothing to press.
        status.Text = (_invisibleToCapture
            ? "The whole screen is the shot. This bar is not in it. "
            : "The whole screen is the shot. This Windows cannot hide the bar from a capture, so it "
              + "disappears while the shutter works. ")
            + $"{Hotkeys.SnapshotChord} shoots, {Hotkeys.RecordChord} rolls.";

        _body.Children.Add(row);
        _body.Children.Add(status);
    }

    /// <summary>The original page: a frame to drag, and room to explain it.</summary>
    private void ShowFramingPage(Button poster, Button record, Button length, Button next, Button mode,
        TextBlock status)
    {
        _shell.Padding = new Thickness(18);
        poster.Content = "Take the poster";

        _body.Children.Add(Chrome("Frame your client"));
        _body.Children.Add(Note(
            "Drag the frame over it, resize from the corners. The area inside the frame is a real hole " +
            "in this overlay — you can click straight through it to set your client up, and what you see " +
            "there is exactly what gets captured. Or take the whole screen instead."));

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { poster, record, length, mode, next },
        });
        _body.Children.Add(status);
    }

    // --- whole-screen mode ------------------------------------------------------------

    /// <summary>
    /// Swaps between the frame and the whole screen, remembering where the frame was.
    /// </summary>
    /// <remarks>
    /// The frame is hidden rather than resized to the monitor: at that size the dim has
    /// nothing left to dim, the corner handles sit off the edge of the screen, and all it
    /// would contribute is a window between the person and the client they are trying to
    /// photograph. The region it holds is still what gets captured — the frame is just not
    /// drawn for it.
    /// </remarks>
    private void UseFullscreen(bool on)
    {
        if (_recording is not null) return;

        _fullscreen = on;
        // A re-aim is a re-placement: the bar belongs along the bottom of the screen it is
        // pointed at, and the panel belongs beside the frame it has just got back.
        _placedByHand = false;
        if (on)
        {
            _framed = _viewfinder.Region;
            _screenIndex = ScreenUnderTheFrame();
            _viewfinder.Hide();
            _viewfinder.Aim(WholeScreen());
        }
        else
        {
            _viewfinder.Aim(_framed);
            _viewfinder.Show();
        }
        ShowShootPage();
    }

    private void NextScreen()
    {
        _screenIndex = (_screenIndex + 1) % Math.Max(1, Screens.All.Count);
        // The bar has to go to the monitor it is now photographing, wherever it was dropped.
        _placedByHand = false;
        _viewfinder.Aim(WholeScreen());
        ShowShootPage();
    }

    /// <summary>The monitor the whole-screen mode is pointed at, in physical pixels.</summary>
    private PixelBox WholeScreen()
    {
        var screens = Screens.All;
        var bounds = screens[Math.Clamp(_screenIndex, 0, screens.Count - 1)].Bounds;
        return new PixelBox(bounds.X, bounds.Y, bounds.Width, bounds.Height).Evened();
    }

    /// <summary>Which monitor to start on: the one the frame was already over.</summary>
    private int ScreenUnderTheFrame()
    {
        var region = _viewfinder.Region;
        var middle = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
        var screens = Screens.All;
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Bounds.Contains(middle)) return i;
        return 0;
    }

    // --- the clip ---------------------------------------------------------------------

    /// <summary>
    /// Start, or stop. One button either way, because the thing being recorded is a client
    /// doing something — and only the person watching it knows when that finished.
    /// </summary>
    /// <remarks>
    /// The <see cref="Capture.ClipSeconds"/> cap still applies, so a forgotten recording
    /// ends on its own; stopping early is the normal case rather than the exception, since
    /// most of what a build indicator does it does in about three seconds. Where the panel
    /// has to hide for the capture there is nothing to press, and the cap is the only way it
    /// ends — which the status line says before it disappears.
    /// </remarks>
    private async Task Record(Button record, Button poster, Button next, TextBlock status)
    {
        // The press that just unlocked the length button was a hold, not a click on Record.
        if (_unlockedByHold)
        {
            _unlockedByHold = false;
            return;
        }

        if (_recording is { } running)
        {
            running.Stop();
            record.IsEnabled = false;
            record.Content = "Finishing…";
            return;
        }

        if (!Capture.HasFfmpeg)
        {
            status.Text = "ffmpeg is not on PATH, so the clip is out — the poster alone makes a perfectly "
                        + "good listing. To add one: winget install Gyan.FFmpeg";
            return;
        }

        var path = Path.Combine(_workingDirectory, "clip.mp4");
        var seconds = _clipSeconds;
        var hidden = await StepOutOfShot();
        var (recording, failure) = Capture.StartClip(_viewfinder.Region, path, seconds);
        if (recording is null)
        {
            StepBackIn(hidden);
            status.Text = "No clip: " + failure;
            return;
        }

        // The old file is gone the moment ffmpeg opens the new one, so a failed second take
        // costs the first take too. Better said now than discovered in the listing.
        _recording = recording;
        _clipPath = null;
        poster.IsEnabled = next.IsEnabled = false;
        record.Content = "■ Stop";
        status.Text = hidden
            ? $"Recording with the panel hidden. {Hotkeys.RecordChord} stops it; failing that it ends "
              + $"itself at {seconds} seconds."
            : $"Recording. Stop when the client has said its piece — the button or {Hotkeys.RecordChord}; "
              + $"{seconds} seconds is the cap.";

        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ticker.Tick += (_, _) => record.Content = $"■ Stop  {recording.Elapsed.TotalSeconds:0}s";
        ticker.Start();

        var verdict = await recording.Finished;

        ticker.Stop();
        _recording = null;
        StepBackIn(hidden);
        poster.IsEnabled = record.IsEnabled = true;
        next.IsEnabled = _posterPath is not null;
        record.Content = RecordLabel;

        if (verdict is not null)
        {
            status.Text = "No clip: " + verdict;
            return;
        }

        // Length is what decides this, not the cap it was started with: a two-minute take
        // stopped after eight seconds is an ordinary clip, and pretending otherwise would
        // throw away a perfectly good one for the setting it happened to be recorded under.
        var ran = recording.Elapsed.TotalSeconds;
        var megabytes = recording.Bytes / 1024 / 1024.0;

        if (ran <= Capture.SchemaClipSeconds && recording.Bytes <= Capture.ClipMaxBytes)
        {
            _clipPath = path;
            status.Text = $"Clip saved. {megabytes:F1} MB of the 3 MB allowed, about {ran:0} seconds.";
        }
        else
        {
            // Kept, and said plainly. Attaching it anyway would put the listing through the
            // gallery's checks to find out the same thing an hour later.
            status.Text = $"Recorded {ran:0} seconds, {megabytes:F1} MB — too {(ran > Capture.SchemaClipSeconds
                ? $"long for a listing's clip, which stops at {Capture.SchemaClipSeconds} seconds"
                : "big for a listing's clip, which stops at 3 MB")}. "
                        + $"The file is at {path} if you want it for something else.";
        }
    }

    private void TakePoster()
    {
        // Nothing of ours is drawn inside the frame — the hole is cut out of the window
        // region, so the dimming stops at its edge. That is why neither window has to be
        // hidden for the grab, and why what you framed is what lands in the file.
        _posterPath = Path.Combine(_workingDirectory, "poster.jpg");
        Capture.Poster(_viewfinder.Region, _posterPath);
    }

    // --- page two: the words ----------------------------------------------------------

    private void ShowDetailsPage()
    {
        // The shot is taken and the clip is recorded; the frame has nothing left to do, and
        // a topmost window spanning every screen is exactly the thing that fights the text
        // boxes for focus. Back brings it back.
        _viewfinder.Hide();
        _shoot = null;
        _roll = null;
        _body.Children.Clear();
        _body.Children.Add(Chrome("Five things, then you are done"));

        var (nameRow, name) = Field("Name", "Cars");
        var (taglineRow, tagline) = Field("One line", "Cars that pile up at a red light when a pipeline breaks");
        var (descriptionRow, description) = Field("A paragraph", "What it shows, where it sits, what it does when the build breaks.", lines: 3);
        var (authorRow, author) = Field("Your name", "Jane Developer");
        var (repositoryRow, repository) = Field("Repository", "https://github.com/you/your-client");

        var integration = new ComboBox { Width = 430, ItemsSource = new[] { "sdk", "protocol" }, SelectedIndex = 0 };
        var buildTime = new ComboBox
        {
            Width = 430, SelectedIndex = 0,
            ItemsSource = new[] { "an evening", "a weekend", "a few days", "longer" },
        };
        var license = new TextBox { Text = "MIT", Width = 430 };

        foreach (var row in new[] { nameRow, taglineRow, descriptionRow, authorRow, repositoryRow })
            _body.Children.Add(row);

        _body.Children.Add(new TextBlock { Text = "Built on", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)) });
        _body.Children.Add(integration);
        _body.Children.Add(new TextBlock { Text = "Licence", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)) });
        _body.Children.Add(license);
        _body.Children.Add(new TextBlock { Text = "Took about", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)) });
        _body.Children.Add(buildTime);

        var attestation = new CheckBox
        {
            Content = "These images are mine — my own app, my own artwork",
            IsChecked = true,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xEA)),
        };
        _body.Children.Add(attestation);
        _body.Children.Add(Note(
            "If they are not — if there is somebody else's artwork in the frame — untick this and add the "
            + "licence by hand. Original assets only, and describe by function rather than by franchise."));

        _body.Children.Add(Note(_greenlightVersion.Length > 0
            ? $"Verified against Greenlight {_greenlightVersion}, filled in for you from the one running here."
            : "Greenlight is not running, so last-verified will need the version typing in by hand."));

        var status = Note("");
        var write = Action("Write the listing", primary: true);
        write.Click += (_, _) =>
        {
            if (name.Text is not { Length: > 0 } || repository.Text is not { Length: > 0 })
            {
                status.Text = "A name and a repository, at least.";
                return;
            }

            var slug = Listing.Slugify(name.Text);
            var listing = new Listing
            {
                Slug = slug,
                Name = name.Text.Trim(),
                Tagline = tagline.Text?.Trim() is { Length: > 0 } t ? t : name.Text.Trim(),
                Description = description.Text?.Trim() is { Length: > 0 } d ? d : tagline.Text?.Trim() ?? name.Text.Trim(),
                Author = new Listing.Person { Name = author.Text?.Trim() is { Length: > 0 } a ? a : "Unknown" },
                Repository = repository.Text.Trim(),
                License = license.Text?.Trim() is { Length: > 0 } l ? l : "MIT",
                Language = "C#",
                Integration = (string)(integration.SelectedItem ?? "sdk"),
                BuildTime = (string?)buildTime.SelectedItem,
                Assets = new Listing.Media
                {
                    Poster = "poster.jpg",
                    Clip = _clipPath is null ? null : "clip.mp4",
                    Attestation = attestation.IsChecked == true ? "original" : "licensed",
                    AttestationNote = attestation.IsChecked == true ? null : "TODO: say where these came from, and under what licence",
                },
                LastVerified = new Listing.Verified
                {
                    Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    Greenlight = _greenlightVersion.Length > 0 ? _greenlightVersion : "0.0",
                    Sdk = (string?)integration.SelectedItem == "sdk" ? _sdkVersion ?? "1.2.0" : null,
                },
            };

            try
            {
                ShowDonePage(Write(listing));
            }
            catch (Exception error)
            {
                status.Text = "Could not write it: " + error.Message;
            }
        };

        var back = Action("← Back");
        back.Click += (_, _) => ShowShootPage();

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { back, write },
        });
        _body.Children.Add(status);
    }

    /// <summary>
    /// Writes straight into this repository's listings/ when the tool is run from inside a
    /// clone, which is the normal case, and onto the desktop otherwise.
    /// </summary>
    private string Write(Listing listing)
    {
        var repository = FindRepositoryRoot();
        var destination = repository is not null
            ? Path.Combine(repository, "listings", listing.Slug)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "listing-" + listing.Slug);

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "listing.json"), listing.ToJson());
        File.Copy(_posterPath!, Path.Combine(destination, "poster.jpg"), overwrite: true);
        if (_clipPath is not null) File.Copy(_clipPath, Path.Combine(destination, "clip.mp4"), overwrite: true);
        return destination;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "schema", "listing.schema.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private void ShowDonePage(string destination)
    {
        _viewfinder.Hide();
        _body.Children.Clear();
        _body.Children.Add(Chrome("That is a listing"));
        _body.Children.Add(Note(destination));
        _body.Children.Add(Note(
            "Check it with  python tools/validate.py  then commit the directory and open a pull request. "
            + "It appears in the gallery within half an hour of merge."));

        var open = Action("Open the folder", primary: true);
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{destination}\"") { UseShellExecute = true });

        var done = Action("Done");
        done.Click += (_, _) => Close();

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, done },
        });
    }

    // --- the one field worth not asking for -------------------------------------------

    /// <summary>
    /// last-verified.greenlight is meant to be the version you actually ran against. Asking
    /// the Greenlight running on this machine is both less work for the contributor and
    /// considerably more likely to be true than asking them to go and look it up.
    /// </summary>
    private async Task AskGreenlightWhatVersionItIs()
    {
        try
        {
            await _greenlight.StartAsync();

            // Absent is a normal state, so this waits briefly and then gives up quietly
            // rather than treating "no Greenlight" as a failure. The client keeps retrying
            // in the background regardless, for the state buttons.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (_greenlight.HostVersion is { Length: > 0 } version)
                {
                    _greenlightVersion = version;
                    break;
                }
                await Task.Delay(150);
            }

            _sdkVersion = typeof(GreenlightClient).Assembly.GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : null;
        }
        catch (Exception)
        {
            // A tool that will not start because a nicety failed is a worse tool.
        }
    }

    /// <summary>
    /// Hands the panel and the strip back to themselves before anything is destroyed.
    /// </summary>
    /// <remarks>
    /// <see cref="Native.SetOwner"/> made the frame their Win32 owner, which Win32 reads as
    /// permission to destroy them when the frame goes. That cascade lands in the middle of
    /// Avalonia's own teardown and leaves the frame's close unfinished, which in turn leaves
    /// the application lifetime convinced a window is still open — so it cancels its
    /// shutdown and the process outlives its last window. Closing runs before any window is
    /// destroyed, so it is the moment to break the links.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        _closing = true;
        // Before the window is destroyed, while there is still something to take the hook off.
        _hotkeys?.Dispose();
        Native.ClearOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        Native.ClearOwner(_states.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Belt and braces for the cascade above: should anything still close this window a
        // second time, the frame and the strip must not be told to close from inside their
        // own teardown.
        if (_torndown) return;
        _torndown = true;

        // An ffmpeg left running would keep grabbing the screen after its window is gone.
        _recording?.Stop();
        _viewfinder.Close();
        _states.Close();
        // Detaching is what releases any hold this tool placed; Greenlight does that itself.
        _ = _greenlight.DisposeAsync().AsTask();
    }
}
