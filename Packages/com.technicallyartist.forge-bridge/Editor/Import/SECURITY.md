# Input and path validation

`ForgeGlb.Validate` runs before glTFast. It checks the complete GLB container, UTF-8 JSON, array
shapes, buffer and accessor ranges, sparse spans and ordered indices, hierarchy cycles, and
referenced resource indices. Primitive indices must address existing vertices. Buffers and
images must be embedded; data URI byte lengths are checked before decoding, then against buffer
declarations. No external resource is fetched.

Allocation limits also apply to expanded data: declared buffers and accessor storage each total
at most 256 MiB. PNG/JPEG headers are checked before image decoding; dimensions are at most 16384
per axis and images total at most 64 megapixels. Oversized or unsupported images receive an
explicit import error. These bounds cover sparse/zero-filled accessors and compressed-image
inputs that are small on the wire but request large allocations.

`ForgeIndex.ValidateAssetPath` validates paths for both new and moved Forge assets. Every path
remains under `Assets`, rejects traversal, reserved/invalid names, and checks every existing
ancestor for symbolic links/junctions, even when the final destination does not yet exist.
Ownership resolution rejects unowned files, folders, orphaned metadata and case-only collisions.
Prefab identity is stored in importer metadata, with the ownership index tracking native assets;
there are no identity components in player builds.

The index and prefab importer metadata must agree before a refresh; an index cannot claim a
foreign prefab. Copied base-prefab identities are refused, while Prefab Variants are excluded
from duplicate-base detection. Writing identity preserves other JSON metadata fields and refuses
opaque user metadata without replacing it. Folder creation checks physical directory existence
inside an asset-editing batch because Unity may defer folder registration until the batch ends.

`ForgeSecurityTests` covers malformed array shapes, integer-overflow ranges, zero-filled accessor
allocation, sparse spans, short data URIs, oversized image dimensions, invalid triangle indices,
and invalid/escaping paths.
`ForgeOwnershipTests` additionally covers metadata coexistence and duplicate identity, repeated
folder creation during batched writes, material reordering, and references in unsaved scenes and
controller states.

[Package overview](../../README.md)
