# Forge Bridge for Unity

Push a mesh or animated character from Forge's **Engine Output** node into the
open Unity project. Select the project in **Destination**, click **Send to
Unity**, and approve the origin in Unity once. **Window > Forge** shows the
listener, sessions, approved origins and recent results.

Requires Unity **6000.0+**. Development validation targets **6000.0.40f1** and
**6000.4.6f1** on Windows. Product C# is cross-platform; other platforms still
need live validation. The package depends on Unity glTFast 6.14.0 for parsing
and Newtonsoft.Json for strict protocol headers.

## Installation

The quickest route is Forge itself: when an Engine Output node finds no Unity
project, **Install into a Unity project…** asks for the project folder and adds
this package to its `Packages/manifest.json`; Unity installs it on its next
refresh. Git must be installed for Unity to fetch it. To install by hand, use
Package Manager > Add package from git URL:

```
https://github.com/TechnicallyArtist/ForgeUnityBridge.git?path=Packages/com.technicallyartist.forge-bridge#0.1.0
```

For local development, Package Manager > Add package from disk: select this
folder's `package.json`.

Open **Project Settings > Forge** to set the Forge folder, listener
port range (default 7042–7049) and payload cap (at most 256 MiB).

## Native assets and refresh

```
Assets/Forge/<Folder>/
  <Name>.prefab
  Meshes/<Name>.asset, <Name>_Avatar.asset
  Materials/<Name>_<Slot>.mat
  Textures/<Name>_<Slot>_<Map>.png
  Animations/<Name>_<Clip>.anim, <Name>.controller
```

Additional meshes retain their own assets and hierarchy. No glb remains in
Assets. A `ForgeIndex.asset` tracks ownership and references; keep it with the
project. The prefab's `.meta` stores its Forge project/node identity in a
namespaced importer metadata entry. No custom component enters player builds.

Refresh finds identity before looking at the requested path. Renamed or moved
assets retain their GUIDs. Forge rewrites its meshes, materials, textures, clips,
avatar and base prefab; Prefab Variants and scene overrides remain Unity's own
overrides. Materials reassigned to existing renderer slots are preserved. A
foreign asset at any new generated path causes a refusal before writing.

Characters use a **Generic avatar**, with Hips as the root and all hair joints
retained. glTFast performs the single coordinate conversion; meters and +Z
forward are preserved. Clips retain their keyframes and Forge playback metadata.
The first clip is the default state in a newly created Animator Controller.
Later pushes only add missing clips as states. Renamed states, transitions,
parameters, layers and blend trees survive. A custom controller assignment is
preserved and never edited. Deleting the generated controller recreates it on
the next rigged push. Removed clips/controllers are deleted only when no saved
project asset, open scene, Prefab Stage or unsaved controller references them;
otherwise the push reports that they were kept.

## Materials

Built-in **Standard**, **Universal Render Pipeline/Lit**, and **HDRP/Lit** use
native materials and PNG textures. Metal/rough maps are repacked into metallic
and smoothness (HDRP also packs occlusion and detail-mask channels). Normal maps
use Unity's Normal Map importer. A pipeline switch updates owned materials and
textures in place on the next push. Unknown SRPs and known material features stock
shaders cannot represent are refused with a reason. Unknown optional glTF extensions
produce a warning and use their core-data fallback; unsupported required extensions
fail the push. See the
[material support rules](Editor/Import/MATERIALS.md).

## Package map

- [Server](Editor/Server/README.md): RFC 6455, loopback listener and lifecycle.
- [Protocol](Editor/Protocol/README.md): framing, sessions and results.
- [Import](Editor/Import/README.md): native assets, identity, rigs and ownership.
- [UI](Editor/UI/README.md): Allow, Window > Forge and settings.
- [Tests](Tests/Editor/README.md): EditMode fixtures and validation.
- [Decisions](Documentation~/adr/README.md).
- [Release checklist](Documentation~/release-checks.md).
- [Implementation validation](Documentation~/validation.md).
- [Canonical protocol](https://github.com/TechnicallyArtist/ProceduralMeshDCC/blob/main/Plugins/PROTOCOL.md)
  lives in Forge's `Plugins/PROTOCOL.md`; this package speaks 1.1.

All product code is editor-only. Generated content uses native Unity components,
stock shaders and standalone assets; it has no Forge runtime assembly dependency.
