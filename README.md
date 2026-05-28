# SkinChanger for Flipping is Hard

BepInEx plugin that replaces phone textures and charm meshes in *Flipping is Hard*.

## Supported game versions
- v0.9.15+
- v0.11.008+
- v0.11.014+

## Installation
1. Copy `charmreplacer/` folder into `BepInEx/plugins/`
2. Place your custom assets:
   - `Skin.png` → your phone texture
   - `Skin_Emission.png` → emission map (optional)
   - `hatbundle` → your charm mesh bundle
   - `Charms/*.hatbundle` → multiplayer charm bundles (named after player nickname)
   - `Skins/*.png` → multiplayer skin textures (named after player nickname)

## Building
```bash
dotnet build -c Release
```
Or use `build.bat` (also copies to game directory).
