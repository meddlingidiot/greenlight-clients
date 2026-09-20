using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Greenlight.GalleryCapture;

/// <summary>
/// Picks a window up by its own background.
/// </summary>
/// <remarks>
/// <para>
/// Neither the panel nor the state strip has a title bar. That is deliberate — they are
/// furniture pointed at somebody else's window, and a title bar is twenty-eight pixels of
/// chrome to keep out of the shot — but a window with no title bar is also a window Windows
/// will not let you move, and both of these land somewhere unhelpful sooner or later. The
/// panel is placed beside the frame, which on a full-screen client means on top of it; the
/// form is the tallest thing the tool draws and the corner it falls back to is the corner the
/// strip is already in; the strip is pinned to the top-left of the primary screen whatever
/// else lives there.
/// </para>
/// <para>
/// So the chassis is the handle. Everything that is not a control — the padding, the heading,
/// the prose, the status line — drags the window it is part of, which is what a title bar
/// would have done, without the title bar.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DragToMove
{
    /// <summary>
    /// Makes every part of <paramref name="handle"/> that is not itself a control drag
    /// <paramref name="window"/> about, telling <paramref name="moved"/> when one has.
    /// </summary>
    public static void By(Control handle, Window window, Action? moved = null)
    {
        // Tunnelling: a control that is going to handle the press has not handled it yet on
        // the way down, so this sees the presses that never bubble back out — the one that
        // puts a caret in a text box, for instance. It only acts on the ones that landed on
        // nothing, so seeing them all costs the controls nothing.
        handle.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            if (!IsBackground(e.Source, handle)) return;

            // Win32 runs the move itself, in a loop of its own, and hands control back when
            // the button comes up — so this is where a drag is over, and comparing the two
            // positions is how a drag is told from a click that landed on the background.
            // The difference matters to the panel, which stops following the frame once it
            // has been moved and should not do that because somebody clicked it.
            var before = window.Position;
            window.BeginMoveDrag(e);
            if (window.Position != before) moved?.Invoke();
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Whether a press landed on the chassis rather than on something with a job.
    /// </summary>
    /// <remarks>
    /// Walking up from what was actually hit rather than looking at it alone: the thing under
    /// the pointer on a button is the TextBlock the button's template made for its label,
    /// which is as inert as any other TextBlock and must still not move the window. Anything
    /// with a template — button, text box, combo box, scroll bar — is something to leave
    /// alone; panels, borders and text are the chassis. Which is also why the handle is the
    /// shell inside the window rather than the window itself: a combo box's dropdown is
    /// hung in the window's overlay layer, outside the shell, so a click on a list item
    /// never reaches this at all.
    /// </remarks>
    private static bool IsBackground(object? source, Control handle)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, handle)) return true;
            if (visual is TemplatedControl) return false;
        }

        return false;
    }
}
