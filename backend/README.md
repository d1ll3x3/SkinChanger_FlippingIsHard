# Skins/charms backend

Web service where users upload their skins/charms. The mod downloads them **on demand**
in-game, matching each file to a player by **Steam ID** or **Steam name**.

There is **no login**: users paste their Steam profile URL and the page auto-detects their
Steam ID and name from the public profile XML — the keyless trick https://steamid.xyz uses.

## How it works (free, no credit card)

Storage is pluggable (see `store.py`):

- **Local disk** — used automatically when no GitHub env vars are set (for development).
- **A GitHub repository** — used in production. The repo holds the skins/charms and the
  manifest, and **the mod downloads straight from GitHub's CDN** (`raw.githubusercontent.com`).
  This means the read path never sleeps and costs nothing; the upload app only handles writes.

```
Browser ──upload──▶  Render app (FastAPI)  ──commit──▶  GitHub repo (public)
                                                            │ raw CDN
Mod in-game  ◀──────────── download manifest + assets ──────┘
```

Because the static layout is keyed by Steam ID (`asset/<steamid>/<kind>`), a **Steam ID is
required to upload** (the page auto-detects it). In-game matching is still by name; the
manifest maps name → steamid.

## Local run (disk storage)

```bash
cd backend
python -m venv .venv
# Windows: .venv\Scripts\activate   |  Linux/macOS: source .venv/bin/activate
pip install -r requirements.txt
uvicorn app:app --reload --port 8000
```

Open http://localhost:8000. Files are stored under `backend/storage/` in the exact layout
GitHub would hold (`index.json`, `api/manifest.tsv`, `asset/<steamid>/<kind>`).

## Production setup (GitHub + Render)

1. **Create the data repo (PUBLIC):** on GitHub, create an empty public repo, e.g.
   `youruser/charm-skins-data`. It must be public so `raw.githubusercontent.com` serves it.

2. **Create a token:** GitHub → Settings → Developer settings → **Fine-grained tokens** →
   *Generate new token*. Repository access: only the data repo. Permissions:
   **Contents → Read and write**. Copy the token (`github_pat_...`).

3. **Deploy the app on Render (free, no card):** push this project to GitHub, then on
   https://render.com → *New +* → **Blueprint** → pick the repo (it reads the root
   `render.yaml`, which sets `rootDir: backend`).
   In the service's **Environment** tab set:
   - `GITHUB_REPO = youruser/charm-skins-data`
   - `GITHUB_TOKEN = github_pat_...`
   - `GITHUB_BRANCH = main`
   Redeploy. Visit the Render URL and check `/healthz` shows `"store":"github"`.

4. **Point the mod at GitHub raw:** in `BepInEx/config/com.dani.charmreplacer.cfg`:
   ```ini
   [RemoteSkins]
   Enabled = true
   BackendBaseUrl = https://raw.githubusercontent.com/youruser/charm-skins-data/main
   ```
   (Note: that's the **raw GitHub** base, not the Render URL. The Render URL is only the
   upload website that players visit in a browser.)

## Endpoints consumed by the mod

| Method | Path | Description |
|--------|------|-------------|
| `GET`  | `/api/manifest.tsv` | Players with assets + hashes (TSV). |
| `GET`  | `/asset/{steamid}/{kind}` | Binary download. `kind` ∈ `skin`, `emission`, `charm`. |

In production these are plain files in the repo, served by GitHub's CDN.

## Notes

- **Caching:** `raw.githubusercontent.com` caches for ~5 min, so a freshly uploaded skin can
  take a few minutes to appear. Acceptable for a cosmetic mod.
- **Charm size:** capped at ~25 MB to stay within the GitHub Contents API comfort zone.
- **Moderation:** no content moderation; PNG/UnityFS signatures and size are validated. Remove
  bad entries by deleting the files + the index entry in the data repo.
- **Uploads are unauthenticated by design.** If impersonation becomes a problem, add a rate
  limiter in front or reintroduce a login.
