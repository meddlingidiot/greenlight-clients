# greenlight-clients

The manifest behind the [Greenlight client gallery](https://meddlingidiot.com/greenlight/gallery).
One directory per listing, assets committed alongside it, and no database — the site reads
this repository on a schedule, so a merged pull request becomes a card within half an hour.

```
listings/
  cars/
    listing.json      the entry
    poster.webp       the card, at most 1 MB
    clip.mp4          optional, silent, under 15s and 3 MB
schema/
  listing.schema.json the contract, and what CI checks against
example/
  listing.json        a complete annotated entry to copy
tools/
  validate.py         the mechanical review, runnable locally
```

## Adding your client

Read [CONTRIBUTING.md](CONTRIBUTING.md). The short version: copy `example/listing.json`
into `listings/your-slug/`, add a poster, run `python tools/validate.py`, open a pull
request.

**Listing a client is not an endorsement of it.** The review here covers the listing — that
it is well-formed, that the repository exists, that the licence is stated, that the images
are the contributor's to publish. Nobody reads the client's code for safety or quality, and
this repository will never claim otherwise. Download from here the way you would download
any other stranger's executable.

## Running the checks

```bash
pip install jsonschema
python tools/validate.py
```

`--offline` skips the repository reachability check, which is the only one that needs a
network. `ffprobe` is what measures clip duration; without it that single check is skipped
with a warning, and CI passes `--require-ffprobe` so it cannot be skipped there.

The checks have their own tests:

```bash
python tools/validate.py --self-test
```

Ten fixtures under `tools/tests/` — one that must pass, nine that must each fail for a
named reason. Rebuild them with `python tools/tests/make_fixtures.py` after changing the
schema. The point of them is that a validator nobody has watched fail might be passing
everything.

## Licence

The schema, the tooling and the documentation here are MIT — see [LICENSE](LICENSE).

**Listing content is not.** Each listing's text and assets belong to whoever contributed
them, licensed to Meddling Idiot Software only for display in the gallery and this
repository. Contributors attest in `assets.attestation` that the images are original or
that they hold a licence to publish them. If something here is yours and should not be,
open an issue and it comes down.
