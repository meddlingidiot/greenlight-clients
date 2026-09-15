# So you've built a thing

Congratulations. You looked at a perfectly good build indicator, thought "this needs more
cars", and now here you are. This is the right place.

One pull request, one directory, no build step. If the checks pass, your listing shows up on
[meddlingidiot.com/greenlight/gallery](https://meddlingidiot.com/greenlight/gallery) within
half an hour of merge, because the site re-reads this repository on a timer like some kind
of animal. There is nothing to deploy. There is nothing to wait for. Go and have a biscuit.

## What gets reviewed (spoiler: not your code)

**Reviewed: the listing.** Does it describe a real client. Does the repository exist. Is the
licence stated. Are the images yours. Can a stranger read the description and understand
what the thing does.

**Not reviewed: literally your entire codebase.** Nobody here is reading it for correctness,
quality, elegance, or whether it quietly mines cryptocurrency on the side. Being listed is
not an endorsement, not a security review, and not a promise that it works on anything other
than the machine you wrote it on. It means somebody built a client and filled in a form
honestly.

That limit is deliberate and permanent, and no, we're not going to "just have a quick look"
at yours. Download things from here with the same enthusiasm you'd apply to any other
stranger's executable — which is to say, some, but not unlimited.

Spot a listing that's malicious or lying? Open an issue. It comes down. No ceremony.

## The IP rule, which is one sentence long

**Original assets only — describe by function, not by franchise.**

Your screenshots, your drawings, or art you actually have a licence to publish. If your
client renders somebody else's beloved cartoon mouse, ships their logo, or plays eight bars
of their theme tune, it cannot go up here. It does not matter how good it looks. It looks
especially good in the cease-and-desist.

Describing what your client *does* is always fine. The rule is about the pixels and the
name, not the idea.

`assets.attestation` is where you say which one applies:

- `original` — you made these, or they're screenshots of your own app.
- `licensed` — you have the right to publish them, and `attestation-note` says where they
  came from and under what terms.

There is no third option. There is no `assets.attestation: "probably fine"`. A listing that
can't honestly claim one of those two is a listing that doesn't go up, and we will both be
much happier having established that now.

## Actually adding your listing

### The short way

```bash
cd capture
dotnet run
```

Frame your client — or take the whole screen — press Enter, answer five questions. The tool writes the whole directory —
manifest, poster, and a clip if you want one — already inside every limit, so steps 2 to 4
below stop existing. See [capture/README.md](capture/README.md).

### The long way, which is fine too

1. Fork this repository. You know how to do this.
2. Create `listings/your-slug/`. Lowercase, hyphenated, and **permanent once merged** —
   it's the gallery URL, so picking a slug you'll resent in six months is a choice you get
   to live with.
3. Write `listings/your-slug/listing.json`. Copy [`example/listing.json`](example/listing.json),
   which is a complete annotated entry, and point `$schema` at the schema so your editor
   does the remembering for you.
4. Put the assets in the same directory:
   - **`poster`** — required. A still, **1 MB tops**, `.webp`, `.png` or `.jpg`. Landscape,
     roughly 16:9. This is the card. It is doing more work than your description, sorry.
   - **`clip`** — optional. A silent `.mp4`, **3 MB tops**, **under 15 seconds**. Cards
     autoplay it muted on a loop, so make it a loop. Nobody wants your trailer. Nobody
     wants anybody's trailer.
5. Run the checks yourself, before CI does it in front of everyone:

```bash
pip install jsonschema
python tools/validate.py
```

6. Open the pull request.

## The two fields everyone tries to skip

Yes, they're required. Yes, from the very first listing. Here's why, so you can be annoyed
with an informed opinion rather than a vague one.

**`greenlight-api`** — which local-API protocol version your client targets (it's `1`; it
has always been `1`; enjoy this while it lasts). The day that becomes `2`, the only way to
know which listings still work *without* this field is to email every author individually
and wait for the 40% who reply. That is not a project anybody wants to run. Hence: required
now, when it costs you one line.

**`last-verified`** — the date you last actually ran the thing, and which Greenlight version
you ran it against. This is the field standing between the gallery and becoming a museum of
software that stopped working in 2027. Update it when you touch your listing.

Neither field promises your client still works. They record when somebody last checked,
which is dramatically more useful than the alternative, which is silence.

## Keeping it honest

- Update `last-verified` whenever you re-check. It takes four seconds.
- Stopped maintaining it? Set `maintained: false` yourself. The listing stays, the card says
  so, and that's genuinely more useful to a reader than clicking through to a dead link and
  wondering. A weekly job also flags listings whose repositories have been archived, so this
  will eventually happen with or without you. Beat it to the punch, look responsible.
- Want it gone entirely? Delete the directory. No explanation, no exit interview.

## What the checks actually enforce

Everything in [`schema/listing.schema.json`](schema/listing.schema.json), plus:

- the slug matches the directory name
- the poster and clip exist and are within the size and duration limits
- no mystery files loitering in your listing directory
- two listings don't claim the same repository
- the repository URL actually resolves
- `last-verified.sdk` is there when `integration` is `sdk`

They do not check whether your client is safe to run. They never will. We've been over this.
