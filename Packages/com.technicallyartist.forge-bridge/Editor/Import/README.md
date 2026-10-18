# Native asset import

[Package overview](../../README.md)

`ForgeImporter` serializes main-thread pushes. It validates the envelope and glb,
detects the active pipeline, prepares all assets in a preview scene, resolves all
output paths and refuses foreign collisions before writing. glTFast loads bytes
in memory; its meshes, hierarchy, skinning and transform curves supply the native
assets without a second axis conversion.

- `ForgeGlb`: [self-contained GLB checks, bounds and path safety](SECURITY.md).
- `ForgeGltfMaterialGenerator` (in `ForgeMaterials.cs`): transient stock materials identify glTF slots
  without requiring glTFast runtime shaders.
- `ForgeMaterials`: [stock pipeline translation and map packing](MATERIALS.md).
- `ForgeIndex`: identity-first lookup, GUID-backed ownership and safe paths.
- `ForgeSource`: prefab authoring identity in namespaced importer metadata.
- `ForgeAssetWriter`: in-place serialized updates and disk restoration on failure.
- `ForgeAnimator`: create once and append missing clip states; custom assignments
  bypass controller edits entirely. After the batch, it explicitly attaches new graph
  objects to the owned controller before the scoped save: Unity cannot resolve a new
  controller's path during StartAssetEditing, so its normal AddLayer/AddState attachment
  is deferred. Existing graph objects and local IDs remain unchanged. Missing graph data
  is refused with a restore/recreate explanation instead of replacing user work.

One StartAssetEditing/StopAssetEditing block writes a push's native assets, with
Stop in a finally block. Texture imports and reference binding complete afterward
because Unity defers PNG import while asset editing is paused. The ownership index
is saved only with the complete prefab. Failed writes restore original files/metas
and reimport them. Removed Forge-owned meshes, materials, textures, animations and static-output controllers are pruned only after saved
asset dependencies, open scenes, Prefab Stage and unsaved controllers have been checked.

Material overrides retain their prior numeric slot for reading the saved prefab,
and match the refreshed renderer by transform path, source material name and
occurrence. Reordering named material slots therefore preserves their assignments.
The generated base hierarchy uses deterministic unique names so Unity can match
prefab objects on refresh. Duplicate base-prefab identities are refused; use a
Variant for custom copies. Losing the ownership index causes an explicit refusal
instead of guessing which assets Forge may overwrite.

Textures are encoded, written and imported once per source-image recipe, including colour space,
normal-map type and sampler settings. Packed maps include their input images, channels and baked
factors in the key. Materials share the resulting texture asset. Shared textures use the asset
name and role; single-material textures keep the slot name. Repeat pushes resolve keys through
the ownership index to preserve GUIDs, including textures moved by the user.

Cleanup runs after the prefab saves, materials before their unused textures. Referenced assets
are retained with a warning; failed deletion never fails a completed push. Only folders created
and recorded by Forge are removed when empty. User material assignments remain user-owned.


`ForgeCollision.cs` interprets protocol 1.2 collision extras before render/material enumeration, validates hulls, assigns saved collision meshes, and preserves colliders outside the owned Collision child.

`ForgeBackup.cs` reads the 1.4 project backup (the `.ta` archive in an unreferenced bufferView named by
the push's `backup` header) and refuses a descriptor that does not match the glb before any write. After
the push commits, it writes `Backup~/<Name>.ta` beside the prefab, leaving an identical file untouched.
`ForgeImportCompleted.cs` is what `ForgeImporter.ImportCompleted` reports after every successful push.
