# greenlight-clients

The manifest behind the [Greenlight client gallery](https://meddlingidiot.com/greenlight/gallery):
a directory of things people built on top of a build indicator, mostly instead of doing their
actual work.

No database. No admin panel. No CMS with a login you'd forget by Thursday. One directory per
listing, assets committed next to the manifest, and a site that re-reads this repository on a
timer — so a merged pull request becomes a card within half an hour and nobody has to deploy
anything.

```
listings/
  cars/
    listing.json      the entry
    poster.webp       the card, 1 MB tops
    clip.mp4          optional, silent, under 15s and 3 MB
schema/
  listing.schema.json the contract, and what CI judges you against
example/
  listing.json        a complete annotated entry, for copying
tools/
  validate.py         the same checks CI runs, runnable before you embarrass yourself
capture/
  dotnet run          frames your client, shoots it, and writes the listing for you
```

## Adding your client

[CONTRIBUTING.md](CONTRIBUTING.md) has the whole story. The short version: copy
`example/listing.json` into `listings/your-slug/`, add a poster, run `python tools/validate.py`,
open a pull request.

**Being listed here is not an endorsement.** The review covers the *listing* — that it's
well-formed, the repository exists, the licence is stated, and the images belong to whoever
submitted them. Nobody reads the client's code for safety or quality, and this repository is
never going to pretend otherwise. Treat anything you download from here exactly as you'd
treat any other stranger's executable, which is to say: with a healthy scepticism you have
almost certainly abandoned by the third click.

## Running the checks

```bash
pip install jsonschema
python tools/validate.py
```

`--offline` skips the repository reachability check, which is the only one that wants a
network. Clip duration is measured by `ffprobe`; if you don't have it, that one check is
skipped with a warning, and CI passes `--require-ffprobe` so nobody can quietly skip it
where it counts.

## The checks have checks

```bash
python tools/validate.py --self-test
```

Ten fixtures: one that must pass, nine that must each fail for a specific named reason. This
sounds like overkill for a JSON validator, and then you remember that a validator nobody has
ever watched fail is indistinguishable from a validator that returns "looks fine" to
everything.

Each bad fixture is built to fail for *exactly* one reason, which is fussier than it sounds
and the entire point: if a fixture also trips some unrelated rule, the real check can rot for
months while the test suite cheerfully stays green.

Rebuild them with `python tools/tests/make_fixtures.py` after changing the schema. The PNGs
are generated rather than hand-committed, so the 1 MB poster limit is tested against actual
bytes instead of a hopeful mock.

## Licence

The schema, the tooling and the documentation are MIT — see [LICENSE](LICENSE). Take them.

**Listing content is not.** Each listing's text and assets belong to whoever contributed
them, licensed to Meddling Idiot Software purely for display in the gallery and in this
repository. Contributors attest in `assets.attestation` that their images are original or
that they hold a licence to publish them.

If something here is yours and shouldn't be, open an issue and it comes down — no argument,
no lawyers, no protracted thread about fair use.
