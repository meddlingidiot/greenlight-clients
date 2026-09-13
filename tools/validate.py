#!/usr/bin/env python3
"""Mechanical review of the gallery's listings.

What this checks is deliberately narrow: that a listing is well-formed, that its assets
are the size and shape the cards expect, and that the repository it points at exists.

What it does not check, and must never pretend to: whether the client's code is safe,
correct, or any good. Reviewing the listing is not reviewing the software. CONTRIBUTING.md
says so to contributors; this docstring says so to whoever next edits the checks.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

try:
    import jsonschema
except ImportError:
    sys.exit("jsonschema is not installed. Try: pip install jsonschema")

POSTER_MAX_BYTES = 1 * 1024 * 1024
CLIP_MAX_BYTES = 3 * 1024 * 1024
CLIP_MAX_SECONDS = 15.0
HTTP_TIMEOUT = 20

# A listing directory holds its manifest and its assets, and nothing else. Anything
# unexpected is flagged rather than ignored: this repository is public and takes pull
# requests from strangers, so a file nobody meant to add should be visible.
ALLOWED_EXTRA = {".gitkeep"}


class Problem(Exception):
    pass


def load_schema(root: Path) -> dict:
    return json.loads((root / "schema" / "listing.schema.json").read_text(encoding="utf-8"))


def listing_dirs(root: Path) -> list[Path]:
    listings = root / "listings"
    if not listings.is_dir():
        return []
    return sorted(d for d in listings.iterdir() if d.is_dir() and not d.name.startswith("."))


def check_schema(data: dict, schema: dict) -> list[str]:
    validator = jsonschema.Draft202012Validator(
        schema, format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER
    )
    problems = []
    for error in sorted(validator.iter_errors(data), key=lambda e: list(e.path)):
        where = "/".join(str(p) for p in error.path) or "(root)"
        problems.append(f"{where}: {error.message}")
    return problems


def check_assets(directory: Path, data: dict) -> list[str]:
    problems = []
    assets = data.get("assets") or {}

    poster = assets.get("poster")
    if poster:
        path = directory / poster
        if not path.is_file():
            problems.append(f"assets/poster: {poster} is not in this listing's directory")
        elif path.stat().st_size > POSTER_MAX_BYTES:
            problems.append(
                f"assets/poster: {poster} is {path.stat().st_size / 1024:.0f} KB, over the 1 MB limit"
            )

    clip = assets.get("clip")
    if clip:
        path = directory / clip
        if not path.is_file():
            problems.append(f"assets/clip: {clip} is not in this listing's directory")
        else:
            size = path.stat().st_size
            if size > CLIP_MAX_BYTES:
                problems.append(
                    f"assets/clip: {clip} is {size / 1024 / 1024:.1f} MB, over the 3 MB limit"
                )
            problems.extend(check_clip_duration(path))

    named = {v for k, v in assets.items() if k in ("poster", "clip")}
    for entry in sorted(directory.iterdir()):
        if entry.name == "listing.json" or entry.name in named:
            continue
        if entry.suffix in ALLOWED_EXTRA:
            continue
        problems.append(
            f"{entry.name}: not referenced by listing.json. Remove it, or point the listing at it"
        )
    return problems


# Where ffprobe is. Resolved from PATH by default; --ffprobe overrides it, which is what CI
# does: the workflow proves ffprobe exists in a shell step and hands that same path over,
# rather than trusting that Python's view of PATH matches the shell's. It did not, once.
FFPROBE: str | None = None


def find_ffprobe() -> str | None:
    return FFPROBE or shutil.which("ffprobe")


def check_clip_duration(path: Path) -> list[str]:
    """Duration needs ffprobe. Missing ffprobe is reported by the caller, not faked here."""
    ffprobe = find_ffprobe()
    if ffprobe is None:
        raise Problem("ffprobe-missing")
    try:
        result = subprocess.run(
            [
                ffprobe, "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                str(path),
            ],
            capture_output=True,
            text=True,
        )
    except OSError:
        # A --ffprobe that points at nothing is the same situation as none on PATH.
        raise Problem("ffprobe-missing") from None
    if result.returncode != 0:
        return [f"assets/clip: {path.name} could not be read as a video ({result.stderr.strip()})"]
    try:
        seconds = float(result.stdout.strip())
    except ValueError:
        return [f"assets/clip: {path.name} reports no duration; is it really an mp4?"]
    if seconds > CLIP_MAX_SECONDS:
        return [f"assets/clip: {path.name} runs {seconds:.1f}s, over the 15s limit"]
    return []


def check_repository(url: str) -> list[str]:
    request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "greenlight-clients-validate"})
    try:
        with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT) as response:
            if response.status >= 400:
                return [f"repository: {url} answered {response.status}"]
    except urllib.error.HTTPError as error:
        return [f"repository: {url} answered {error.code}"]
    except Exception as error:  # DNS, TLS, timeout — all mean "we could not reach it"
        return [f"repository: {url} could not be reached ({error})"]
    return []


def validate_listing(directory: Path, schema: dict, offline: bool, ffprobe_required: bool) -> list[str]:
    manifest = directory / "listing.json"
    if not manifest.is_file():
        return ["listing.json is missing"]

    try:
        data = json.loads(manifest.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        return [f"listing.json is not valid JSON: {error}"]

    problems = check_schema(data, schema)
    if problems:
        # Cross-field checks below assume the shape held; reporting both sets at once
        # would bury the real cause under consequences of it.
        return problems

    if data["slug"] != directory.name:
        problems.append(f"slug: '{data['slug']}' does not match the directory name '{directory.name}'")

    try:
        problems.extend(check_assets(directory, data))
    except Problem:
        message = (
            "assets/clip: ffprobe is not installed, so the 15s limit was not checked "
            f"(looked on PATH={os.environ.get('PATH', '')!r}; pass --ffprobe to say where it is)"
        )
        if ffprobe_required:
            problems.append(message.replace("was not checked", "could not be checked"))
        else:
            print(f"  ! {message}")

    if data.get("integration") == "sdk" and "sdk" not in data.get("last-verified", {}):
        problems.append("last-verified/sdk: required when integration is 'sdk'")

    if not offline:
        problems.extend(check_repository(data["repository"]))

    return problems


def run(root: Path, offline: bool, ffprobe_required: bool) -> int:
    schema = load_schema(root)
    jsonschema.Draft202012Validator.check_schema(schema)

    failures = 0

    # The example is what every contributor copies, so it is checked too — schema only,
    # since it deliberately ships without assets. An example that has quietly stopped
    # matching the schema teaches everyone the wrong shape at once.
    example = root / "example" / "listing.json"
    if example.is_file():
        problems = check_schema(json.loads(example.read_text(encoding="utf-8")), schema)
        if problems:
            failures += 1
            print("FAIL example/listing.json")
            for problem in problems:
                print(f"       {problem}")
        else:
            print("ok   example/listing.json (schema only)")

    directories = listing_dirs(root)
    if not directories:
        print("No listings yet.")
        return 1 if failures else 0

    seen_repos: dict[str, str] = {}

    for directory in directories:
        problems = validate_listing(directory, schema, offline, ffprobe_required)

        manifest = directory / "listing.json"
        if manifest.is_file() and not problems:
            data = json.loads(manifest.read_text(encoding="utf-8"))
            repo = data["repository"].rstrip("/").lower()
            if repo in seen_repos:
                problems.append(f"repository: already listed by '{seen_repos[repo]}'")
            else:
                seen_repos[repo] = directory.name

        if problems:
            failures += 1
            print(f"FAIL {directory.name}")
            for problem in problems:
                print(f"       {problem}")
        else:
            print(f"ok   {directory.name}")

    print()
    print(f"{len(directories) - failures}/{len(directories)} listings passed")
    return 1 if failures else 0


def self_test(root: Path) -> int:
    """Prove the checks reject what they are supposed to reject.

    A validator nobody has watched fail is a validator that might be passing everything.
    Every directory under tools/tests/bad/ must fail, for the reason named against it in
    tools/tests/expectations.json — the expectations live in one file outside the fixtures
    because a listing directory is checked for unreferenced files, and a stray expectation
    file would make each case fail for the wrong reason.
    """
    schema = load_schema(root)
    tests = root / "tools" / "tests"
    expectations = json.loads((tests / "expectations.json").read_text(encoding="utf-8"))
    failures = 0

    good = tests / "good"
    if good.is_dir():
        for directory in sorted(d for d in good.iterdir() if d.is_dir()):
            problems = validate_listing(directory, schema, offline=True, ffprobe_required=False)
            if problems:
                failures += 1
                print(f"FAIL self-test good/{directory.name} should have passed:")
                for problem in problems:
                    print(f"       {problem}")
            else:
                print(f"ok   self-test good/{directory.name}")

    bad = tests / "bad"
    if bad.is_dir():
        for directory in sorted(d for d in bad.iterdir() if d.is_dir()):
            if directory.name not in expectations:
                failures += 1
                print(f"FAIL self-test bad/{directory.name} has no entry in expectations.json")
                continue
            expect = expectations[directory.name]
            problems = validate_listing(directory, schema, offline=True, ffprobe_required=False)
            joined = " | ".join(problems)
            if not problems:
                failures += 1
                print(f"FAIL self-test bad/{directory.name} passed, but should have failed")
            elif expect not in joined:
                failures += 1
                print(f"FAIL self-test bad/{directory.name} failed for the wrong reason")
                print(f"       expected to contain: {expect}")
                print(f"       actually got:        {joined}")
            else:
                print(f"ok   self-test bad/{directory.name}")

    print()
    print("self-test passed" if not failures else f"self-test: {failures} case(s) wrong")
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("root", nargs="?", default=Path(__file__).resolve().parent.parent, type=Path)
    parser.add_argument("--offline", action="store_true", help="skip the repository reachability check")
    parser.add_argument("--require-ffprobe", action="store_true", help="fail rather than warn when ffprobe is absent")
    parser.add_argument("--ffprobe", metavar="PATH", help="the ffprobe to use, instead of searching PATH")
    parser.add_argument("--self-test", action="store_true", help="check the checks against tools/tests")
    args = parser.parse_args()

    if args.self_test:
        return self_test(args.root)
    global FFPROBE
    FFPROBE = args.ffprobe
    return run(args.root, args.offline, args.require_ffprobe)


if __name__ == "__main__":
    sys.exit(main())
