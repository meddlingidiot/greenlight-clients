"""Rebuilds the validator's test fixtures.

Run after changing the schema, then re-run `python tools/validate.py --self-test`.

The PNGs are generated rather than hand-committed so the 1 MB poster rule is exercised
against real bytes instead of a mocked stat(). Each bad fixture is built to fail for
exactly one reason — a self-test whose cases fail for incidental reasons can keep passing
long after the check it names has stopped working.
"""
import json, os, struct, zlib
from pathlib import Path

TESTS = Path(__file__).resolve().parent


def png(width: int, height: int, filler: int = 0) -> bytes:
    """A valid greyscale PNG. `filler` bytes of incompressible noise pad the file so a
    fixture can exceed the 1 MB poster limit for real."""
    raw = b"".join(b"\x00" + bytes([(x * 7 + y * 13) % 256 for x in range(width)]) for y in range(height))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(raw, 9))
    if filler:
        out += chunk(b"teXt", b"pad\x00" + os.urandom(filler))  # random: will not compress away
    out += chunk(b"IEND", b"")
    return out


def base_listing(**overrides) -> dict:
    listing = {
        "slug": "example-client",
        "name": "Example Client",
        "tagline": "A listing that exists so the checks can be checked",
        "description": "Fixture data for the validator's self-test. Not a real client, and never published.",
        "author": {"name": "Meddling Idiot Software"},
        "repository": "https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.CarsClient",
        "license": "MIT",
        "platforms": ["windows"],
        "integration": "sdk",
        "greenlight-api": 1,
        "assets": {"poster": "poster.png", "attestation": "original"},
        "last-verified": {"date": "2026-09-12", "greenlight": "1.4", "sdk": "1.1.0"},
    }
    listing.update(overrides)
    return listing


def write(directory: Path, listing: dict, poster: bytes | None = png(32, 32), extra: dict | None = None,
          keep_slug: bool = False):
    # Each fixture must fail for exactly one reason, or the self-test could keep passing
    # while the check it actually names has quietly stopped working. So the slug tracks the
    # directory name everywhere except the case that is deliberately about the slug.
    if not keep_slug and "slug" in listing:
        listing["slug"] = directory.name
    directory.mkdir(parents=True, exist_ok=True)
    for old in directory.iterdir():
        old.unlink()
    (directory / "listing.json").write_text(json.dumps(listing, indent=2) + "\n", encoding="utf-8")
    if poster is not None:
        (directory / "poster.png").write_bytes(poster)
    for name, content in (extra or {}).items():
        (directory / name).write_bytes(content)


# --- the one that must pass ---------------------------------------------------------
write(TESTS / "good" / "example-client", base_listing())

# --- the ones that must fail, and why ------------------------------------------------
cases = {}

write(TESTS / "bad" / "slug-mismatch", base_listing(slug="something-else"), keep_slug=True)
cases["slug-mismatch"] = "does not match the directory name"

write(TESTS / "bad" / "unknown-field", base_listing(downloads="https://example.com"))
cases["unknown-field"] = "Additional properties are not allowed"

listing = base_listing()
listing["assets"] = {"poster": "poster.png", "attestation": "licensed"}
write(TESTS / "bad" / "licensed-without-note", listing)
cases["licensed-without-note"] = "attestation-note"

write(TESTS / "bad" / "poster-missing", base_listing(), poster=None)
cases["poster-missing"] = "is not in this listing's directory"

write(TESTS / "bad" / "poster-too-big", base_listing(), poster=png(64, 64, filler=1_100_000))
cases["poster-too-big"] = "over the 1 MB limit"

listing = base_listing()
del listing["greenlight-api"]
write(TESTS / "bad" / "no-api-version", listing)
cases["no-api-version"] = "'greenlight-api' is a required property"

listing = base_listing()
listing["last-verified"] = {"date": "2026-09-12", "greenlight": "1.4"}
write(TESTS / "bad" / "sdk-without-version", listing)
cases["sdk-without-version"] = "last-verified/sdk: required when integration is 'sdk'"

write(TESTS / "bad" / "stray-file", base_listing(), extra={"notes.md": b"# stray\n"})
cases["stray-file"] = "not referenced by listing.json"

listing = base_listing()
listing["last-verified"] = {"date": "12 September 2026", "greenlight": "1.4", "sdk": "1.1.0"}
write(TESTS / "bad" / "bad-date", listing)
cases["bad-date"] = "is not a 'date'"

(TESTS / "expectations.json").write_text(json.dumps(cases, indent=2, sort_keys=True) + "\n", encoding="utf-8")
print("fixtures written: 1 good, %d bad" % len(cases))
for name in sorted(cases):
    print("  bad/%s" % name)
