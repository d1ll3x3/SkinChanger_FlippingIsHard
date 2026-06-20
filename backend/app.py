"""
Charm & Skin Replacer — web backend.

Serves skins/charms "on the fly": users upload their files and the mod downloads them
on demand, matching each player by Steam ID or Steam name.

There is intentionally NO login. The uploader just provides their Steam profile (the page
auto-detects their Steam ID and name from the public profile XML — the keyless trick that
sites like steamid.xyz use). This keeps uploading frictionless; the trade-off is that
uploads are not authenticated, which is acceptable for a cosmetic community mod.

Storage is pluggable (see store.py):
  * Local disk for development.
  * A GitHub repository in production — free, no credit card, served through GitHub's CDN
    so the mod downloads straight from raw.githubusercontent.com and this app only handles
    uploads.

Because the static layout is keyed by Steam ID (asset/<steamid>/<kind>), a Steam ID is
required to upload. In-game matching is still by name; the manifest maps name -> steamid.

Endpoints:
    GET    /                      Upload page (HTML).
    POST   /api/resolve           Resolve Steam ID + persona name from input or profile URL.
    POST   /api/upload            Upload/replace a skin|emission|charm.
    GET    /api/record            Current assets for a given identity (status display).
    DELETE /api/asset/{kind}      Delete an asset.
    GET    /api/manifest          Full manifest (JSON, human-readable).
    GET    /api/manifest.tsv      Manifest as TSV (consumed by the mod; in production the mod
                                   reads this from raw.githubusercontent instead).
    GET    /asset/{key}/{kind}    Binary download (consumed by the mod; production uses raw).

Local run:
    pip install -r requirements.txt
    uvicorn app:app --reload --port 8000
"""

from __future__ import annotations

import hashlib
import io
import os
import re
from typing import Optional

# Load backend/.env if present (handy for local GitHub-mode testing). On Render the real
# environment variables are set in the dashboard, so this is a no-op there.
try:
    from dotenv import load_dotenv

    load_dotenv()
except ImportError:
    pass

import httpx
from fastapi import FastAPI, File, Form, HTTPException, Response, UploadFile
from fastapi.responses import HTMLResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pathlib import Path

import store as storage

# ──────────────────────────────────────────────────────────────────────────────
# Config
# ──────────────────────────────────────────────────────────────────────────────

MAX_IMAGE_BYTES = int(os.environ.get("MAX_IMAGE_BYTES", str(4 * 1024 * 1024)))
MAX_CHARM_BYTES = int(os.environ.get("MAX_CHARM_BYTES", str(25 * 1024 * 1024)))

KINDS = storage.KINDS
# Public extension per kind (used for the download filename so the mod caches correctly).
KIND_EXT = {"skin": ".png", "emission": ".png", "charm": ".hatbundle"}

_STEAMID64_RE = re.compile(r"^\d{17}$")

app = FastAPI(title="Charm & Skin Replacer backend")
_store = storage.get_store()

_static_dir = Path(__file__).parent / "static"
if _static_dir.is_dir():
    app.mount("/static", StaticFiles(directory=str(_static_dir)), name="static")


def _normalize_name(name: str) -> str:
    name = (name or "").strip().lower()
    return re.sub(r"[\t\r\n]+", " ", name)


def _find_record(index: dict, key: str) -> Optional[dict]:
    """Resolve a record by Steam ID or by normalized name."""
    key = (key or "").strip()
    if key in index:
        return index[key]
    norm = _normalize_name(key)
    for record in index.values():
        if record.get("name") == norm:
            return record
    return None


# ──────────────────────────────────────────────────────────────────────────────
# Steam ID resolution (no login — just a convenience for the user)
# ──────────────────────────────────────────────────────────────────────────────

_PROFILE_ID_RE = re.compile(r"steamcommunity\.com/profiles/(\d{17})")
_VANITY_RE = re.compile(r"steamcommunity\.com/id/([^/?#]+)")
_XML_STEAMID64_RE = re.compile(r"<steamID64>(\d+)</steamID64>")
_XML_NAME_RE = re.compile(r"<steamID>\s*<!\[CDATA\[(.*?)\]\]>\s*</steamID>", re.DOTALL)


@app.post("/api/resolve")
async def api_resolve(value: str = Form(...)):
    """
    Best-effort resolution of a Steam ID and persona name from raw input: a SteamID64, a
    /profiles/<id> URL, or a /id/<vanity> URL. Uses Steam's public profile XML endpoint
    (no API key needed), the same keyless approach as steamid.xyz.
    """
    value = (value or "").strip()
    if not value:
        raise HTTPException(400, "Empty value")

    steamid: Optional[str] = None
    vanity: Optional[str] = None

    if _STEAMID64_RE.match(value):
        steamid = value
    else:
        m = _PROFILE_ID_RE.search(value)
        if m:
            steamid = m.group(1)
        else:
            m = _VANITY_RE.search(value)
            vanity = m.group(1) if m else value

    resolved_id, name = await _resolve_via_xml(steamid=steamid, vanity=vanity)
    steamid = steamid or resolved_id

    if not steamid:
        raise HTTPException(
            422,
            "Could not detect a Steam ID. Paste your SteamID64 (17 digits), a "
            "steamcommunity.com/profiles/... or /id/... URL, or get your ID from "
            "https://steamid.xyz and paste it here.",
        )
    return JSONResponse({"steamid": steamid, "name": name or ""})


async def _resolve_via_xml(steamid: Optional[str] = None, vanity: Optional[str] = None):
    if steamid:
        url = f"https://steamcommunity.com/profiles/{steamid}/?xml=1"
    elif vanity:
        url = f"https://steamcommunity.com/id/{vanity}/?xml=1"
    else:
        return None, None
    try:
        async with httpx.AsyncClient(timeout=10, follow_redirects=True) as client:
            resp = await client.get(url, headers={"User-Agent": "CharmReplacer/1.0"})
        text = resp.text
        sid_m = _XML_STEAMID64_RE.search(text)
        name_m = _XML_NAME_RE.search(text)
        sid = sid_m.group(1) if sid_m else None
        name = name_m.group(1).strip() if name_m else None
        return sid, name
    except httpx.HTTPError:
        return None, None


# ──────────────────────────────────────────────────────────────────────────────
# Upload / delete / status
# ──────────────────────────────────────────────────────────────────────────────


def _validate_png(data: bytes) -> None:
    if len(data) > MAX_IMAGE_BYTES:
        raise HTTPException(413, f"PNG too large (max {MAX_IMAGE_BYTES} bytes)")
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise HTTPException(400, "File is not a valid PNG")
    try:
        from PIL import Image  # lazy import: Pillow is optional

        with Image.open(io.BytesIO(data)) as img:
            img.verify()
            if img.format != "PNG":
                raise HTTPException(400, "File is not a valid PNG")
    except ImportError:
        pass


def _validate_charm(data: bytes) -> None:
    if len(data) > MAX_CHARM_BYTES:
        raise HTTPException(413, f"Charm too large (max {MAX_CHARM_BYTES} bytes)")
    if not data.startswith(b"UnityFS"):
        raise HTTPException(400, "Charm does not look like a Unity AssetBundle (hatbundle)")


@app.post("/api/upload")
async def api_upload(
    kind: str = Form(...),
    name: str = Form(...),
    steamid: str = Form(...),
    file: UploadFile = File(...),
):
    if kind not in KINDS:
        raise HTTPException(400, f"Invalid kind (use one of {KINDS})")
    display = (name or "").strip()
    if not display:
        raise HTTPException(400, "Steam name is required")
    steamid = (steamid or "").strip()
    if not _STEAMID64_RE.match(steamid):
        raise HTTPException(400, "A 17-digit Steam ID is required (use Detect to fill it in)")

    data = await file.read()
    if not data:
        raise HTTPException(400, "Empty file")
    if kind in ("skin", "emission"):
        _validate_png(data)
    else:
        _validate_charm(data)

    digest = hashlib.sha256(data).hexdigest()
    await _store.put_blob(steamid, kind, data)

    index = await _store.load_index()
    record = index.setdefault(
        steamid, {"steamid": steamid, "name": _normalize_name(display), "display": display, "assets": {}}
    )
    record["name"] = _normalize_name(display)
    record["display"] = display
    record["assets"][kind] = {"hash": digest}
    await _store.save_index(index)
    return JSONResponse({"ok": True, "kind": kind, "hash": digest})


@app.get("/api/record")
async def api_record(steamid: str = "", name: str = ""):
    index = await _store.load_index()
    record = _find_record(index, steamid) or _find_record(index, name)
    assets = {k: bool(record.get("assets", {}).get(k)) for k in KINDS} if record else {k: False for k in KINDS}
    return JSONResponse({"assets": assets})


@app.delete("/api/asset/{kind}")
async def api_delete_asset(kind: str, steamid: str = "", name: str = ""):
    if kind not in KINDS:
        raise HTTPException(400, "Invalid kind")
    index = await _store.load_index()
    record = _find_record(index, steamid) or _find_record(index, name)
    if not record or kind not in record.get("assets", {}):
        raise HTTPException(404, "No such asset for that identity")
    sid = record["steamid"]
    await _store.delete_blob(sid, kind)
    del record["assets"][kind]
    if not record["assets"]:
        index.pop(sid, None)
    await _store.save_index(index)
    return JSONResponse({"ok": True, "kind": kind})


# ──────────────────────────────────────────────────────────────────────────────
# Manifest + download (consumed by the mod; in production served by GitHub raw)
# ──────────────────────────────────────────────────────────────────────────────


@app.get("/api/manifest")
async def api_manifest():
    index = await _store.load_index()
    entries = []
    for record in index.values():
        assets = {k: v["hash"] for k, v in record.get("assets", {}).items()}
        if not assets:
            continue
        entries.append(
            {
                "steamid": record.get("steamid", ""),
                "name": record.get("name", ""),
                "display": record.get("display", ""),
                "assets": assets,
            }
        )
    return JSONResponse({"version": 1, "entries": entries})


@app.get("/api/manifest.tsv", response_class=HTMLResponse)
async def api_manifest_tsv():
    index = await _store.load_index()
    return HTMLResponse(storage.build_manifest_tsv(index), media_type="text/plain; charset=utf-8")


@app.get("/asset/{key}/{kind}")
async def get_asset(key: str, kind: str):
    if kind not in KINDS:
        raise HTTPException(400, "Invalid kind")
    index = await _store.load_index()
    record = _find_record(index, key)
    if not record or kind not in record.get("assets", {}):
        raise HTTPException(404, "Not found")
    data = await _store.get_blob(record["steamid"], kind)
    if data is None:
        raise HTTPException(404, "Blob missing")
    media = "image/png" if kind != "charm" else "application/octet-stream"
    filename = f"{record.get('display') or key}{KIND_EXT[kind]}"
    return Response(
        content=data,
        media_type=media,
        headers={"Content-Disposition": f'inline; filename="{filename}"'},
    )


# ──────────────────────────────────────────────────────────────────────────────
# Upload page
# ──────────────────────────────────────────────────────────────────────────────


@app.get("/", response_class=HTMLResponse)
async def index_page():
    page = _static_dir / "index.html"
    if page.exists():
        return HTMLResponse(page.read_text("utf-8"))
    return HTMLResponse("<h1>Charm & Skin Replacer backend</h1><p>Missing static/index.html</p>")


@app.get("/healthz")
async def healthz():
    return {"ok": True, "store": storage.store_kind()}
