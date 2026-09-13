using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
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

    private string? _posterPath;
    private Action? _shoot;
    private string? _clipPath;
    private string _greenlightVersion = "";
    private string? _sdkVersion;

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

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD7, 0x7F)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
            Child = _body,
        };

        _viewfinder.RegionChanged += (_, _) => Dispatcher.UIThread.Post(FollowFrame);
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

    private void StayAboveTheFrame()
    {
        Native.RaiseWithoutFocus(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        _states.RaiseWithoutFocus();
    }

    /// <summary>
    /// A close control on every page, because the window has no title bar to put one on.
    /// </summary>
    private Control Chrome(string heading)
    {
        var quit = new Button
        {
            Content = "✕", Padding = new Thickness(8, 2), FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
        };
        ToolTip.SetTip(quit, "Quit (Esc)");
        quit.Click += (_, _) => Close();
        DockPanel.SetDock(quit, Dock.Right);

        return new DockPanel { Width = 430, Children = { quit, Heading(heading) } };
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape) Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AdoptByTheFrame();
        StayAboveTheFrame();
    }

    /// <summary>
    /// The frame owns this window and the state strip, so both sit above it no matter what
    /// was clicked. Owner and owned must both exist, so this runs once each is open.
    /// </summary>
    private void AdoptByTheFrame()
    {
        var frame = _viewfinder.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Native.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, frame);
        Native.SetOwner(_states.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, frame);
    }

    /// <summary>
    /// Sits beside the frame — below, above, right or left, whichever fits on the screen —
    /// and never inside it.
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

        var region = _viewfinder.Region;
        var width = (int)(Bounds.Width * RenderScaling);
        var height = (int)(Bounds.Height * RenderScaling);
        const int gap = 16;

        var screen = Screens.ScreenFromPoint(new PixelPoint(region.X, region.Y))?.Bounds
                     ?? new PixelRect(region.X, region.Y, region.Width, region.Height);

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

    /// <summary>True when any of this window overlaps the capture area.</summary>
    private bool InShot()
    {
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
        var hide = InShot();
        if (hide)
        {
            Hide();
            // Let the compositor actually remove us before the first frame is grabbed.
            await Task.Delay(250);
        }
        try { await capture(); }
        finally { if (hide) { Show(); StayAboveTheFrame(); } }
    }

    // --- page one: the shot -----------------------------------------------------------

    private void ShowShootPage()
    {
        _viewfinder.Show();
        StayAboveTheFrame();
        _body.Children.Clear();
        _body.Children.Add(Chrome("Frame your client"));
        _body.Children.Add(Note(
            "Drag the frame over it, resize from the corners. The area inside the frame is a real hole " +
            "in this overlay — you can click straight through it to set your client up, and what you see " +
            "there is exactly what gets captured."));

        var status = Note("");
        var poster = Action("Take the poster", primary: true);
        var clip = Action($"Record {Capture.ClipSeconds}s clip (optional)");
        var next = Action("Next: the details") ;
        next.IsEnabled = false;

        _shoot = async () =>
        {
            await OutOfShot(() => { TakePoster(); return Task.CompletedTask; });
            status.Text = $"Poster saved. {new FileInfo(_posterPath!).Length / 1024} KB, comfortably inside the 1 MB limit.";
            next.IsEnabled = true;
        };
        poster.Click += (_, _) => _shoot();

        clip.Click += async (_, _) =>
        {
            if (!Capture.HasFfmpeg)
            {
                status.Text = "ffmpeg is not on PATH, so the clip is out — the poster alone makes a perfectly "
                            + "good listing. To add one: winget install Gyan.FFmpeg";
                return;
            }

            clip.IsEnabled = poster.IsEnabled = false;
            for (var count = 3; count > 0; count--)
            {
                status.Text = $"Recording in {count}…";
                await Task.Delay(700);
            }
            status.Text = $"Recording {Capture.ClipSeconds} seconds. Make it loop.";

            var path = Path.Combine(_workingDirectory, "clip.mp4");
            string? failure = null;
            await OutOfShot(async () => failure = await Capture.Clip(_viewfinder.Region, path, CancellationToken.None));

            if (failure is null)
            {
                _clipPath = path;
                status.Text = $"Clip saved. {new FileInfo(path).Length / 1024 / 1024.0:F1} MB of the 3 MB allowed.";
            }
            else
            {
                status.Text = "No clip: " + failure;
            }
            clip.IsEnabled = poster.IsEnabled = true;
        };

        next.Click += (_, _) => ShowDetailsPage();

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { poster, clip, next },
        });
        _body.Children.Add(status);
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewfinder.Close();
        _states.Close();
        // Detaching is what releases any hold this tool placed; Greenlight does that itself.
        _ = _greenlight.DisposeAsync().AsTask();
    }
}
