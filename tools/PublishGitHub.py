"""Publish a reviewed local repository and release using Git's configured credential helper.

Credentials stay in memory and are never written to files or printed.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def git(*args, capture=False):
    return subprocess.run(["git", *args], cwd=ROOT, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def credentials(owner):
    environment = dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="Never")
    result = subprocess.run(["git", "-c", "credential.interactive=never", "credential", "fill"],
                            cwd=ROOT, input=f"protocol=https\nhost=github.com\nusername={owner}\n\n",
                            text=True, capture_output=True, env=environment, timeout=45)
    fields = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    if result.returncode != 0 or not fields.get("password"):
        raise RuntimeError("No usable GitHub credential in Git's configured helper. Authenticate Git before publishing.")
    return fields["password"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["inspect", "publish"])
    parser.add_argument("--owner", required=True)
    parser.add_argument("--repository", default="unityMesh")
    parser.add_argument("--public", action="store_true", help="Explicitly create a public repository")
    args = parser.parse_args()
    token = credentials(args.owner)
    opener = urllib.request.build_opener(NoRedirect)

    def api(path, method="GET", payload=None, raw=None, content_type="application/json"):
        url = path if path.startswith("https://") else "https://api.github.com" + path
        if urllib.parse.urlparse(url).hostname not in {"api.github.com", "uploads.github.com"}:
            raise RuntimeError("Unexpected GitHub API host")
        body = raw if raw is not None else (json.dumps(payload).encode() if payload is not None else None)
        request = urllib.request.Request(url, data=body, method=method, headers={
            "Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "StylizedMeshStudio-Publisher",
            "Content-Type": content_type,
        })
        try:
            with opener.open(request, timeout=60) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 404 and method == "GET":
                return None
            # Do not dump request headers, credentials or arbitrary server response data.
            raise RuntimeError(f"GitHub {method} request failed with HTTP {error.code}") from None

    user = api("/user")
    if user["login"].lower() != args.owner.lower():
        raise RuntimeError("Credential account does not match requested repository owner")
    repository = f"{args.owner}/{args.repository}"
    base = "/repos/" + repository
    repo = api(base)
    print("Authenticated GitHub account:", user["login"])
    print("Repository:", repo["html_url"] if repo else repository + " (not created)")
    if args.mode == "inspect":
        return
    version = json.loads((ROOT / "Packages/com.codex.stylized-mesh/package.json").read_text())["version"]
    tag = "v" + version
    notes = (ROOT / f"docs/releases/{tag}.md").read_text(encoding="utf-8")
    assets = [ROOT / f"artifacts/StylizedMeshStudio-{version}.unitypackage",
              ROOT / f"artifacts/StylizedMeshStudio-{version}-UPM.zip",
              ROOT / "artifacts/validation.txt", ROOT / "artifacts/algorithm-checks.txt",
              ROOT / "artifacts/import-validation.txt", ROOT / "artifacts/validation-status.json",
              ROOT / "artifacts/privacy-checks.json"]
    if (ROOT / "artifacts/avatar-validation.json").is_file():
        assets.append(ROOT / "artifacts/avatar-validation.json")
    if not all(path.is_file() for path in assets):
        raise RuntimeError("Build and verify the release assets first")
    if git("status", "--porcelain", capture=True).strip():
        raise RuntimeError("Commit all reviewed changes before publishing")
    if repo is None:
        repo = api("/user/repos", "POST", {
            "name": args.repository, "private": not args.public, "auto_init": False,
            "description": "Unity mesh simplification, vertex sculpting and UV editing for static and skinned avatars.",
            "has_issues": True, "has_wiki": False,
        })
    elif args.public and repo["private"]:
        raise RuntimeError("Existing repository is private; this script will not change its visibility")
    remote = "https://github.com/" + repository + ".git"
    remotes = git("remote", capture=True).splitlines()
    if "origin" in remotes:
        if git("remote", "get-url", "origin", capture=True).strip() != remote:
            raise RuntimeError("Existing origin points to a different repository")
    else:
        if api(base + "/branches/main") is not None:
            raise RuntimeError("Existing repository already has a main branch; inspect it before connecting")
        git("remote", "add", "origin", remote)
    git("config", "credential.https://github.com.username", args.owner)
    git("push", "-u", "origin", "main")
    commit = git("rev-parse", "HEAD", capture=True).strip()
    branch = api(base + "/branches/main")
    if not branch or branch["commit"]["sha"] != commit:
        raise RuntimeError("Remote main does not match the reviewed local commit")
    existing_tag = api(base + "/git/ref/tags/" + tag)
    if existing_tag:
        raise RuntimeError("Release tag already exists; never overwrite a published tag")
    git("tag", "-a", tag, "-m", "Stylized Mesh Studio " + tag)
    git("push", "origin", tag)
    # Publish only after every attachment has uploaded successfully.
    release = api(base + "/releases", "POST", {
        "tag_name": tag, "target_commitish": commit, "name": "Stylized Mesh Studio " + tag,
        "body": notes, "draft": True, "prerelease": False,
    })
    upload = release["upload_url"].split("{", 1)[0]
    for path in assets:
        kind = "application/zip" if path.suffix == ".zip" else "application/octet-stream"
        asset = api(upload + "?" + urllib.parse.urlencode({"name": path.name}), "POST",
                    raw=path.read_bytes(), content_type=kind)
        if asset["size"] != path.stat().st_size:
            raise RuntimeError("Uploaded attachment size mismatch")
        print("Uploaded:", path.name)
    release = api(base + "/releases/" + str(release["id"]), "PATCH", {"draft": False, "make_latest": "true"})
    checked = api(base + "/releases/tags/" + tag)
    if checked["draft"] or len(checked["assets"]) != len(assets):
        raise RuntimeError("Published release verification failed")
    state = {"repository_url": repo["html_url"], "release_url": release["html_url"],
             "commit": commit, "tag": tag, "visibility": "private" if repo["private"] else "public"}
    (ROOT / ".github-publish-state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
    print(json.dumps(state, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.SubprocessError, OSError) as error:
        print("Publication stopped:", str(error), file=sys.stderr)
        sys.exit(1)
