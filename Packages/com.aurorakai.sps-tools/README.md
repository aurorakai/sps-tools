# SPS Tools

Editor tooling for VRChat avatars using VRCFury SPS, bundled as a single VPM package.

## Included tools

- **Bulge Configurator** — generates a depth-driven traveling bulge effect from blendshapes. Menu: `Tools > Kai > SPS > Bulge Configurator`.
- **Normal Map Baker** — bakes per-vertex blendshape delta normals to a texture for use with Poiyomi shaders. Menu: `Tools > Kai > SPS > Normal Map Baker`.
- **Debug window** — inspects generated FX layers and parameters on a selected avatar. Menu: `Tools > Kai > SPS > Debug`.

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
