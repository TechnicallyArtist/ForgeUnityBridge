# 0001 — Keep authoring identity out of player builds

Accepted, 2026-10-14.

The epic originally required a ForgeSource component on the generated base prefab,
while the package is strictly editor-only. Unity 6000.0 refused AddComponent with
"Can't add script behaviour 'ForgeSource' because it is an editor script." A
runtime metadata assembly would add a dependency solely for editor refresh.

The owner selected prefab metadata plus the Forge index on 2026-10-14. ForgeSource
is an editor utility that stores projectId/nodeId under the `forgeBridge` key in
the prefab importer's JSON userData. It preserves other JSON keys and refuses to
overwrite non-JSON userData. The persistent ForgeIndex holds native asset
references and remains the ownership authority. Scenes and prefabs require only
stock Unity renderers, Animator, materials and meshes at runtime.

Identity is managed through pushes and the index. External asset processing tools
must preserve .meta files, as they already must to preserve Unity GUIDs. This
deliberately replaces PRO-288's component requirement without adding runtime code.
