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
    ├── hatbundle
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

For charms, add `YourFriendsNames.` to the name: `YourFriendsNames.hatbundle`.

---

*Enjoy breaking phones in style.*
