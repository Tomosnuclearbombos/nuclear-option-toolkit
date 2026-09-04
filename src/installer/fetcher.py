#!/usr/bin/env python3
"""Manifest-driven dependency fetcher for the Nuclear Option setup installer.

Reads installer/sources.json and, for a chosen hosting option, resolves the LATEST
correct version of each dependency from its real upstream and downloads + verifies +
places it — online or offline. Stdlib only (offline-friendly, freeze-friendly).

    python fetcher.py plan    <option>                 # show what would be fetched + latest versions
    python fetcher.py resolve <dependency>             # resolve one dep's latest URL/version
    python fetcher.py fetch   <option> --dest <dir> [--offline <folder>]

Options: own_pc_windows | own_pc_linux | external_linux_ptero | external_windows
Methods: github-release (tag_filter+asset_regex), github-raw, http-zip, steamcmd,
         thunderstore (fallback), bundled. See installer/sources.json.
"""
import argparse
import hashlib
import json
import os
import re
import ssl
import subprocess
import sys
import urllib.parse
import urllib.request
import urllib.error

for _s in (sys.stdout, sys.stderr):                   # never crash printing on a cp1252 console
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MANIFEST = os.path.join(HERE, "sources.json")
LOCKFILE = os.path.join(HERE, "sources.lock.json")
USER_DIR = os.environ.get("NOST_DATA_DIR") or os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")
_CTX = ssl.create_default_context()
_MAX_BYTES = 1024 * 1024 * 1024  # 1 GiB hard ceiling per download

# Only these hosts (or their subdomains) may be contacted or redirected to. This
# closes the SSRF / redirect-to-internal surface: a spoofed API response or a 302
# cannot steer the fetcher at an internal/metadata host, and only https is allowed.
_ALLOWED_HOSTS = (
    "github.com", "api.github.com", "codeload.github.com",
    "raw.githubusercontent.com", "objects.githubusercontent.com",
    "release-assets.githubusercontent.com",
    "thunderstore.io", "gcdn.thunderstore.io",
    "media.steampowered.com", "steamcdn-a.akamaihd.net",
    "cdn.akamai.steamstatic.com",
)


def _host_ok(netloc):
    host = netloc.split("@")[-1].split(":")[0].lower()
    return any(host == h or host.endswith("." + h) for h in _ALLOWED_HOSTS)


class _SafeRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse any redirect to a non-https or non-allowlisted host."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        u = urllib.parse.urlparse(newurl)
        if u.scheme != "https" or not _host_ok(u.netloc):
            raise urllib.error.URLError("blocked redirect to non-allowlisted URL: %s" % newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


_OPENER = urllib.request.build_opener(_SafeRedirect(),
                                      urllib.request.HTTPSHandler(context=_CTX))


def load_manifest():
    with open(MANIFEST, encoding="utf-8") as f:
        return json.load(f)


def _user_config():
    try:
        with open(os.path.join(USER_DIR, "config.json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _http(url, raw=True, headers=None):
    u = urllib.parse.urlparse(url)
    if u.scheme != "https" or not _host_ok(u.netloc):
        raise urllib.error.URLError("blocked non-allowlisted URL: %s" % url)
    h = {"User-Agent": "NukeOptionToolkit-Fetcher", "Accept": "application/json"}
    if headers:
        h.update(headers)
    if u.netloc == "api.github.com" and os.environ.get("GITHUB_TOKEN"):
        h["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    req = urllib.request.Request(url, headers=h)
    with _OPENER.open(req, timeout=30) as r:
        clen = r.headers.get("Content-Length")
        if clen and clen.isdigit() and int(clen) > _MAX_BYTES:
            raise urllib.error.URLError("download exceeds size cap (%d bytes)" % _MAX_BYTES)
        data = r.read(_MAX_BYTES + 1)
    if len(data) > _MAX_BYTES:
        raise urllib.error.URLError("download exceeds size cap (%d bytes, streamed)" % _MAX_BYTES)
    return data if raw else json.loads(data.decode("utf-8", "replace"))


# ---------------- resolve methods (lazy, network) ----------------
def _resolve_github_release(fetch):
    repo = fetch["repo"]
    rels = _http("https://api.github.com/repos/%s/releases" % repo, raw=False)
    tag_filter = re.compile(fetch["tag_filter"]) if fetch.get("tag_filter") else None
    asset_re = re.compile(fetch["asset_regex"])
    for rel in rels:                                       # newest-first
        if rel.get("draft") or rel.get("prerelease"):
            continue
        tag = rel.get("tag_name", "")
        if tag_filter and not tag_filter.search(tag):
            continue
        for a in rel.get("assets", []):
            if asset_re.search(a.get("name", "")):
                return {"url": a["browser_download_url"], "version": tag, "asset": a["name"]}
    raise LookupError("no release asset matching %s (tag_filter=%s) in %s"
                      % (fetch["asset_regex"], fetch.get("tag_filter"), repo))


def _resolve_github_raw(fetch):
    repo, branch, path = fetch["repo"], fetch.get("branch", "main"), fetch["path"]
    url = "https://raw.githubusercontent.com/%s/%s/%s" % (repo, branch, path)
    ver = "HEAD"
    try:                                                   # best-effort: latest commit touching the file
        commits = _http("https://api.github.com/repos/%s/commits?path=%s&per_page=1" % (repo, path), raw=False)
        if commits:
            ver = commits[0]["sha"][:10]
    except Exception:                                      # noqa: BLE001
        pass
    return {"url": url, "version": ver, "asset": os.path.basename(path)}


def _resolve_thunderstore(fetch):
    ns, pkg = fetch["namespace"], fetch["package"]
    d = _http("https://thunderstore.io/api/experimental/package/%s/%s/" % (ns, pkg), raw=False)
    latest = d["latest"]
    return {"url": latest["download_url"], "version": latest["version_number"], "asset": "%s-%s.zip" % (pkg, latest["version_number"])}


def _resolve_http_zip(fetch):
    return {"url": fetch["url"], "version": "evergreen", "asset": os.path.basename(fetch["url"].split("?")[0])}


def resolve(dep, cfg=None):
    """Resolve a dependency's latest source. Returns {method, url, version, asset, note}."""
    fetch = dict(dep["fetch"])
    method = fetch["method"]
    cfg = cfg or _user_config()
    # template the plugin repo from user config
    if "${UPDATE_REPO}" in json.dumps(fetch):
        repo = (cfg.get("update", {}) or {}).get("github_repo", "")
        if not repo:                                       # fall back to the manifest default
            try:
                repo = (load_manifest() or {}).get("default_repo", "")
            except Exception:                              # noqa: BLE001
                repo = ""
        if not repo:
            return {"method": method, "url": "", "version": "?", "asset": "",
                    "note": "plugin repo not configured (set update.github_repo) — skipped"}
        fetch["repo"] = repo
    try:
        if method == "github-release":
            r = _resolve_github_release(fetch)
        elif method == "github-raw":
            r = _resolve_github_raw(fetch)
        elif method == "thunderstore":
            r = _resolve_thunderstore(fetch)
        elif method == "http-zip":
            r = _resolve_http_zip(fetch)
        elif method == "steamcmd":
            return {"method": method, "url": "steam://app/%s" % fetch["appid"], "version": "always-latest",
                    "asset": "(SteamCMD app_update %s)" % fetch["appid"], "note": "self-updates via SteamCMD validate"}
        elif method == "bundled":
            return {"method": method, "url": "(ships with toolkit)", "version": "toolkit",
                    "asset": ", ".join(fetch.get("files", [])), "note": "bundled"}
        elif method == "repo-file":
            p = os.path.join(ROOT, fetch["src"])
            return {"method": method, "url": "file://" + p, "version": "toolkit",
                    "asset": os.path.basename(fetch["src"]),
                    "note": "" if os.path.exists(p) else "MISSING in repo: " + fetch["src"]}
        elif method == "repo-dir":
            return {"method": method, "url": "(repo dir: %s)" % fetch.get("src", "."),
                    "version": "toolkit", "asset": ", ".join(fetch.get("files", [])), "note": ""}
        elif method == "generated":
            return {"method": method, "url": "(generated by wizard)", "version": "toolkit",
                    "asset": ", ".join(fetch.get("produces", [])), "note": "written by /api/save"}
        else:
            return {"method": method, "url": "", "version": "?", "asset": "", "note": "unknown method"}
        r["method"] = method
        r["note"] = ""
        return r
    except Exception as e:                                  # noqa: BLE001
        # try the documented fallback (e.g. Thunderstore for BepInEx)
        if fetch.get("fallback"):
            try:
                fb = dict(fetch["fallback"])
                r = _resolve_thunderstore(fb) if fb["method"] == "thunderstore" else {}
                r["method"] = fb["method"]
                r["note"] = "via fallback (%s); primary failed: %s" % (fb["method"], e)
                return r
            except Exception as e2:                        # noqa: BLE001
                return {"method": method, "url": "", "version": "?", "asset": "", "note": "FAILED + fallback failed: %s" % e2}
        if isinstance(e, LookupError) or getattr(e, "code", None) in (403, 404):
            return {"method": method, "url": "", "version": "none", "asset": "",
                    "note": "no published release for %s yet (or the repo is private) — the plugin DLL ships via GitHub Releases and resolves automatically once a release exists"
                            % fetch.get("repo", "?")}
        return {"method": method, "url": "", "version": "?", "asset": "", "note": "FAILED: %s" % e}


def plan(option):
    m = load_manifest()
    deps = m["options"].get(option)
    if deps is None:
        sys.exit("unknown option '%s'. Choose: %s" % (option, ", ".join(m["options"])))
    cfg = _user_config()
    print("Plan for '%s':\n" % option)
    for dep_id in deps:
        dep = m["dependencies"][dep_id]
        provided = option in (dep.get("provided_by_host") or [])
        r = {"method": dep["fetch"]["method"], "version": "host-provided", "asset": "", "note": "egg installs it server-side"} if provided else resolve(dep, cfg)
        print("  • %-22s %-15s %-12s %s" % (dep_id, r.get("method", "?"), r.get("version", "?"), r.get("note", "")))
        if r.get("url") and not provided:
            print("        %s" % r["url"])
    print("\n(Offline: each item can instead be hand-downloaded from its official URL — see `offline.py`.)")


# ---------------- fetch + verify + extract ----------------
def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def _safe_extract(archive_bytes, dest, kind):
    import io
    import zipfile
    import tarfile
    os.makedirs(dest, exist_ok=True)
    dest_abs = os.path.abspath(dest)
    if kind == "zip":
        z = zipfile.ZipFile(io.BytesIO(archive_bytes))
        names = z.namelist()
        for n in names:
            target = os.path.abspath(os.path.join(dest, n))
            if not target.startswith(dest_abs + os.sep) and target != dest_abs:
                raise ValueError("unsafe path in zip: %s" % n)
        z.extractall(dest)
        return len(names)
    else:
        t = tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:*")
        members = t.getmembers()
        for member in members:
            target = os.path.abspath(os.path.join(dest, member.name))
            if target != dest_abs and not target.startswith(dest_abs + os.sep):
                raise ValueError("unsafe path in tar: %s" % member.name)
            # Block the classic tar symlink/hardlink escape: a link member's OWN path
            # can stay inside dest while its TARGET points out, then a later file gets
            # written through the link to an arbitrary location.
            if member.issym() or member.islnk():
                if member.issym():   # symlink target is relative to the link's directory
                    link_abs = os.path.abspath(os.path.join(os.path.dirname(target), member.linkname))
                else:                # hardlink target is relative to the archive root
                    link_abs = os.path.abspath(os.path.join(dest, member.linkname))
                if link_abs != dest_abs and not link_abs.startswith(dest_abs + os.sep):
                    raise ValueError("unsafe link target in tar: %s -> %s" % (member.name, member.linkname))
        t.extractall(dest)
        return len(members)


def _load_lock():
    try:
        with open(LOCKFILE, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _save_lock(lock):
    tmp = LOCKFILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(lock, f, indent=2)
    os.replace(tmp, LOCKFILE)


def _fetch_repo(dep_id, dep, dest, deployer=None):
    """Place a toolkit file/dir that ships INSIDE the repo (method repo-file/repo-dir).
    Local install -> copy onto disk; remote install -> push via the deployer (SFTP)."""
    fetch = dep["fetch"]
    out_root = os.path.join(dest, dep["dest"]) if dep["dest"] not in (".", "") else dest
    if fetch["method"] == "repo-file":
        items, flat = [fetch["src"]], True
    else:
        base = fetch.get("src", ".")
        files = fetch.get("files")
        if not files:                                   # no explicit list -> every file in the dir
            import glob as _glob
            srcdir = os.path.join(ROOT, *base.split("/")) if base not in (".", "") else ROOT
            files = sorted(os.path.basename(p) for p in _glob.glob(os.path.join(srcdir, "*")) if os.path.isfile(p))
        items, flat = [(base + "/" + f) if base not in (".", "") else f for f in files], False
    placed = []
    for rel in items:
        sp = os.path.join(ROOT, *rel.split("/"))
        if not os.path.exists(sp):
            return {"id": dep_id, "ok": False, "note": "missing repo source: %s" % rel}
        with open(sp, "rb") as f:
            data = f.read()
        target = out_root if flat else os.path.join(out_root, os.path.basename(rel))
        if deployer is not None:
            deployer.put_bytes(data, target)
        else:
            os.makedirs(os.path.dirname(target) or out_root, exist_ok=True)
            with open(target, "wb") as f:
                f.write(data)
        placed.append(os.path.basename(target))
    where = "(sftp) " if deployer is not None else ""
    return {"id": dep_id, "ok": True, "placed": "%s%d file(s) -> %s" % (where, len(placed), out_root)}


def fetch_one(dep_id, dest, offline_dir=None, deployer=None):
    """Download (or locate offline) + verify a single dependency into dest. Returns a result dict."""
    m = load_manifest()
    dep = m["dependencies"][dep_id]
    method = dep["fetch"]["method"]
    if method == "steamcmd":
        return {"id": dep_id, "ok": None, "note": "use `setup.py` SteamCMD step (app_update %s) — not a file download" % dep["fetch"]["appid"]}
    if method == "bundled":
        return {"id": dep_id, "ok": True, "note": "bundled with the toolkit"}
    if method == "generated":
        return {"id": dep_id, "ok": True, "note": "generated by the wizard (config/secrets/cfg)"}
    if method in ("repo-file", "repo-dir"):
        return _fetch_repo(dep_id, dep, dest, deployer)

    r = resolve(dep)
    if not r.get("url"):
        return {"id": dep_id, "ok": False, "note": r.get("note", "could not resolve")}

    # acquire the bytes — offline (local folder) or online (download)
    if offline_dir:
        import glob
        pat = dep.get("offline", {}).get("filename", r.get("asset", "*"))
        hits = glob.glob(os.path.join(offline_dir, pat))
        if not hits:
            return {"id": dep_id, "ok": False, "note": "offline file not found: %s (download from %s)"
                    % (pat, dep.get("offline", {}).get("official_url", "?"))}
        data = open(hits[0], "rb").read()
        src = hits[0]
    else:
        data = _http(r["url"], raw=True)
        src = r["url"]
    sha = _sha256(data)

    # integrity: a manifest-pinned sha wins; otherwise TOFU — the FIRST fetch records
    # the sha in sources.lock.json and every later fetch MUST match it. (Previously the
    # lock was written but never re-read, so a changed sha256_tofu download passed.)
    lock = _load_lock()
    known = (dep.get("integrity", {}).get("sha256") or "").strip()
    locked = (lock.get(dep_id) or {}).get("sha256", "")
    pinned = known or locked
    if pinned and pinned.lower() != sha.lower():
        return {"id": dep_id, "ok": False,
                "note": "SHA-256 MISMATCH vs %s pin (expected %s, got %s)"
                        % ("manifest" if known else "lockfile TOFU", pinned, sha)}

    # place: extract archive, or copy a single file
    dest_path = os.path.join(dest, dep["dest"]) if not dep["dest"].endswith("/") else dest
    placed = None
    if r["asset"].endswith((".zip", ".tar.gz", ".tgz")) or "extract" in dep.get("post", []):
        kind = "zip" if r["asset"].endswith(".zip") else "tar"
        target_dir = os.path.join(dest, dep["dest"]) if dep["dest"] not in (".", "") else dest
        n = _safe_extract(data, target_dir, kind)
        placed = "%d files -> %s" % (n, target_dir)
    else:
        os.makedirs(os.path.dirname(dest_path) or dest, exist_ok=True)
        with open(dest_path, "wb") as f:
            f.write(data)
        placed = dest_path

    lock = _load_lock()
    lock[dep_id] = {"version": r.get("version"), "sha256": sha, "source": src, "asset": r.get("asset")}
    _save_lock(lock)
    return {"id": dep_id, "ok": True, "version": r.get("version"), "sha256": sha[:16], "placed": placed,
            "note": r.get("note", "")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["plan", "resolve", "fetch"])
    ap.add_argument("target")
    ap.add_argument("--dest", default=".")
    ap.add_argument("--offline", default=None)
    a = ap.parse_args()
    if a.cmd == "plan":
        plan(a.target)
    elif a.cmd == "resolve":
        m = load_manifest()
        dep = m["dependencies"].get(a.target)
        if not dep:
            sys.exit("unknown dependency. Have: %s" % ", ".join(m["dependencies"]))
        print(json.dumps(resolve(dep), indent=2))
    elif a.cmd == "fetch":
        m = load_manifest()
        deps = m["options"].get(a.target, [a.target] if a.target in m["dependencies"] else None)
        if deps is None:
            sys.exit("unknown option/dependency '%s'" % a.target)
        for dep_id in deps:
            res = fetch_one(dep_id, a.dest, a.offline)
            mark = "ok" if res.get("ok") else ("--" if res.get("ok") is None else "FAIL")
            print("[%4s] %-22s %s" % (mark, dep_id, res.get("placed") or res.get("note") or ""))


if __name__ == "__main__":
    main()
