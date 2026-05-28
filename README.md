# Charm & Skin Replacer (Multiplayer Edition)

Customize your phone and hat in Flipping is Hard and share it with your friends!

This mod lets you completely change your phone's appearance (normal and emissive textures) and add custom 3D models as accessories (charms/hats) with real physics.

**Now with full multiplayer support!**

## Features

- **Multiplayer Skins & Charms**: Your friends can see your custom skin and you can see theirs! Just place each other's files in the mod folder and everyone gets styled automatically in online matches.
- **Instant Transitions**: Changing your outfit or accessory mid-game applies instantly — no lag, no loading screens.
- **Real Physics**: 3D accessories (like hats) keep their original physics thanks to full MagicaCloth compatibility. They sway with the wind!
- **Perfect Details**: When your phone shatters, the flying debris keeps your custom skin. Your phone also shows your design in the main menu preview.
- **Extreme Performance**: Designed to be invisible to your CPU. No lag or FPS drops, even with 10 friends loading different hats.

## Installation

1. Make sure BepInEx is installed for *Flipping is Hard*.
2. Download the latest `.zip` from the **Releases** tab.
3. Extract the contents into your BepInEx plugins folder. The structure must match exactly:

    ```text
    Flipping is Hard Demo/BepInEx/plugins/charmreplacer/
    ├── CharmReplacer.dll
    ├── System.Drawing.Common.dll
    ├── Microsoft.Win32.SystemEvents.dll
    ├── Skin.png
    ├── Skin_Emission.png
    ├── hatbundle
    ├── Skins/
    │   └── YourFriendsNames.png
    └── Charms/
        └── YourFriendsNames.hatbundle
    ```

## How to see your friends' skins and charms

To see your friends' custom skins and charms, just ask them for their files and place them in the `Skins/` and `Charms/` folders inside the mod folder.

**Make sure the filename matches their in-game name exactly.**

For example, if your friend is called `ElProGamer`, put their texture as `ElProGamer.png` inside the `Skins/` folder!

For emission textures, add `_emission` to the name: `ElProGamer_emission.png`.

---

*Enjoy breaking phones in style.*
