# Implementation validation — 2026-10-14

PRO-287 through PRO-291 are implemented. The owner approved private repository
creation, publishing and remaining release work on 2026-10-14. PRO-292's release
checks are tracked in [release checks](release-checks.md).

## Automated evidence

- Unity 6000.0.40f1: 132 EditMode tests passed, zero skipped. The host remains
  pinned to this version.
- Unity 6000.4.6f1: 132 EditMode tests passed, zero skipped, in the separate
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
rollback, foreign ownership and refusal of missing graph data. The release remains
a draft while corrected release CI and tagged-install smoke finish. The full local
matrix passed 132 tests on each editor; reports are in `TestResults/animator-fixed/`.

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
accepted both editors' public-origin Allow dialogs. Asset pushes and visual
textured-character/Play checks remain in progress under PRO-292.

## Release state

The private [ForgeUnityBridge repository](https://github.com/TechnicallyArtist/ForgeUnityBridge)
now contains the implementation and LFS fixtures. Version 0.1.0 remains a draft
until its corrected controller persistence and final smoke checks are verified.
The [Engine Output guide](https://docs.forge.technicallyartist.com/nodes/utility/engine-output/)
is deployed with private-install instructions and the labelled local screenshot.
Its source is Forge commit `51a8eba7` plus the guide-only patch with SHA-256
`572747c63658b565a95098a0491c16b666f57c45be4e2338290c82ea6365d1d2`;
unrelated later Forge work was excluded from this deployment.
Follow-ups are filed as PRO-293 (auto-push), PRO-294 (Humanoid), PRO-295 (Unreal
migration) and PRO-296 (public distribution).

Prefab identity uses importer metadata plus ForgeIndex, as explicitly approved
by the owner; [ADR 0001](adr/0001-editor-only-identity.md) records the reason.
