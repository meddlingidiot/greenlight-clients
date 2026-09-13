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
/// Red / Yellow / Green / Off / Blink / Live, floating in the top-left corner of the screen
/// for the whole session — the developer page's buttons, sent through the SDK as
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
/// Pinned to the corner, not to the frame, so it is in the same place every time and never
/// in the shot unless the frame is dragged over the corner — in which case it is in the
/// shot, deliberately: hiding it would take the buttons away at the one moment they matter.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StateStrip : Window
{
    /// <summary>Physical height, so the panel knows where the corner stops being spoken for.</summary>
    public const int PixelHeight = 64;

    private readonly GreenlightClient _greenlight;
    private readonly TextBlock _why = new()
    {
        FontSize = 11, TextWrapping = TextWrapping.NoWrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xAA, 0xA0)),
    };

    private GreenlightStatus? _held;
    private bool _pretendBuilding;

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

        var buttons = new[]
        {
            Button("Red", GreenlightStatus.Red),
            Button("Yellow", GreenlightStatus.Yellow),
            Button("Green", GreenlightStatus.Green),
            Button("Off", GreenlightStatus.Unknown),
        };

        var blink = Chip("Blink");
        blink.Click += async (_, _) =>
        {
            _pretendBuilding = !_pretendBuilding;
            var result = await _greenlight.HoldIndicatorsAsync(_held, _pretendBuilding ? true : null);
            if (!result.IsOk) _pretendBuilding = !_pretendBuilding;
            blink.Content = _pretendBuilding ? "Stop blink" : "Blink";
            Report(result, _pretendBuilding ? "Blinking as though a build were running." : "Blink off.");
        };

        var live = Chip("Live");
        live.Click += async (_, _) =>
        {
            var result = await _greenlight.ReleaseIndicatorsAsync();
            if (result.IsOk) { _held = null; _pretendBuilding = false; blink.Content = "Blink"; }
            Report(result, "Back to the real build state.");
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var button in buttons) row.Children.Add(button);
        row.Children.Add(blink);
        row.Children.Add(live);

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD7, 0x7F)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Child = new StackPanel { Spacing = 4, Children = { row, _why } },
        };

        void Refresh()
        {
            var offered = _greenlight.Availability == GreenlightAvailability.Connected
                          && _greenlight.SupportedCommands.Contains(CommandName.HoldIndicators);
            foreach (var button in buttons) button.IsEnabled = offered;
            blink.IsEnabled = live.IsEnabled = offered;

            _why.Text = offered
                ? "Every indicator Greenlight drives follows these. It lets go when this tool closes."
                : _greenlight.Availability != GreenlightAvailability.Connected
                    ? "Greenlight is not running, so its state cannot be driven from here."
                    : _greenlight.SupportedCommands.Count == 0
                        ? "Greenlight's local API is read-only. Settings → Local API → allow commands."
                        : $"This Greenlight ({_greenlight.HostVersion}) predates hold_indicators; 1.0.20 or later has it.";
        }

        // Both events arrive on a background thread.
        _greenlight.AvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _greenlight.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private Button Button(string label, GreenlightStatus status)
    {
        var button = Chip(label);
        button.Click += async (_, _) =>
        {
            var result = await _greenlight.HoldIndicatorsAsync(status, _pretendBuilding ? true : null);
            if (result.IsOk) _held = status;
            Report(result, $"Holding every indicator at {label}.");
        };
        return button;
    }

    private static Button Chip(string text) => new()
    {
        Content = text, Padding = new Thickness(12, 5), FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xEA)),
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x2C, 0x27)),
    };

    private void Report(CommandResult result, string didWhat) =>
        _why.Text = result.IsOk ? didWhat : $"Greenlight refused: {result.Message ?? result.Error ?? result.Outcome.ToString()}";

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Native.MakeToolWindow(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        // The corner of the primary screen. Not the frame's screen: the frame moves, and the
        // point of the strip is to be in the same place every time.
        var primary = Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        Position = new PixelPoint(primary.X + 16, primary.Y + 16);
        RaiseWithoutFocus();
    }

    /// <summary>Back to the top of the topmost band, without taking focus from anything.</summary>
    public void RaiseWithoutFocus() =>
        Native.RaiseWithoutFocus(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
}
