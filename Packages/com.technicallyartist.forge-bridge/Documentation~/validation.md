# Implementation validation — 2026-10-14

PRO-287 through PRO-291 are implemented. The owner approved private repository
creation, publishing and remaining release work on 2026-10-14. PRO-292's release
checks are tracked in [release checks](release-checks.md).

## Automated evidence

- Unity 6000.0.40f1: 133 EditMode tests passed, zero skipped. The host remains
  pinned to this version.
- Unity 6000.4.6f1: 133 EditMode tests passed, zero skipped, in the separate
  `E:/b/fu/probe-6000.4` project.
- Transport tests exercise real loopback sockets, handshake/consent refusal,
  framing and payload limits, concurrent sessions, port reuse, and bounded
  diagnostics that always complete or close a push.
- Import tests cover native layout, refresh GUIDs, moved assets, collisions,
  variants and scene overrides, material reassignments, rollback, dirty user
  assets and retained references. Shader and texture tests cover all three stock
  pipelines, channel packing, normal import settings and vertex-color refusal.
- Real Forge fixtures exercise a 30-joint Generic character, including five hair
  joints, Idle/Walk curves and playback metadata. Local/world pose comparisons
  use a 0.5 mm position tolerance and 0.1 degree rotation tolerance. Controller
  tests cover custom assignments, transitions, states, deletion and static/rigged
  changes.
- Forge: TypeScript and production build passed; 1,700 frontend tests and 1,724
  Rust tests passed. A separate copy built and cooked with `Plugins/Unity/`
  removed. The docs build produced 74 pages with valid internal links.

Local Unity XML/logs are in ignored `TestResults/`. The dedicated Windows runner
uploads XML/logs from both supported editors. The initial
[release CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38616051841)
passed 126 tests on each editor, with no failures or skips.

Fresh projects installed `#0.1.0` from the private Git repository on both editors.
Package Manager reported a Git source and the expected commit; both LFS fixtures
contained real GLB bytes, and the shipped test suite passed. An additional
editor-reopen check exposed a controller persistence issue: Unity's asset batch
had deferred registration of the new controller's state-machine subassets.
The importer now attaches those objects after the batch and before its scoped save.
Six additional regressions verify serialized graph reload, automatic Idle playback
and hips/hair motion, stable GUIDs/local IDs, user graph preservation, dirty-state
rollback, foreign ownership and refusal of missing graph data. The full local
matrix passed 132 tests on each editor; reports are in `TestResults/animator-fixed/`.
The corrected commit's [CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38629303814)
passed on both editors.

Fresh projects then installed `#0.1.0` (commit `5d33816`) from the private Git
repository on both editors. Each ran the shipped suite plus the tagged smoke
harness (133 tests, zero failures or skips): the textured prop and the Idle/Walk/hair
character imported twice with unchanged GUIDs, and Play started Idle with hips and
hair moving. A second editor process reopened the saved scene without importing
again, kept every GUID, and Idle played again with the same motion.

The same checks were repeated for the final tag at `6c1bce5` (the identity
reimport fix below): [CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38637520860)
passed on both editors, and fresh installs passed 134 tests each (133 shipped plus
the smoke harness) with the reopen check and zero import-worker errors.

## Live development smoke

Chrome at `http://localhost:5184` connected to the floor editor's Built-in host.
The owner accepted the Unity Allow dialog. A real Cube → Engine Output graph
pushed `Assets/Forge/Unity Bridge Smoke/Cube.prefab`, with native mesh/material
assets and no source GLB. A repeat push preserved every recorded asset/folder
GUID. The prefab rendered in Unity's Prefab Stage with the stock material.

A real script compilation/domain reload rebound port 7043, retained the approved
origin and automatically reconnected the saved Destination. A third push
refreshed the same prefab successfully. The Unity screenshot is included in the
Forge Engine Output documentation and explicitly labelled as a local smoke.

The Forge app at commit `51a8eba7` is now deployed to the production site. The
production Chrome destination picker listed both fresh Git-install projects at
once, and switching between 6000.0.40f1 and 6000.4.6f1 connected to each. The owner
accepted both editors' public-origin Allow dialogs.

## Production smoke

On 2026-10-14, Chrome at `https://forge.technicallyartist.com` pushed into two open
projects that installed `#0.1.0` by Git URL (6000.0.40f1 and 6000.4.6f1, Built-in).
Both appeared in Destination at once; Window ▸ Forge showed the listening port, the
public-origin session and the Allow dialog.

- The Paint Fill cube created `Assets/Forge/Unity Prop Smoke/` with the prefab, a
  mesh, a Standard material and its base color, metal-rough and emissive PNGs.
- The sample character (Auto Rig with Idle, Walk and Jog) created the prefab,
  avatar, material, four PNGs, three `.anim` clips and a controller whose
  Idle/Walk/Jog graph is saved on disk. Dragged into a scene, it played Idle on
  both editors.
- On 6000.0 a saved scene instance, a Prefab Variant and a move of the folder to
  `Assets/Characters/` survived an upstream change (Target triangles 20,000 →
  15,000) and a push: Forge reported `Assets/Characters/…/SampleCharacter.prefab
  (refreshed)`, all 16 asset and folder GUIDs were unchanged, the mesh was
  rewritten and nothing was duplicated under `Assets/Forge/`.
- On 6000.4 the default pipeline was then switched to URP. Pushing again refreshed
  the cube's material to URP Lit with its three maps.

The smoke found one defect, now fixed in `6c1bce5`: the first push of a prefab
saved its Forge identity in the `.meta` without a reimport, so Unity's preview
worker logged `Import Error Code:(4)` until the prefab was reimported. After the
fix, a first push on 6000.4 rendered the thumbnail with zero import errors.

## Release state

The private [ForgeUnityBridge repository](https://github.com/TechnicallyArtist/ForgeUnityBridge)
now contains the implementation and LFS fixtures. Version 0.1.0 (tag at
`6c1bce5`) is published as a private GitHub release.
The [Engine Output guide](https://docs.forge.technicallyartist.com/nodes/utility/engine-output/)
is deployed with private-install instructions and the labelled local screenshot.
Its source is Forge commit `51a8eba7` plus the guide-only patch with SHA-256
`572747c63658b565a95098a0491c16b666f57c45be4e2338290c82ea6365d1d2`;
unrelated later Forge work was excluded from this deployment.
Follow-ups are filed as PRO-293 (auto-push), PRO-294 (Humanoid), PRO-295 (Unreal
migration) and PRO-296 (public distribution).

Prefab identity uses importer metadata plus ForgeIndex, as explicitly approved
by the owner; [ADR 0001](adr/0001-editor-only-identity.md) records the reason.
