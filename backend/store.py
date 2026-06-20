"""
Storage backends for the Charm & Skin Replacer service.

Two interchangeable stores, selected by environment:

  * LocalStore  — files on disk. Used for local development/testing.
  * GitHubStore — a GitHub repository via the Contents API. Used in production: the repo
                  is free, has no card requirement, and is served through GitHub's CDN
                  (raw.githubusercontent.com), so the mod downloads straight from GitHub and
                  the upload app never has to stay awake to serve reads.

Both expose the same async interface used by app.py. Layout (identical in both):

    index.json                 # {"<steamid>": {steamid, name, display, assets: {...}}}
    api/manifest.tsv           # generated; consumed by the mod
    asset/<steamid>/<kind>     # raw asset bytes (kind ∈ skin|emission|charm), no extension

The asset path matches the URL scheme the mod requests ({base}/asset/<steamid>/<kind>),
so pointing the mod's BackendBaseUrl at the repo's raw base needs no mod changes.
"""

from __future__ import annotations

import base64
import json
import os
from pathlib import Path
from typing import Optional

import httpx

KINDS = ("skin", "emission", "charm")

INDEX_PATH = "index.json"
MANIFEST_PATH = "api/manifest.tsv"


def asset_path(steamid: str, kind: str) -> str:
    return f"asset/{steamid}/{kind}"


def build_manifest_tsv(index: dict) -> str:
    """
    One line per player with assets:
        steamid \\t name \\t skin_hash \\t emission_hash \\t charm_hash
    Each hash field is the sha256 or "-" when that asset is absent.
    """
    lines = ["#steamid\tname\tskin\temission\tcharm"]
    for record in index.values():
        assets = record.get("assets", {})
        if not assets:
            continue
        lines.append(
            "\t".join(
                [
                    record.get("steamid", ""),
                    record.get("name", ""),
                    assets.get("skin", {}).get("hash", "-") if assets.get("skin") else "-",
                    assets.get("emission", {}).get("hash", "-") if assets.get("emission") else "-",
                    assets.get("charm", {}).get("hash", "-") if assets.get("charm") else "-",
                ]
            )
        )
    return "\n".join(lines) + "\n"


# ──────────────────────────────────────────────────────────────────────────────
# Local disk store
# ──────────────────────────────────────────────────────────────────────────────


class LocalStore:
    def __init__(self, root: Path):
        self.root = root
        self.root.mkdir(parents=True, exist_ok=True)

    def _p(self, rel: str) -> Path:
        return self.root / rel

    async def load_index(self) -> dict:
        p = self._p(INDEX_PATH)
        if not p.exists():
            return {}
        try:
            return json.loads(p.read_text("utf-8"))
        except (json.JSONDecodeError, OSError):
            return {}

    async def save_index(self, index: dict) -> None:
        p = self._p(INDEX_PATH)
        p.parent.mkdir(parents=True, exist_ok=True)
        tmp = p.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(index, ensure_ascii=False, indent=2), "utf-8")
        tmp.replace(p)
        # Keep the static manifest in sync so the on-disk layout mirrors production.
        man = self._p(MANIFEST_PATH)
        man.parent.mkdir(parents=True, exist_ok=True)
        man.write_text(build_manifest_tsv(index), "utf-8")

    async def put_blob(self, steamid: str, kind: str, data: bytes) -> None:
        p = self._p(asset_path(steamid, kind))
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)

    async def get_blob(self, steamid: str, kind: str) -> Optional[bytes]:
        p = self._p(asset_path(steamid, kind))
        return p.read_bytes() if p.exists() else None

    async def delete_blob(self, steamid: str, kind: str) -> None:
        try:
            self._p(asset_path(steamid, kind)).unlink()
        except FileNotFoundError:
            pass


# ──────────────────────────────────────────────────────────────────────────────
# GitHub repository store (Contents API)
# ──────────────────────────────────────────────────────────────────────────────


class GitHubStore:
    def __init__(self, repo: str, token: str, branch: str = "main"):
        self.repo = repo            # "owner/name"
        self.token = token
        self.branch = branch
        self.api = f"https://api.github.com/repos/{repo}/contents"
        self._headers = {
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        }

    async def _get(self, path: str):
        """Return (bytes, sha) for a file, or (None, None) if it does not exist."""
        async with httpx.AsyncClient(timeout=20) as client:
            r = await client.get(f"{self.api}/{path}", params={"ref": self.branch}, headers=self._headers)
            if r.status_code == 404:
                return None, None
            r.raise_for_status()
            data = r.json()
            sha = data.get("sha")
            # The Contents API inlines base64 only for files <= 1 MB; larger files come back
            # with empty content and must be fetched via download_url.
            if data.get("encoding") == "base64" and data.get("content"):
                return base64.b64decode(data["content"]), sha
            dl = data.get("download_url")
            if dl:
                rd = await client.get(dl, headers=self._headers)
                rd.raise_for_status()
                return rd.content, sha
            return b"", sha

    async def _put(self, path: str, data: bytes, message: str) -> None:
        _, sha = await self._get(path)
        body = {
            "message": message,
            "content": base64.b64encode(data).decode("ascii"),
            "branch": self.branch,
        }
        if sha:
            body["sha"] = sha
        async with httpx.AsyncClient(timeout=30) as client:
            r = await client.put(f"{self.api}/{path}", json=body, headers=self._headers)
        r.raise_for_status()

    async def _delete(self, path: str, message: str) -> None:
        _, sha = await self._get(path)
        if not sha:
            return
        body = {"message": message, "sha": sha, "branch": self.branch}
        async with httpx.AsyncClient(timeout=30) as client:
            r = await client.request("DELETE", f"{self.api}/{path}", json=body, headers=self._headers)
        r.raise_for_status()

    async def load_index(self) -> dict:
        data, _ = await self._get(INDEX_PATH)
        if not data:
            return {}
        try:
            return json.loads(data.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            return {}

    async def save_index(self, index: dict) -> None:
        await self._put(INDEX_PATH, json.dumps(index, ensure_ascii=False, indent=2).encode("utf-8"),
                        "Update index")
        # The mod reads this committed manifest from raw.githubusercontent.
        await self._put(MANIFEST_PATH, build_manifest_tsv(index).encode("utf-8"), "Update manifest")

    async def put_blob(self, steamid: str, kind: str, data: bytes) -> None:
        await self._put(asset_path(steamid, kind), data, f"Upload {kind} for {steamid}")

    async def get_blob(self, steamid: str, kind: str) -> Optional[bytes]:
        data, _ = await self._get(asset_path(steamid, kind))
        return data

    async def delete_blob(self, steamid: str, kind: str) -> None:
        await self._delete(asset_path(steamid, kind), f"Delete {kind} for {steamid}")


# ──────────────────────────────────────────────────────────────────────────────
# Factory
# ──────────────────────────────────────────────────────────────────────────────


def get_store():
    """GitHubStore when GITHUB_TOKEN + GITHUB_REPO are set, else LocalStore."""
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    repo = os.environ.get("GITHUB_REPO", "").strip()
    if token and repo:
        branch = os.environ.get("GITHUB_BRANCH", "main").strip() or "main"
        return GitHubStore(repo, token, branch)
    root = Path(os.environ.get("STORAGE_DIR", "storage")).resolve()
    return LocalStore(root)


def store_kind() -> str:
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    repo = os.environ.get("GITHUB_REPO", "").strip()
    return "github" if (token and repo) else "local"
