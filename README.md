# Charm & Skin Replacer

## Features

- **Multiplayer Skins & Charms**: Your friends can see your custom skin and you can see theirs! Just place each other's files in the mod folder!
- **Multi-version**: Compatible with versions 0.9+, 0.10+, 0.11+, and playtest.

## Installation

1. Make sure BepInEx is installed for *Flipping is Hard*.
2. Download the latest `.zip` from the **Releases** tab.
3. Extract the contents into your BepInEx plugins folder. The structure must match exactly:

    ```text
    Flipping is Hard Demo/BepInEx/plugins/charmreplacer/
    ├── CharmReplacer.dll
    ├── System.Drawing.Common.dll
    ├── Microsoft.Win32.SystemEvents.dll
    ├── Skin.png (Your own skin)
    ├── Skin_Emission.png (Your own Emission map)
    ├── hatbundle (Your own charm)
    ├── Skins/
    │   └── YourFriendsNames.png
    │   └── YourFriendsNames_emission.png
    └── Charms/
        └── YourFriendsNames.hatbundle
    ```

## How to see your friends' skins and charms

To see your friends' custom skins and charms, just ask them for their files and place them in the `Skins/` and `Charms/` folders inside the mod folder.

**Make sure the filename matches their in-game name exactly (Steam name).**

For example, if your friend is called `ElProGamer`, put their texture as `ElProGamer.png` inside the `Skins/` folder!

For emission textures, add `_emission` to the name: `ElProGamer_emission.png`.

For charms, add `ElProGamer.` to the name: `ElProGamer.hatbundle`.

## Automatic skins from the web backend (optional)

Instead of swapping files by hand, the mod can **download skins and charms automatically**
from a web backend. Players upload their own skin/charm once (logging in with Steam), and
anyone running the mod downloads them on demand when that player appears in-game — matched by
Steam ID or Steam name. No more asking friends for their files.

**For players:** open the backend's website, log in with Steam, upload your `Skin.png`,
optional emission map, and `hatbundle`. That's it. Anyone with the mod will see them.

**To enable it in the mod:** set the backend URL in the config file
`BepInEx/config/com.dani.charmreplacer.cfg` (created on first run):

```ini
[RemoteSkins]
Enabled = true
BackendBaseUrl = https://raw.githubusercontent.com/youruser/charm-skins-data/main
```

The URL is the **raw GitHub base** of the data repo (that's where skins are served from).
The upload website players visit is a separate URL — see [`backend/`](backend/).

Downloaded files are cached in your `Skins/` and `Charms/` folders, so the game only
re-downloads when a skin changes. **Files you placed manually always take precedence** —
the backend never overwrites your own skin.

The backend itself lives in [`backend/`](backend/) (see its README to run/deploy it).

## Charm physics (MagicaCloth 2)

For a charm to **swing with physics**, the prefab you put in the `hatbundle` **must include a
configured MagicaCloth 2 component**. A mesh-only bundle (just a mesh + material) will load and
show the charm, but it hangs **static** — the mod cannot synthesize physics a bundle does not
contain, and it will not reuse the game's own cloth (that is bound to the original phone charm
mesh and refuses to rebind). If you see `[CharmPhysics] Mesh-swap path cannot drive cloth
physics` in the logs, re-export the charm with a MagicaCloth 2 component. See
[`Tuto_Templates/`](Tuto_Templates) for presets and steps.

## Charm & Skin Templates

https://github.com/d1ll3x3/SkinChanger_FlippingIsHard/releases/download/v1.1.0/Tuto_Templates.zip

---

*Enjoy breaking phones in style.*
