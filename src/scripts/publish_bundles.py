#!/usr/bin/env python3
"""Publish the bundle ZIPs to a GitHub Release via the REST API.

`gh` is not installed, so this uses the GitHub REST API authenticated with the token from
the machine's git credential manager (the same credential `git push` uses). The token is
read from `git credential fill` and kept only in memory — it is NEVER printed or logged.

    python scripts/publish_bundles.py --list
    python scripts/publish_bundles.py --tag v0.9.14-bundles --dist C:\\Users\\Server\\no-dist \
        --assets nuclear-option-toolkit-pterodactyl.zip nuclear-option-toolkit-local.zip \
                 nuclear-option-toolkit-manual.zip
"""
import argparse
import json
import os
import subprocess
import sys
import urllib.request
import urllib.error

REPO = os.environ.get("PUBLISH_REPO", "Tomosnuclearbombos/nuclear-option-toolkit")
API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"


def _token():
    """Read the GitHub token from git credential manager. Never logged."""
    tok = os.environ.get("GITHUB_TOKEN")
    if tok:
        return tok
    p = subprocess.run(["git", "credential", "fill"],
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True, timeout=30)
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):].strip()
    raise SystemExit("could not obtain a GitHub token from git credential manager")


def _req(token, method, url, body=None, ctype="application/json", raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
        "Content-Type": ctype, "User-Agent": "nuke-toolkit-publish",
        "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            b = r.read()
        return r.status, (json.loads(b) if b and ctype == "application/json" and method != "PUT" else (json.loads(b) if b else {}))
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def list_releases(token):
    st, rels = _req(token, "GET", "%s/repos/%s/releases" % (API, REPO))
    if st != 200:
        raise SystemExit("list failed: HTTP %s %s" % (st, rels))
    for r in rels:
        assets = ", ".join(a["name"] for a in r.get("assets", []))
        print("- %-22s id=%s latest=%s prerelease=%s\n    assets: %s"
              % (r.get("tag_name"), r.get("id"), not r.get("draft") and not r.get("prerelease"),
                 r.get("prerelease"), assets or "(none)"))


def get_or_create(token, tag, name, notes, prerelease):
    st, rel = _req(token, "GET", "%s/repos/%s/releases/tags/%s" % (API, REPO, tag))
    if st == 200:
        print("[release] reusing existing %s (id=%s)" % (tag, rel["id"]))
        return rel
    st, rel = _req(token, "POST", "%s/repos/%s/releases" % (API, REPO),
                   {"tag_name": tag, "name": name, "body": notes,
                    "draft": False, "prerelease": prerelease})
    if st not in (200, 201):
        raise SystemExit("create release failed: HTTP %s %s" % (st, rel))
    print("[release] created %s (id=%s)" % (tag, rel["id"]))
    return rel


def upload_asset(token, rel, path):
    name = os.path.basename(path)
    for a in rel.get("assets", []):
        if a["name"] == name:
            _req(token, "DELETE", "%s/repos/%s/releases/assets/%s" % (API, REPO, a["id"]))
            print("  (replaced existing %s)" % name)
    with open(path, "rb") as f:
        raw = f.read()
    url = "%s/repos/%s/releases/%s/assets?name=%s" % (UPLOADS, REPO, rel["id"], name)
    ctype = "application/zip" if name.endswith(".zip") else "text/plain"
    st, resp = _req(token, "POST", url, raw=raw, ctype=ctype)
    if st not in (200, 201):
        raise SystemExit("upload %s failed: HTTP %s %s" % (name, st, resp))
    print("  uploaded %s (%.1f MB)" % (name, len(raw) / 1e6))


def list_all_releases(token, per_page=100):
    st, rels = _req(token, "GET", "%s/repos/%s/releases?per_page=%d" % (API, REPO, per_page))
    if st != 200:
        raise SystemExit("list failed: HTTP %s %s" % (st, rels))
    return rels


def delete_release(token, rel):
    """Delete a release AND its now-dangling git tag. Tolerant of already-gone."""
    tag = rel.get("tag_name")
    st, _ = _req(token, "DELETE", "%s/repos/%s/releases/%s" % (API, REPO, rel["id"]))
    if st not in (200, 204, 404):
        print("    (release delete HTTP %s for %s)" % (st, tag))
    if tag:
        st2, _ = _req(token, "DELETE", "%s/repos/%s/git/refs/tags/%s" % (API, REPO, tag))
        if st2 not in (200, 204, 404, 422):
            print("    (tag delete HTTP %s for %s)" % (st2, tag))
    return st in (200, 204)


def prune_nightlies(token, keep=3, dry_run=False):
    """Keep the `keep` most-recent nightly PRE-releases; delete older ones (and their git tags).
    Stable (non-prerelease) releases are NEVER touched. Returns the list of removed tags."""
    nightly = [r for r in list_all_releases(token)
               if r.get("prerelease") and "-nightly." in (r.get("tag_name") or "")]
    nightly.sort(key=lambda r: r.get("created_at") or "", reverse=True)
    doomed = nightly[keep:]
    for r in doomed:
        if dry_run:
            print("  [prune] WOULD delete %s (created %s)" % (r.get("tag_name"), r.get("created_at")))
        else:
            print("  [prune] deleting %s" % r.get("tag_name"))
            delete_release(token, r)
    return [r.get("tag_name") for r in doomed]


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--prune-nightlies", type=int, metavar="KEEP", default=None,
                    help="delete all but the KEEP most-recent nightly pre-releases, then exit")
    ap.add_argument("--dry-run", action="store_true",
                    help="with --prune-nightlies: show what would be deleted, delete nothing")
    ap.add_argument("--tag")
    ap.add_argument("--name", default="")
    ap.add_argument("--notes", default="")
    ap.add_argument("--notes-file", default="")
    ap.add_argument("--dist", default="")
    ap.add_argument("--assets", nargs="*", default=[])
    ap.add_argument("--prerelease", action="store_true")
    a = ap.parse_args(argv)
    token = _token()
    if a.list:
        list_releases(token)
        return 0
    if a.prune_nightlies is not None:
        pruned = prune_nightlies(token, keep=a.prune_nightlies, dry_run=a.dry_run)
        print("%s %d nightl%s%s" % ("would prune" if a.dry_run else "pruned",
              len(pruned), "y" if len(pruned) == 1 else "ies",
              (": " + ", ".join(pruned)) if pruned else " (nothing to do)"))
        return 0
    if not (a.tag and a.dist and a.assets):
        raise SystemExit("need --tag, --dist and --assets (or use --list)")
    notes = a.notes
    if a.notes_file:
        with open(a.notes_file, encoding="utf-8") as f:
            notes = f.read()
    rel = get_or_create(token, a.tag, a.name or a.tag, notes, a.prerelease)
    for name in a.assets:
        path = name if os.path.isabs(name) else os.path.join(a.dist, name)
        if not os.path.exists(path):
            raise SystemExit("asset not found: %s" % path)
        upload_asset(token, rel, path)
        sha = path + ".sha256"
        if os.path.exists(sha):
            upload_asset(token, rel, sha)
    print("DONE. https://github.com/%s/releases/tag/%s" % (REPO, a.tag))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
