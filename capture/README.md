# The capture tool

The annoying part of listing a client isn't the pull request — it's producing a 16:9 poster
under 1 MB and a silent loop under 15 seconds and 3 MB. That's twenty minutes of fiddling to
show off two minutes of work, and it's the step where "I'll do it later" means never.

So this does it.

```bash
cd capture
dotnet run
```

A frame appears over your desktop. Everything outside it dims; **the inside is a real hole in
the window**, so you can click straight through and drive your client while you line the shot
up, and what you see through the frame is exactly what lands in the file.

- **drag** anywhere in the dim to move it, **corners** to resize, **wheel** to zoom
- **arrow keys** nudge by a pixel, with Shift by ten
- **Enter** takes the poster, **Esc** gives up

Then answer five questions — name, one line, a paragraph, your name, your repository — and it
writes `listings/<slug>/` with the manifest and the assets, ready to validate and commit.

## Why the hole is a hole

`SetWindowRgn` removes the frame's interior from the window entirely. A merely *transparent*
pixel is still part of the window and still swallows the click; a pixel outside the window
region doesn't exist, so the mouse lands on whatever is underneath.

That one call buys three things at once: you can interact with your client through the frame,
the dimming stops cleanly at the edge, and **neither window has to be hidden for the capture**
— there is nothing of ours inside the frame to photograph.

It's the same overlay technique
[CarsClient](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.CarsClient) uses to lay
cars over the taskbar. The difference is that the cars make the *whole* window click-through,
because nothing on them is interactive; here the dimmed surround has to stay draggable, so
only the interior is cut away.

## Making something happen

A clip of a client sitting green is a clip of nothing happening. The **Show a state** row —
Red, Yellow, Green, Off, Blink, Live — sends Greenlight's `hold_indicators` command through
the SDK, and every indicator Greenlight drives follows it: its tray icon, its desktop stoplight,
any hardware lamps, and every attached app, yours included. Go red before the still, start the
blink partway through the clip, come back to green for the last second.

Greenlight releases the hold the moment this tool's connection ends, so however you leave —
Done, Esc, the ✕, Task Manager — the user's real light comes back. The buttons grey out, and
say why, when the attached Greenlight doesn't offer the command: its local API set to
read-only, or a version before 1.0.20.

## What it fills in for you

`last-verified.greenlight` is meant to be the Greenlight version you actually ran against, so
the tool asks the Greenlight running on this machine rather than asking you to go and look.
It uses the SDK to do it — which also means that if the tool can fill that field in, your
machine has a working Greenlight for your client to talk to.

Greenlight not running is fine. The field falls back and the tool says so.

## The clip is optional

The poster needs nothing but Windows. The clip needs `ffmpeg` on PATH:

```
winget install Gyan.FFmpeg
```

Without it the clip button explains itself and stops — a poster alone is a perfectly good
listing, which is why the schema makes `clip` optional.

Bitrate is computed from the 3 MB limit rather than guessed, and capped with
`maxrate`/`bufsize` so a busy scene can't overshoot the way a plain CRF encode can. Ten
seconds, not the fifteen the schema allows: the card loops it forever, and a loop wants to be
short.

## Known limits

- **Windows only.** The viewfinder is a Win32 window region and the grab is `BitBlt`. The
  clients it photographs are Windows apps, so this costs nothing.
- Both paths are verified end to end. The first real run (12 Sep 2026, ffmpeg 9.0.1) produced
  a 10 s, 30 fps h.264 clip at 573 KB and a 106 KB poster, with a layered click-through
  client in the frame — which is the case CAPTUREBLT exists for.
- Poster comes out as `.jpg`. The schema also accepts `.webp` and `.png`; JPEG is what
  Windows can encode without another dependency, and quality steps down until the file fits
  rather than guessing once.
