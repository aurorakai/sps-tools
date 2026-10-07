# SPS Tools

Editor tooling for VRChat avatars using VRCFury SPS, bundled as a single VPM package.

## Included tools

- **Bulge Configurator** — generates a depth-driven traveling bulge effect from blendshapes. Menu: `Tools > Kai > SPS > Bulge Configurator`.
- **Normal Map Baker** — bakes per-vertex blendshape delta normals to a texture for use with Poiyomi shaders. Menu: `Tools > Kai > SPS > Normal Map Baker`.

## SPS2 guided paths

SPS2 plugs bend (in the shader) along a socket's guided path, so the tip travels the path as depth increases. For sockets with a guided path:

- **Depth FX Floats** added by SPS Tools use VRCFury's Local units over the path's length, so the value goes from 0 at the entrance to 1 at the end of the path, and stays correct when the avatar is scaled in game. An existing FX Float can be refitted with **Fit to Path** in the SPS Sockets list.
- **The bulge follows the path.** Draw the bulge path (or set up the bone chain) wherever the bulge should show, such as along the belly. Each position peaks when the plug tip reaches the point on the guided path closest to it, so the bulge stays over the tip however the path curves. A plug that doesn't reach the end of the path only moves the bulge as far as its tip gets. This works for FX Floats in Local or Meters units; Depth Range doesn't apply to these sockets. An FX Float in Plugs units keeps the SPS1 behaviour instead: the bulge does its full travel for any plug, spread over the depth range. Positions the tip never reaches on their own, such as ones past the end of the path, are skipped.

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
