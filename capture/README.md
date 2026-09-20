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
- the **panel** and the **state strip** drag too, by any part of them that is not a control

Or press **Whole screen** and skip the framing entirely.

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

## Everything moves

The frame could always be dragged. The other two windows could not, and they are the ones
that end up in the way. The panel is placed beside the frame, which for a client that fills
the screen means on top of it, and the page with the five questions on it is the tallest
thing the tool draws. The strip is pinned to the top-left corner of the primary screen,
whatever else already lives there.

Both drag now, by any part of them that is not a button or a field: the heading row — which
shows the cursor a title bar would, being the nearest thing the panel has to one — the
padding, the prose, the status line, and the grip at the left of the strip. Neither has a
title bar, because a title bar is chrome to keep out of a photograph, and the price of not
having one is that the window is also unmovable unless it says otherwise. This is it saying
otherwise.

Once the panel has been put somewhere by hand it stays there: a window that springs back
beside the frame the next time the frame is nudged is a window that cannot be moved at all.
It hands the placement back when the capture is re-aimed — **Whole screen**, or the next
monitor along — because where the bar goes is part of what those two do; it belongs along the
bottom of the screen it is pointed at. And a window dropped near the bottom of the screen is
pulled back on when the page it is showing grows taller, which the five questions do.

## The whole screen, and a shutter

Some clients are not a rectangle you can put a frame around. A desktop stoplight in one corner,
a taskbar strip along the bottom, cars driving between the two — frame any one of them and the
listing shows a third of the thing.

**Whole screen** drops the frame and points the capture at an entire monitor. What is left is a
small bar along the bottom of that screen: **Snapshot**, **Record**, the monitor it is pointed
at when there is more than one, and the way back to a frame.

The bar sits inside the shot and is not in it. `SetWindowDisplayAffinity` with
`WDA_EXCLUDEFROMCAPTURE` takes a window out of what `BitBlt` and the desktop duplication API
see — ffmpeg's `gdigrab` included — while leaving it perfectly visible to the person clicking
it. The state buttons get the same treatment, so Red, Blink and Green stay pressable *during* a
whole-screen recording without appearing in it.

Windows 10 2004 is where that call arrived. Older builds refuse it, and the tool falls back to
what it did before: the panel hides itself for the length of the capture and says so, which
also means there is no Stop button to press, and the clip ends on its cap instead.

The poster comes out at the monitor's own size and aspect — 3440×1440 stays 3440×1440. The
schema has never asked for 16:9; the *frame* keeps 16:9 because a card looks better that way,
and a monitor is whatever the monitor is.

## Making something happen

A clip of a client sitting green is a clip of nothing happening. The state strip in the corner —
**Live, Grey, Green, Amber, Red, Blinking, Auto** — sends Greenlight's `hold_indicators` command
through the SDK, and every indicator Greenlight drives follows it: its tray icon, its desktop
stoplight, any hardware lamps, and every attached app, yours included. Go red before the still,
start the blink partway through the clip, come back to green for the last second.

The pads are the [TestStrip](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.TestStrip)'s
colours in the TestStrip's order, and the lit one is the one being held. The two tools get
reached for in the same half hour and often sit on the same screen; a Red that is the same red
in both is one less thing to translate. Before that there was nothing on the strip saying what
the machine was being told — six identical chips and a line of prose underneath.

**Auto** does the whole performance on its own: green and blinking for three seconds, amber for
three, red for four, and round again. That is ten seconds, which is exactly one clip at the
default cap — so a recording started anywhere in the cycle catches all three states. It loops
rather than running once for the same reason. Pressing anything else takes manual control back,
and the blink goes out with it: Auto owns the blink for as long as it runs. While it runs, the
Auto pad wears the colour of the phase it is in, so the cycle can be read off the strip without
watching the thing it is driving.

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

**Record** starts it and the same button stops it, counting up while it runs. Ten seconds is
the cap rather than the length — most of what a build indicator does, it does in about three —
and stopping is a `q` on ffmpeg's stdin, which closes the file properly. Killing the process
would be faster and would leave an mp4 with no moov atom, which is to say no mp4.

### Longer than ten seconds

**Hold Record down for three seconds** and a length button appears next to it: 10s, 15s, 30s,
60s, 120s. Holding it again puts the button away and the cap back to ten.

It is hidden because a listing's clip stops at fifteen seconds and wants to be shorter than
that. A length control sitting there by default reads as an invitation to record forty seconds
of a build light, which is the wrong thing to put on a card — and the people who want a longer
take want it for something else and can be expected to go and find it.

The bitrate does not change with the length. Spreading the same 3 MB across two minutes would
not give you a longer clip, it would give you a worse-looking one, so a long take is recorded
at the rate a ten-second one gets and comes out however big it comes out.

What decides whether a recording becomes the listing's clip is the finished file, not the cap
it was started under: under fifteen seconds and under 3 MB and it is attached, and a two-minute
cap stopped after eight seconds is an ordinary clip. Anything else is kept, and the tool says
where it is and why it is not going in the listing — better said there than discovered from CI.

## Known limits

- **Windows only.** The viewfinder is a Win32 window region and the grab is `BitBlt`. The
  clients it photographs are Windows apps, so this costs nothing.
- Both paths are verified end to end. The first real run (12 Sep 2026, ffmpeg 9.0.1) produced
  a 10 s, 30 fps h.264 clip at 573 KB and a 106 KB poster, with a layered click-through
  client in the frame — which is the case CAPTUREBLT exists for.
- The whole-screen path was checked the same way (14 Sep 2026): a 1920×1080 poster taken with
  the shutter bar and the state buttons on screen, and neither of them anywhere in the file.
- Poster comes out as `.jpg`. The schema also accepts `.webp` and `.png`; JPEG is what
  Windows can encode without another dependency, and quality steps down until the file fits
  rather than guessing once.
