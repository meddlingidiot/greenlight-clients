using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Win32;

namespace Greenlight.GalleryCapture;

/// <summary>
/// The shutter and the record button, on keys that work from anywhere.
/// </summary>
/// <remarks>
/// <para>
/// System-wide rather than key bindings on our own windows, because the moment worth
/// photographing is nearly always a moment when something else has the keyboard: a build
/// being broken in an IDE, a client being poked at, a menu being held open. Pressing a
/// button means clicking this window first, and clicking this window is a click the client
/// underneath does not get — which for a client that reacts to clicks is the difference
/// between photographing it working and photographing it idle.
/// </para>
/// <para>
/// It also puts a stop within reach during a capture the panel had to hide for. Before this
/// there was genuinely nothing to press then, and a take that started could only end by
/// running out its cap.
/// </para>
/// <para>
/// Ctrl+Alt+F9 and F10 rather than anything more comfortable: a system-wide hotkey takes the
/// combination away from every other app on the machine for as long as this tool is open,
/// which rules out the ones worth having. Ctrl+Alt+S is JetBrains' Settings, F9 and F10 on
/// their own are a debugger's step keys, and Win+Shift+S is the Snipping Tool. Three
/// modifiers deep with a function key is a combination almost nothing else claims — and the
/// registration says so honestly when something does.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class Hotkeys : IDisposable
{
    private const int SnapshotId = 0xC1;
    private const int RecordId = 0xC2;

    public const string SnapshotChord = "Ctrl+Alt+F9";
    public const string RecordChord = "Ctrl+Alt+F10";

    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;

    private readonly IntPtr _hwnd;
    private readonly Action _snapshot;
    private readonly Action _record;
    private readonly Window _window;

    /// <summary>Held so the same delegate instance can be handed back to be removed.</summary>
    private readonly Win32Properties.CustomWndProcHookCallback? _hook;

    private bool _disposed;

    /// <summary>
    /// Takes over the two keys, for as long as this object lives.
    /// </summary>
    /// <param name="window">The window the presses are delivered to. Hidden is fine —
    /// WM_HOTKEY is posted to the queue whether the window is on screen or not, which is
    /// what makes a stop possible mid-capture.</param>
    public Hotkeys(Window window, Action snapshot, Action record)
    {
        _window = window;
        _snapshot = snapshot;
        _record = record;
        _hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero) return;

        const uint modifiers = Native.ModControl | Native.ModAlt | Native.ModNoRepeat;
        SnapshotRegistered = Native.RegisterHotKey(_hwnd, SnapshotId, modifiers, VkF9);
        RecordRegistered = Native.RegisterHotKey(_hwnd, RecordId, modifiers, VkF10);

        if (!SnapshotRegistered && !RecordRegistered) return;

        _hook = WndProc;
        Win32Properties.AddWndProcHookCallback(window, _hook);
    }

    /// <summary>Whether the machine let us have each key. Something else may already hold it.</summary>
    public bool SnapshotRegistered { get; }

    public bool RecordRegistered { get; }

    /// <summary>What to tell the person, or null when both keys are theirs to press.</summary>
    public string? Trouble => (SnapshotRegistered, RecordRegistered) switch
    {
        (true, true) => null,
        (false, false) => $"{SnapshotChord} and {RecordChord} are spoken for by another app, so the buttons are the way in.",
        (false, true) => $"{SnapshotChord} is spoken for by another app; {RecordChord} works.",
        (true, false) => $"{RecordChord} is spoken for by another app; {SnapshotChord} works.",
    };

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != Native.WmHotkey) return IntPtr.Zero;

        switch ((int)wParam)
        {
            case SnapshotId: _snapshot(); break;
            case RecordId: _record(); break;
            default: return IntPtr.Zero;
        }

        // Ours, and answered. Saying so keeps it from being passed on as an unhandled message.
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Gives the keys back.
    /// </summary>
    /// <remarks>
    /// Windows releases a window's hotkeys when the window is destroyed, so this is mostly
    /// tidiness — but the hook has to come off before the window goes, and doing both in one
    /// place is how they stay in step.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook is not null) Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        if (_hwnd == IntPtr.Zero) return;
        if (SnapshotRegistered) Native.UnregisterHotKey(_hwnd, SnapshotId);
        if (RecordRegistered) Native.UnregisterHotKey(_hwnd, RecordId);
    }
}
