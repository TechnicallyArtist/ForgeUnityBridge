# Changelog

## 0.1.0 — 2026-10-14

- Loopback discovery, per-project origin consent and Forge editor window.
- Engine Output pushes produce native meshes, stock-pipeline materials and PNGs.
- Identity-first refresh preserves asset GUIDs, Variants and user material slots.
- Generic characters, hair joints and standalone animation clips.
- Animator Controllers are created once and appended without rewriting user work.
- Controller state machines and states persist as native subassets after editor reload.
- Writing a prefab's Forge identity reimports it, so Unity's preview worker never sees a stale .meta.
- Unity 6000.0/6000.4 EditMode validation and self-hosted CI workflow.
