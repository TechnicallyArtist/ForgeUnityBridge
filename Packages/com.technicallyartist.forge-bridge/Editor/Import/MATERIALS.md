# Native materials

[Import overview](README.md)

`ForgeMaterials.cs` reads the glTF material schema and readable textures from glTFast. It
prepares stock Unity materials and PNG bytes before the push writes assets. Pipeline detection
uses `GraphicsSettings.currentRenderPipeline` for each push, so quality overrides are included.
An unknown pipeline is refused with its type name before any asset is written.
`ForgeGltfMaterialGenerator` supplies temporary stock material identities during glTFast scene
instantiation, so importing does not need glTFast's shader graphs even transiently. The import
owns those temporary materials; the generator separately disposes its fallback material.
Only materials used by instantiated renderers become native assets. A Default material is
created only when an instantiated primitive has no assigned glTF material.

| Pipeline | Shader | Packed `MetallicRoughness` PNG |
|---|---|---|
| Built-in | Standard | R metallic, A smoothness |
| Universal | Universal Render Pipeline/Lit | R metallic, A smoothness |
| High Definition | HDRP/Lit | R metallic, G occlusion, B zero, A smoothness |

glTF metallic comes from B and roughness from G. Scalar factors are baked into the packed map:
metallic = B × metallicFactor; smoothness = 1 − G × roughnessFactor. An absent HDRP occlusion
map becomes white; occlusion strength is applied in the packed mask. Built-in and URP receive a
separate occlusion PNG with glTF's R channel copied to Unity's G channel. Different resolutions
are resampled to the larger mask dimensions. Sampler wrap/filter settings are retained.

Base color and emissive PNGs use sRGB import; packed maps and normal maps use linear import.
Normal PNGs use TextureImporterType.NormalMap. Base color factors are converted from glTF
linear color to the shader's color property; HDR emission factors remain linear, with no added
intensity multiplier. The pipeline's own stock ShaderGUI validates keywords, render passes,
stencil state, transparency, alpha clipping and emission after textures are assigned. Optional
SRP packages are discovered through Unity's TypeCache instead of hard assembly dependencies.

`ForgePreparedMaterial.Save` writes PNGs and copies material data in place inside the push's
single AssetDatabase batch. `Finish` runs after StopAssetEditing, configures TextureImporters
and binds the imported textures. The caller saves assets after finishing. Material paths and
texture paths must already have passed the push transaction's ownership/collision checks.
The `MetallicRoughness` role remains the same across pipeline switches, preserving both material
and packed texture GUIDs. User slot overrides are handled by the prefab translator.

The push transaction snapshots existing files and metas before the batch. If a Forge-owned
file has unsaved edits, it saves that file first so its complete state, including Animator
Controller subassets, is part of the backup. Failure restores those backups and forces asset
reimport, then restores prior dirty flags. This prevents a later SaveAssets from persisting a
failed import's in-memory material or index changes. Unrelated files are not saved by this
snapshot step.

Stock Lit/Standard translation currently accepts the metallic/roughness workflow with UV0 and
baked texture transforms. The known material extensions `KHR_materials_clearcoat`,
`KHR_materials_ior`, `KHR_materials_pbrSpecularGlossiness`, `KHR_materials_sheen`,
`KHR_materials_specular`, `KHR_materials_transmission` and `KHR_materials_unlit` are refused,
as are alternate UV sets and unbaked transforms; bake these materials in Forge first. An unknown
optional extension declared in `extensionsUsed` follows glTF fallback semantics: glTFast warns
that the extension is unsupported, and the push result carries that warning while importing
the core data. An unsupported required extension fails the push. HDRP's combined mask
requires matching metallic/roughness and occlusion samplers. URP and HDRP support double-sided
materials; Built-in Standard has fixed back-face culling and explicitly refuses double-sided
input. Forge's procedural material defaults are single-sided.

Stock shaders do not consume glTF `COLOR_0`. The importer checks glTFast's decoded vertex colors
on the rendered submesh indices before writing and refuses RGB tint, or vertex alpha used by a
masked/blended material, with a bake-to-texture explanation. Missing and all-white colors remain
valid; vertex alpha on an opaque material is harmless and is accepted. This check uses decoded
mesh data, including normalized integer and interleaved accessor layouts.

`ForgeMaterialsTests` checks repack pixels, factors, absent occlusion, pipeline detection,
unsupported-input preflight, native shader keywords, import settings and GUID preservation on
refresh and pipeline changes. Pipeline tests require the corresponding optional SRP package;
otherwise they are reported as skipped, while Built-in and packing tests still run.
`ForgeVertexColorTests` exercises normalized U8/U16 and float RGB/RGBA with interleaved strides,
white/opaque-alpha acceptance, visible-tint refusal without changing existing files, and rendered
indices with a nonzero base vertex.
