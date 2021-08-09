# Deliter <img src="https://raw.githubusercontent.com/H3VR-Modding/Deliter/main/media/icon/128.png" height="92" align="left" />

[![releases](https://img.shields.io/github/v/release/H3VR-Modding/Deliter)](https://github.com/H3VR-Modding/Deliter/releases) [![chat](https://img.shields.io/discord/777351065950879744?label=chat&logo=discord&logoColor=white)](https://discord.com/invite/g8xeFyt42j)

Converts Deli mods to Stratum mods, given that their loaders have been converted. The resulting Mason project is safe to pack and upload without intervention.

## Installation
Deliter has [a Thunderstore package](https://h3vr.thunderstore.io/package/Stratum/Deliter), otherwise it can be downloaded from [the releases section](https://github.com/H3VR-Modding/Deliter/releases).

## Usage
Simply install Deliter and it will auto-convert when applicable. Converted mods are not deleted, but instead have their extension changed to `delite_this`.  
If you are a mod creator, you can (and please do) reupload the package without the `delite_this` file. Be sure to change the Deli dependency in the `manifest.json` file to `Stratum-Stratum-1.0.0` first.

## Contributing
Deliter is always looking to support more loaders! Simply fork and add an entry to `plugins` in [config.yaml](Deliter/config.yaml).  
The entry should be the same name as the Deli GUID. `guid` is the new, BepInEx GUID of the mod. `loaders` is a dictionary of old, Deli loader name to new, Stratum loader name.  
