# Changelog

## 0.1.1 — 2026-10-15

- Deny is remembered per origin until the editor restarts: a tab that retries is refused without
  another dialog. **Window > Forge > Denied origins** clears one so it is asked again.
- A Deny now completes the WebSocket upgrade and answers `denied` `origin` instead of HTTP 403,
  so Forge can tell a Deny from an editor that is still importing or reloading scripts, and keeps
  retrying the latter.

## 0.1.0 — 2026-10-14

- Loopback discovery, per-project origin consent and Forge editor window.
- Engine Output pushes produce native meshes, stock-pipeline materials and PNGs.
- Identity-first refresh preserves asset GUIDs, Variants and user material slots.
- Generic characters, hair joints and standalone animation clips.
- Animator Controllers are created once and appended without rewriting user work.
- Controller state machines and states persist as native subassets after editor reload.
- Writing a prefab's Forge identity reimports it, so Unity's preview worker never sees a stale .meta.
- Unity 6000.0/6000.4 EditMode validation and self-hosted CI workflow.
