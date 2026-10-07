# SPS Tools

Editor tooling for VRChat avatars using VRCFury SPS, bundled as a single VPM package.

## Included tools

- **Bulge Configurator** — generates a depth-driven traveling bulge effect from blendshapes. Menu: `Tools > Kai > SPS > Bulge Configurator`.
- **Normal Map Baker** — bakes per-vertex blendshape delta normals to a texture for use with Poiyomi shaders. Menu: `Tools > Kai > SPS > Normal Map Baker`.

## SPS2 guided paths

SPS2 plugs bend (in the shader) along a socket's guided path, so the tip travels the path as depth increases. For sockets with a guided path:

- **Depth FX Floats** added by SPS Tools use VRCFury's Local units over the path's length, so the value goes from 0 at the entrance to 1 at the end of the path, and stays correct when the avatar is scaled in game. An existing FX Float can be refitted with **Fit to Path** in the SPS Sockets list.
- **Build Path from Socket** (Bulge Configurator, Auto-Generate) turns the guided path into the bulge path by projecting it onto the target mesh's front, back, left, right or nearest surface. The depth range is narrowed to the part of the path that lies on the mesh.

## Requirements

- Unity 2022.3 or newer
- VRChat SDK and VRCFury (detected at runtime via reflection — no hard package dependency)

## Installation

Add the VPM listing in VRChat Creator Companion:

```
https://aurorakai.github.io/vpm/index.json
```

Install "SPS Tools" from your project's package picker.

## License

MIT — see `LICENSE.md`.
