# Adding your client to the gallery

One pull request, one directory, and no build step. If the checks pass, your listing
appears on [meddlingidiot.com/greenlight/gallery](https://meddlingidiot.com/greenlight/gallery)
within half an hour of merge — the site re-reads this repository on a schedule, so there is
nothing to deploy and nothing to wait for beyond that.

## What gets reviewed, and what does not

**Reviewed: the listing.** Does it describe a real client, does it point at a repository
that exists, is the licence stated, are the images yours to publish, is the description
something a stranger can understand.

**Not reviewed: your code.** Nobody here reads it for correctness, quality, or safety.
Being listed is not an endorsement, a security review, or a promise that the thing works
on your machine — it means somebody wrote a client and filled in a form honestly. That
limit is deliberate and permanent. Treat anything you download from here the way you would
treat any other stranger's executable.

If you find a listing that is malicious or misrepresented, open an issue and it comes down.

## The IP rule, in one line

**Original assets only — describe by function, not by franchise.**

Your screenshots, your drawings, or artwork you have a licence to publish. If your client
draws somebody else's characters, ships their logo, or borrows their music, it cannot be
listed here — no matter how good it looks. Describing what your client *does* is always
fine; the rule is about the images and the name, not the idea.

`assets.attestation` is where you say which applies:

- `original` — you made these, or they are screenshots of your own app.
- `licensed` — you have the right to publish them, and `attestation-note` says where they
  came from and under what licence.

There is no third option. A listing that cannot honestly claim one of those two is a
listing that cannot go up.

## Adding a listing

1. Fork this repository.
2. Create `listings/your-slug/`. The slug is the gallery URL, lowercase and hyphenated, and
   it is permanent once merged — changing it later breaks links.
3. Write `listings/your-slug/listing.json`. Copy [`example/listing.json`](example/listing.json),
   which is a complete, annotated entry, and point `$schema` at the schema so your editor
   offers completion.
4. Put the assets in the same directory:
   - **`poster`** — required. A still, at most **1 MB**, `.webp`, `.png` or `.jpg`. This is
     the card, and what shows before the clip loads. Landscape, roughly 16:9.
   - **`clip`** — optional. A silent `.mp4`, at most **3 MB** and **under 15 seconds**.
     Cards autoplay it muted and looping, so it wants to be a loop, not a trailer.
5. Run the checks locally:

```bash
pip install jsonschema
python tools/validate.py
```

6. Open a pull request. CI runs the same checks.

## The two fields people skip, and why they are required

**`greenlight-api`** — which local-API protocol version your client targets (`1` today).
Without it, the day the protocol moves to 2 there is no way to tell which listings still
work without writing to every author individually. It cannot be added retroactively, so it
is required from the first listing.

**`last-verified`** — the date you last ran your client, and the Greenlight version you ran
it against. This is what stops the gallery quietly filling with entries that broke two
releases ago. Update it when you touch your listing; a weekly job flags listings whose
repositories have been archived.

Neither is a promise that your client still works. They are the record of when anybody last
checked, which is a much more useful thing to publish than silence.

## Keeping a listing honest

- Update `last-verified` whenever you re-check.
- Set `maintained: false` yourself if you stop maintaining it. The listing stays and the
  card says so — that is more useful to a reader than a dead link.
- To remove a listing entirely, delete its directory. No explanation required.

## What the checks actually enforce

Everything in [`schema/listing.schema.json`](schema/listing.schema.json), plus:

- the slug matches the directory name
- the poster and clip exist, and are within the size and duration limits
- no unreferenced files are left in the listing directory
- two listings do not claim the same repository
- the repository URL resolves
- `last-verified.sdk` is present when `integration` is `sdk`

They do not, and will not, check whether the client is safe to run.
