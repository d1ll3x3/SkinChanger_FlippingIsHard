# Charm & Skin Replacer

## Features

- **Full game support**: v1.5.x targets the Steam release of *Flipping is Hard* (the v1.7 skin rework,
  where covers and charms are one system). For the demo and playtests, use v1.4.x.
- **Custom entries in the game's menu**: a **Custom** entry appears first in the **Cover** and
  **Charm** tabs of the customization screen. Click it to switch your custom cover or charm on or off;
  the preview and the main-menu phone show it too.
- **Multiplayer Skins & Charms**: Your friends can see your custom skin and you can see theirs! Just place each other's files in the mod folder!
- **Players without the mod** see the native skin you have selected in the menu, so pick one you like
  under your custom one.

## Installation

1. Make sure BepInEx 6 (IL2CPP) is installed for *Flipping is Hard* and launch the game once.
2. Download the latest `.zip` from the **Releases** tab.
3. Extract the contents into your BepInEx plugins folder. The structure must match exactly:

    ```text
    Flipping is Hard/BepInEx/plugins/charmreplacer/
    ├── CharmReplacer.dll
    ├── Skin.png (Your own skin)
    ├── Skin_Emission.png (Your own Emission map)
    ├── hatbundle (Your own charm)
    ├── Skins/
    │   └── YourFriendsNames.png
    │   └── YourFriendsNames_emission.png
    └── Charms/
        └── YourFriendsNames.hatbundle
    ```

4. In game, open the customization screen and select **Custom** in the Cover and/or Charm tab.

## Skin Upload

Public upload page: people can now upload their own skin / emission / charm from a
website (no manual file swapping needed). Uploads are matched in-game by Steam ID or name.
→ https://charm-skins-data.onrender.com/

## Charm physics (MagicaCloth 2)

For a charm to **swing like cloth**, the prefab you put in the `hatbundle` **must include a
configured MagicaCloth 2 component**. A mesh-only bundle (just a mesh + material) is shown as a charm
of its own: the game's phone charm is hidden and your mesh hangs from the same attachment point,
swinging as a rigid pendulum (new in v1.5.0 and not tested in game yet; if it stays static, use a
MagicaCloth 2 bundle). See [`Tuto_Templates/`](Tuto_Templates) for presets and steps.

## Charm & Skin Templates

https://github.com/d1ll3x3/SkinChanger_FlippingIsHard/releases/download/v1.1.0/Tuto_Templates.zip

## License

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.
See the [LICENSE](LICENSE) file for details.

---

*Enjoy breaking phones in style.*
