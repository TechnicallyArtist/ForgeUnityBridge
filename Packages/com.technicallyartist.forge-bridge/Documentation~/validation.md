# Implementation validation — 2026-10-14

PRO-287 through PRO-291 are implemented locally. PRO-292's publication and
production acceptance remain pending; see [release checks](release-checks.md).

## Automated evidence

- Unity 6000.0.40f1: 126 EditMode tests passed, zero skipped. The host remains
  pinned to this version.
- Unity 6000.4.6f1: 126 EditMode tests passed, zero skipped, in the separate
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

Local Unity XML/logs are in ignored `TestResults/`; CI uploads its XML/logs as
artifacts when a licensed runner is registered. CI has not run remotely yet.

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

This does not replace the owner-run production Chrome smoke on both editors,
git-URL installation, two simultaneous interactive projects, or the visual
textured-character/Play checks in PRO-292. Those checks remain open.

## Release state

The package is 0.1.0 unreleased. No remote, push, tag, release or deployment was
created by this implementation. Follow-ups are filed as PRO-293 (auto-push),
PRO-294 (Humanoid), PRO-295 (Unreal migration) and PRO-296 (public distribution).

Prefab identity uses importer metadata plus ForgeIndex, as explicitly approved
by the owner; [ADR 0001](adr/0001-editor-only-identity.md) records the reason.
