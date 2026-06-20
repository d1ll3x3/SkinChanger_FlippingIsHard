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
## Skin Upload

Public upload page: people can now upload their own skin / emission / charm from a
website (no manual file swapping needed). Uploads are matched in-game by Steam ID or name.
→ https://charm-skins-data.onrender.com/

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
