using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
    private string? _clipPath;
    private string _greenlightVersion = "";
    private string? _sdkVersion;
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
        _viewfinder.Accepted += (_, _) => Dispatcher.UIThread.Post(TakePoster);

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

    /// <summary>Sits under the frame, or over it when there is no room below.</summary>
    private void FollowFrame()
    {
        var region = _viewfinder.Region;
        var height = (int)(Bounds.Height * RenderScaling);
        var below = region.Bottom + 16;
        var screen = Screens.ScreenFromPoint(new PixelPoint(region.X, region.Y));
        var limit = screen?.Bounds.Y + screen?.Bounds.Height ?? below + height;

        Position = new PixelPoint(region.X, below + height > limit ? Math.Max(0, region.Y - height - 16) : below);
    }

    // --- page one: the shot -----------------------------------------------------------

    private void ShowShootPage()
    {
        _body.Children.Clear();
        _body.Children.Add(Heading("Frame your client"));
        _body.Children.Add(Note(
            "Drag the frame over it, resize from the corners. The area inside the frame is a real hole " +
            "in this overlay — you can click straight through it to set your client up, and what you see " +
            "there is exactly what gets captured."));

        var status = Note("");
        var poster = Action("Take the poster", primary: true);
        var clip = Action($"Record {Capture.ClipSeconds}s clip (optional)");
        var next = Action("Next: the details") ;
        next.IsEnabled = false;

        poster.Click += (_, _) =>
        {
            TakePoster();
            status.Text = $"Poster saved. {new FileInfo(_posterPath!).Length / 1024} KB, comfortably inside the 1 MB limit.";
            next.IsEnabled = true;
        };

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
            var failure = await Capture.Clip(_viewfinder.Region, path, CancellationToken.None);

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
        _body.Children.Clear();
        _body.Children.Add(Heading("Five things, then you are done"));

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
                    Sdk = (string?)integration.SelectedItem == "sdk" ? _sdkVersion ?? "1.1.0" : null,
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
        _body.Children.Add(Heading("That is a listing"));
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
            await using var greenlight = new Greenlight.Sdk.GreenlightClient();
            await greenlight.StartAsync();

            // Absent is a normal state, so this waits briefly and then gives up quietly
            // rather than treating "no Greenlight" as a failure.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (greenlight.HostVersion is { Length: > 0 } version)
                {
                    _greenlightVersion = version;
                    break;
                }
                await Task.Delay(150);
            }

            _sdkVersion = typeof(Greenlight.Sdk.GreenlightClient).Assembly.GetName().Version is { } v
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
    }
}
