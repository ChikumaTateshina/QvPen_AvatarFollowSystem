#!/usr/bin/env python3
"""Build the release zip and the VPM repository listing.

This repository's root is the package itself (it is also what the Booth
unitypackage unpacks to), so the zip is the tracked files minus the things that
only matter to the repository.

Two modes, run from the repository root:

    # Release: write <name>-<version>.zip into --out.
    python .github/scripts/build_vpm_listing.py zip --out dist

    # Pages: write index.json (and the web page) into --out.
    python .github/scripts/build_vpm_listing.py listing --repo owner/name --out site \
        [--existing published-index.json]

The listing only names a version whose zip can actually be downloaded from the
GitHub release, and takes the manifest and the hash from that downloaded zip. So
the listing can never advertise a download that answers 404, and never describes
anything other than the bytes a user will receive. Versions already in the
published listing are kept, so older versions stay installable.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WEBSITE_DIR = REPO_ROOT / "Website~"

LISTING_NAME = "QvPen Avatar Follow System"
LISTING_AUTHOR = "ChikumaTateshina"
LISTING_DESCRIPTION = "QvPen extension for VRChat worlds: drawn lines follow avatars."

# Repository-only files that must not end up in a Unity project.
EXCLUDED_TOP_LEVEL = {".git", ".github", ".gitignore", ".gitattributes", "Website~", "dist", "site"}
EXCLUDED_SUFFIXES = (".unitypackage", ".unitypackage.meta", ".zip", ".zip.meta")

# The earliest timestamp a zip entry can carry, used only when the commit time is unknown.
ZIP_EPOCH = (1980, 1, 1, 0, 0, 0)


def read_manifest() -> dict:
    return json.loads((REPO_ROOT / "package.json").read_text(encoding="utf-8"))


def zip_timestamp() -> tuple[int, int, int, int, int, int]:
    """The time every entry is stamped with: that of the commit being built.

    It has to differ between releases. Unity decides whether a script changed by its
    modification time, and VCC restores the times stored in the zip, so an archive whose
    entries all carry one fixed date installs over the previous version without Unity
    recompiling anything. The commit time changes with every release and is still the
    same for the same commit, so the archive stays reproducible.
    """
    try:
        seconds = int(
            subprocess.run(
                ["git", "log", "-1", "--format=%ct"],
                cwd=REPO_ROOT,
                check=True,
                capture_output=True,
                text=True,
            ).stdout.strip()
        )
    except (OSError, ValueError, subprocess.CalledProcessError):
        return ZIP_EPOCH

    moment = datetime.fromtimestamp(seconds, tz=timezone.utc)
    stamp = (moment.year, moment.month, moment.day, moment.hour, moment.minute, moment.second)
    return max(stamp, ZIP_EPOCH)


def package_files() -> list[Path]:
    files = []

    for path in sorted(REPO_ROOT.rglob("*")):
        relative = path.relative_to(REPO_ROOT)

        if relative.parts[0] in EXCLUDED_TOP_LEVEL or path.is_dir():
            continue

        if relative.name.endswith(EXCLUDED_SUFFIXES):
            continue

        files.append(path)

    return files


def build_zip(args: argparse.Namespace) -> int:
    manifest = read_manifest()
    out_dir = REPO_ROOT / args.out
    out_dir.mkdir(parents=True, exist_ok=True)
    destination = out_dir / f"{manifest['name']}-{manifest['version']}.zip"
    timestamp = zip_timestamp()

    # Collected before the archive is opened, so the archive cannot list itself.
    files = package_files()

    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in files:
            info = zipfile.ZipInfo(path.relative_to(REPO_ROOT).as_posix(), timestamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, path.read_bytes())

    data = destination.read_bytes()
    print(f"{destination.name}  {len(data) / 1024:.0f} KiB  sha256:{hashlib.sha256(data).hexdigest()[:12]}")
    return 0


def fetch_release_zip(url: str) -> bytes | None:
    """Download a published release asset, or answer None when it is not published.

    A check that cannot be made at all is an error: quietly dropping a version would
    unpublish it for everyone subscribed.
    """
    try:
        with urllib.request.urlopen(url, timeout=120) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None

        print(f"error: {url} answered HTTP {error.code}", file=sys.stderr)
        raise SystemExit(1) from error
    except (urllib.error.URLError, OSError) as error:
        print(f"error: could not fetch {url} ({error})", file=sys.stderr)
        raise SystemExit(1) from error


def load_existing(source: str | None) -> dict:
    if not source:
        return {}

    try:
        return json.loads(Path(source).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        # A listing that cannot be read must not silently become an empty one: that
        # would unpublish every older version for everyone subscribed.
        print(f"error: could not read the existing listing from {source}: {error}", file=sys.stderr)
        raise SystemExit(1)


def build_listing(args: argparse.Namespace) -> int:
    repo = args.repo or os.environ.get("GITHUB_REPOSITORY", "")
    if not repo:
        print("error: --repo or GITHUB_REPOSITORY is required", file=sys.stderr)
        return 1

    local = read_manifest()
    name, version = local["name"], local["version"]

    out_dir = REPO_ROOT / args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    listing = load_existing(args.existing)
    listing["name"] = LISTING_NAME
    listing["id"] = name
    listing["author"] = LISTING_AUTHOR
    listing["description"] = LISTING_DESCRIPTION

    # GitHub Pages serves the owner part in lower case, and VCC stores this URL
    # verbatim, so it has to match what the web page tells people to add.
    owner, _, repo_name = repo.partition("/")
    listing["url"] = f"https://{owner.lower()}.github.io/{repo_name}/index.json"

    # The package is always named, even with no versions: the URL has to be a valid
    # listing from the moment the page is live.
    versions = listing.setdefault("packages", {}).setdefault(name, {}).setdefault("versions", {})

    url = f"https://github.com/{repo}/releases/download/v{version}/{name}-{version}.zip"
    data = fetch_release_zip(url)

    if data is None:
        print(f"skipped {name} {version}: its release zip is not published yet")
    else:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            manifest = json.loads(archive.read("package.json").decode("utf-8"))

        if manifest.get("version") != version:
            print(f"error: {url} contains version {manifest.get('version')}", file=sys.stderr)
            return 1

        manifest["url"] = url
        manifest["zipSHA256"] = hashlib.sha256(data).hexdigest()
        versions[version] = manifest
        print(f"listed {name} {version}")

    (out_dir / "index.json").write_text(
        json.dumps(listing, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    if WEBSITE_DIR.is_dir():
        shutil.copytree(WEBSITE_DIR, out_dir, dirs_exist_ok=True)

    print(f"{name}: {', '.join(sorted(versions)) if versions else 'no versions yet'}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)

    zip_parser = commands.add_parser("zip", help="build the release zip")
    zip_parser.add_argument("--out", default="dist", help="output folder")
    zip_parser.set_defaults(run=build_zip)

    listing_parser = commands.add_parser("listing", help="build index.json and the web page")
    listing_parser.add_argument("--repo", help="owner/name on GitHub")
    listing_parser.add_argument("--existing", help="previously published index.json")
    listing_parser.add_argument("--out", default="site", help="output folder")
    listing_parser.set_defaults(run=build_listing)

    args = parser.parse_args()
    return args.run(args)


if __name__ == "__main__":
    raise SystemExit(main())
