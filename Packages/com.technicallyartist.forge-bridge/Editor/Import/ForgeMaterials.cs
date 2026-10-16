using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GLTFast;
using GLTFast.Schema;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Material = UnityEngine.Material;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Import
{
    public enum ForgeRenderPipeline { BuiltIn, Universal, HighDefinition }

    /// <summary>Transient slot identities for glTFast; final material translation happens before saving.</summary>
    public sealed class ForgeGltfMaterialGenerator : GLTFast.Materials.IMaterialGenerator, IDisposable
    {
        private readonly Shader shader;
        private Material defaultMaterial;

        public ForgeGltfMaterialGenerator(ForgeRenderPipeline pipeline)
        {
            shader = Shader.Find(ForgeMaterials.ShaderName(pipeline));
            if (shader == null) throw new NotSupportedException("The active pipeline's stock material shader is unavailable.");
        }

        public Material GenerateMaterial(MaterialBase source, IGltfReadable gltf, bool pointsSupport = false)
        {
            if (pointsSupport) throw new NotSupportedException("Stock Lit/Standard translation does not support point primitives.");
            // glTFast owns these instances and destroys them when its import is disposed.
            return new Material(shader) { name = source.name ?? "Material" };
        }

        public Material GetDefaultMaterial(bool pointsSupport = false)
        {
            if (pointsSupport) throw new NotSupportedException("Stock Lit/Standard translation does not support point primitives.");
            if (defaultMaterial == null) defaultMaterial = new Material(shader) { name = "Default" };
            return defaultMaterial;
        }

        public void SetLogger(GLTFast.Logging.ICodeLogger logger) { }

        public void Dispose()
        {
            // glTFast's material array does not own the fallback returned by GetDefaultMaterial.
            if (defaultMaterial != null) Object.DestroyImmediate(defaultMaterial);
            defaultMaterial = null;
        }
    }

    /// <summary>Translates glTF's PBR data into the active pipeline's stock shader.</summary>
    public static class ForgeMaterials
    {
        public static ForgeRenderPipeline DetectPipeline()
        {
            return DetectPipeline(GraphicsSettings.currentRenderPipeline?.GetType().FullName);
        }

        public static ForgeRenderPipeline DetectPipeline(string pipelineTypeName)
        {
            switch (pipelineTypeName)
            {
                case null:
                case "": return ForgeRenderPipeline.BuiltIn;
                case "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset":
                    return ForgeRenderPipeline.Universal;
                case "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset":
                    return ForgeRenderPipeline.HighDefinition;
                default: throw new NotSupportedException($"Render pipeline '{pipelineTypeName}' materials are not supported.");
            }
        }

        public static ForgePreparedMaterial Prepare(GltfImport gltf, int materialIndex, ForgeRenderPipeline pipeline, ForgeTextureCache cache = null)
        {
            if (gltf == null) throw new ArgumentNullException(nameof(gltf));
            var source = materialIndex < 0 ? new GLTFast.Schema.Material() : gltf.GetSourceMaterial(materialIndex);
            if (source == null) throw new InvalidDataException($"Material {materialIndex} is missing from the glb.");
            return Prepare(source, gltf.GetTexture, pipeline, cache, i => gltf.GetSourceTexture(i).GetImageIndex());
        }

        /// <summary>Preparation validates and encodes everything before the push writes any asset.</summary>
        public static ForgePreparedMaterial Prepare(MaterialBase source, Func<int, Texture2D> texture, ForgeRenderPipeline pipeline,
            ForgeTextureCache cache = null, Func<int, int> image = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            ValidateSource(source, pipeline);
            var shaderName = ShaderName(pipeline);
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new NotSupportedException($"The active render pipeline's stock shader '{shaderName}' is unavailable.");
            var validator = FindValidator(pipeline);
            var material = new Material(shader) { name = source.name ?? "Material" };
            try
            {
                var pbr = source.PbrMetallicRoughness;
                var metallic = pbr?.metallicFactor ?? 1f;
                var roughness = pbr?.roughnessFactor ?? 1f;
                cache = cache ?? new ForgeTextureCache();
                string Input(TextureInfoBase info) => info == null || info.index < 0 ? "none" : (image?.Invoke(info.index) ?? info.index).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string Factor(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                var prepared = new ForgePreparedMaterial(material, validator, cache);
                SetColor(material, pipeline == ForgeRenderPipeline.BuiltIn ? "_Color" : "_BaseColor", (pbr?.BaseColor ?? Color.white).gamma);
                SetFloat(material, "_Metallic", metallic);
                SetFloat(material, "_Glossiness", 1 - roughness);
                SetFloat(material, "_Smoothness", 1 - roughness);
                SetFloat(material, "_WorkflowMode", 1); // Metallic workflow.
                SetFloat(material, "_SmoothnessTextureChannel", 0);
                SetFloat(material, "_NormalScale", source.NormalTexture?.scale ?? 1);
                SetFloat(material, "_BumpScale", source.NormalTexture?.scale ?? 1);
                SetFloat(material, "_OcclusionStrength", source.OcclusionTexture?.strength ?? 1);

                AddTexture(prepared, texture, Input, pbr?.BaseColorTexture, "BaseColor", pipeline == ForgeRenderPipeline.HighDefinition ? "_BaseColorMap" : pipeline == ForgeRenderPipeline.Universal ? "_BaseMap" : "_MainTex", true);
                AddTexture(prepared, texture, Input, source.NormalTexture, "Normal", pipeline == ForgeRenderPipeline.HighDefinition ? "_NormalMap" : "_BumpMap", false, true);
                AddTexture(prepared, texture, Input, source.EmissiveTexture, "Emissive", pipeline == ForgeRenderPipeline.HighDefinition ? "_EmissiveColorMap" : "_EmissionMap", true);

                var mr = ReadTexture(texture, pbr?.MetallicRoughnessTexture);
                var occlusion = ReadTexture(texture, source.OcclusionTexture);
                if (mr != null || (pipeline == ForgeRenderPipeline.HighDefinition && occlusion != null))
                {
                    if (pipeline == ForgeRenderPipeline.HighDefinition && mr != null && occlusion != null &&
                        (mr.wrapModeU != occlusion.wrapModeU || mr.wrapModeV != occlusion.wrapModeV || mr.filterMode != occlusion.filterMode))
                        throw new NotSupportedException("HDRP requires metallic/roughness and occlusion maps with matching samplers. Bake the maps in Forge first.");
                    var width = Math.Max(mr?.width ?? 1, pipeline == ForgeRenderPipeline.HighDefinition ? occlusion?.width ?? 1 : 1);
                    var height = Math.Max(mr?.height ?? 1, pipeline == ForgeRenderPipeline.HighDefinition ? occlusion?.height ?? 1 : 1);
                    string packedKey = "mr-BG/" + pipeline + "/" + Input(pbr?.MetallicRoughnessTexture) + "/" + Factor(metallic) + "/" + Factor(roughness);
                    if (pipeline == ForgeRenderPipeline.HighDefinition) packedKey += "/ao-R/" + Input(source.OcclusionTexture) + "/" + Factor(source.OcclusionTexture?.strength ?? 1);
                    prepared.Add("MetallicRoughness", pipeline == ForgeRenderPipeline.HighDefinition ? "_MaskMap" : "_MetallicGlossMap",
                        () => PackMetallicRoughness(Resample(mr, width, height),
                            pipeline == ForgeRenderPipeline.HighDefinition ? Resample(occlusion, width, height) : null,
                            metallic, roughness, source.OcclusionTexture?.strength ?? 1, pipeline),
                        width, height, mr ?? occlusion, false, false, packedKey);
                    // Factors are baked into the packed pixels, including 1 - (roughness * factor).
                    SetFloat(material, "_Metallic", 1);
                    SetFloat(material, "_Smoothness", 1);
                    SetFloat(material, "_GlossMapScale", 1);
                    SetFloat(material, "_MetallicRemapMin", 0);
                    SetFloat(material, "_MetallicRemapMax", 1);
                    SetFloat(material, "_SmoothnessRemapMin", 0);
                    SetFloat(material, "_SmoothnessRemapMax", 1);
                    SetFloat(material, "_AORemapMin", 0);
                    SetFloat(material, "_AORemapMax", 1);
                }
                if (occlusion != null && pipeline != ForgeRenderPipeline.HighDefinition)
                {
                    prepared.Add("Occlusion", "_OcclusionMap", () => {
                        var pixels = occlusion.GetPixels32();
                        for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(pixels[i].r, pixels[i].r, pixels[i].r, 255);
                        return pixels;
                    }, occlusion.width, occlusion.height, occlusion, false, false, "ao-R/" + Input(source.OcclusionTexture));
                }

                // HDR shader color properties are linear. Do not gamma-convert the emission factor.
                var emissive = source.Emissive;
                var hasEmission = emissive.maxColorComponent > 0;
                SetColor(material, pipeline == ForgeRenderPipeline.HighDefinition ? "_EmissiveColor" : "_EmissionColor", emissive);
                SetFloat(material, "_UseEmissiveIntensity", 0);
                SetFloat(material, "_EmissiveIntensity", 1);
                SetFloat(material, "_AlbedoAffectEmissive", 0);
                SetFloat(material, "_EmissiveExposureWeight", 1);
                material.globalIlluminationFlags = hasEmission ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                ConfigureSurface(material, source, pipeline);
                validator.ValidateMaterial(material);
                return prepared;
            }
            catch
            {
                Object.DestroyImmediate(material);
                throw;
            }
        }

        public static string ShaderName(ForgeRenderPipeline pipeline)
        {
            switch (pipeline)
            {
                case ForgeRenderPipeline.BuiltIn: return "Standard";
                case ForgeRenderPipeline.Universal: return "Universal Render Pipeline/Lit";
                case ForgeRenderPipeline.HighDefinition: return "HDRP/Lit";
                default: throw new ArgumentOutOfRangeException(nameof(pipeline));
            }
        }

        /// <summary>Stock Lit/Standard ignore COLOR_0; refuse visible tint before any asset writes.</summary>
        public static void ValidateVertexColors(UnityEngine.Mesh mesh, IReadOnlyList<bool> alphaBySubmesh)
        {
            if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.Color)) return;
            // Read the decoded mesh, so normalized integer, float, interleaved and base-vertex
            // layouts follow glTFast/Unity's accessor interpretation rather than a second decoder.
            var colors = new List<Color>();
            mesh.GetColors(colors);
            var indices = new List<int>();
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                mesh.GetIndices(indices, submesh, true);
                bool usesAlpha = submesh < alphaBySubmesh.Count && alphaBySubmesh[submesh];
                foreach (int vertex in indices)
                {
                    Color color = colors[vertex];
                    if (!Mathf.Approximately(color.r, 1) || !Mathf.Approximately(color.g, 1) ||
                        !Mathf.Approximately(color.b, 1) || usesAlpha && !Mathf.Approximately(color.a, 1))
                        throw new NotSupportedException($"Mesh '{mesh.name}' uses COLOR_0 vertex colors that stock Lit/Standard shaders cannot display. Bake the vertex colors into the base-color texture in Forge first.");
                }
            }
        }

        /// <summary>glTF G roughness/B metallic to Unity R metallic/A smoothness; HDRP also uses G AO.</summary>
        public static Color32[] PackMetallicRoughness(Color32[] metallicRoughness, Color32[] occlusion,
            float metallicFactor, float roughnessFactor, float occlusionStrength, ForgeRenderPipeline pipeline)
        {
            if (metallicRoughness == null && occlusion == null) throw new ArgumentException("At least one texture is required.");
            var count = metallicRoughness?.Length ?? occlusion.Length;
            if (occlusion != null && occlusion.Length != count) throw new ArgumentException("Repacked textures must have the same dimensions.");
            var result = new Color32[count];
            for (var i = 0; i < count; i++)
            {
                var mr = metallicRoughness == null ? new Color32(255, 255, 255, 255) : metallicRoughness[i];
                var ao = occlusion == null ? 1f : Mathf.Lerp(1, occlusion[i].r / 255f, occlusionStrength);
                result[i] = new Color32(Byte(mr.b / 255f * metallicFactor),
                    pipeline == ForgeRenderPipeline.HighDefinition ? Byte(ao) : (byte)0, 0,
                    Byte(1 - mr.g / 255f * roughnessFactor));
            }
            return result;
        }

        private static byte Byte(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255);

        private static ShaderGUI FindValidator(ForgeRenderPipeline pipeline)
        {
            // Optional SRPs are deliberately not assembly dependencies. Their stock shader GUIs
            // expose Unity's common ValidateMaterial API, including HDRP stencil/pass setup.
            var name = pipeline == ForgeRenderPipeline.HighDefinition ? "UnityEditor.Rendering.HighDefinition.LitGUI" :
                pipeline == ForgeRenderPipeline.Universal ? "UnityEditor.Rendering.Universal.ShaderGUI.LitShader" : "UnityEditor.StandardShaderGUI";
            var type = TypeCache.GetTypesDerivedFrom<ShaderGUI>().FirstOrDefault(candidate => candidate.FullName == name);
            if (type == null) throw new NotSupportedException($"The stock material validator '{name}' is unavailable.");
            return (ShaderGUI)Activator.CreateInstance(type, true);
        }

        private static void ConfigureSurface(Material material, MaterialBase source, ForgeRenderPipeline pipeline)
        {
            var alpha = source.GetAlphaMode();
            var blend = alpha == MaterialBase.AlphaMode.Blend;
            var mask = alpha == MaterialBase.AlphaMode.Mask;
            material.doubleSidedGI = source.doubleSided;
            SetFloat(material, "_Cull", source.doubleSided ? 0 : 2);
            SetFloat(material, "_DoubleSidedEnable", source.doubleSided ? 1 : 0);
            SetFloat(material, "_DoubleSidedNormalMode", 0);
            SetFloat(material, "_Cutoff", source.alphaCutoff);
            SetFloat(material, "_AlphaCutoff", source.alphaCutoff);
            SetFloat(material, "_AlphaCutoffEnable", mask ? 1 : 0);
            SetFloat(material, "_AlphaClip", mask ? 1 : 0);
            SetFloat(material, "_Surface", blend ? 1 : 0);
            SetFloat(material, "_SurfaceType", blend ? 1 : 0);
            SetFloat(material, "_Blend", 0);
            SetFloat(material, "_BlendMode", 0);
            SetFloat(material, "_Mode", blend ? 2 : mask ? 1 : 0);
            SetFloat(material, "_EnableBlendModePreserveSpecularLighting", 0);
            SetFloat(material, "_PreserveSpecular", 0);
            material.renderQueue = blend ? (int)RenderQueue.Transparent : mask ? (int)RenderQueue.AlphaTest : -1;
        }

        private static void ValidateSource(MaterialBase source, ForgeRenderPipeline pipeline)
        {
            if (pipeline == ForgeRenderPipeline.BuiltIn && source.doubleSided)
                throw new NotSupportedException("Built-in Standard cannot render double-sided materials. Make this material single-sided in Forge or use URP/HDRP.");
            var extension = source.Extensions;
            if (extension != null && (extension.KHR_materials_clearcoat != null || extension.KHR_materials_ior != null ||
                extension.KHR_materials_pbrSpecularGlossiness != null || extension.KHR_materials_sheen != null ||
                extension.KHR_materials_specular != null || extension.KHR_materials_transmission != null || extension.KHR_materials_unlit != null))
                throw new NotSupportedException($"Material '{source.name}' uses a glTF material extension that stock Lit/Standard translation does not support. Bake it to metallic/roughness in Forge first.");
            ValidateTextureInfo(source.PbrMetallicRoughness?.BaseColorTexture);
            ValidateTextureInfo(source.PbrMetallicRoughness?.MetallicRoughnessTexture);
            ValidateTextureInfo(source.NormalTexture);
            ValidateTextureInfo(source.OcclusionTexture);
            ValidateTextureInfo(source.EmissiveTexture);
        }

        private static void ValidateTextureInfo(TextureInfoBase info)
        {
            if (info == null || info.index < 0) return;
            var transform = info.Extensions?.KHR_texture_transform;
            var texCoord = transform != null && transform.texCoord >= 0 ? transform.texCoord : info.texCoord;
            if (texCoord != 0) throw new NotSupportedException("Stock Lit/Standard translation requires texture UV0. Bake this material in Forge first.");
            if (transform != null && (transform.rotation != 0 ||
                (transform.offset != null && (transform.offset[0] != 0 || transform.offset[1] != 0)) ||
                (transform.scale != null && (transform.scale[0] != 1 || transform.scale[1] != 1))))
                throw new NotSupportedException("Stock Lit/Standard translation requires baked texture transforms. Bake this material in Forge first.");
        }

        private static Texture2D ReadTexture(Func<int, Texture2D> texture, TextureInfoBase info)
        {
            if (info == null || info.index < 0) return null;
            var result = texture(info.index);
            if (result == null) throw new InvalidDataException($"Texture {info.index} could not be decoded.");
            if (!result.isReadable) throw new InvalidOperationException("glTF textures must be loaded with ImportSettings.TexturesReadable enabled.");
            return result;
        }

        private static void AddTexture(ForgePreparedMaterial prepared, Func<int, Texture2D> texture, Func<TextureInfoBase, string> input,
            TextureInfoBase info, string role, string property, bool srgb, bool normal = false)
        {
            var source = ReadTexture(texture, info);
            if (source != null) prepared.Add(role, property, () => source.GetPixels32(), source.width, source.height, source, srgb, normal, "image/" + input(info));
        }

        private static Color32[] Resample(Texture2D texture, int width, int height)
        {
            if (texture == null) return null;
            if (texture.width == width && texture.height == height) return texture.GetPixels32();
            var result = new Color32[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    result[y * width + x] = texture.filterMode == FilterMode.Point
                        ? texture.GetPixel(Math.Min((int)((x + .5f) * texture.width / width), texture.width - 1), Math.Min((int)((y + .5f) * texture.height / height), texture.height - 1))
                        : texture.GetPixelBilinear((x + .5f) / width, (y + .5f) / height);
            return result;
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }
    }

    /// <summary>Per-push encoded buffers and completed writes, keyed by source images and import settings.</summary>
    public sealed class ForgeTextureCache
    {
        internal readonly Dictionary<string, byte[]> Encoded = new Dictionary<string, byte[]>();
        internal readonly HashSet<string> Written = new HashSet<string>();
        internal readonly HashSet<string> Imported = new HashSet<string>();
    }

    /// <summary>Prepared bytes and material; Save runs in the push batch, Finish after StopAssetEditing.</summary>
    public sealed class ForgePreparedMaterial : IDisposable
    {
        private sealed class TextureWrite
        {
            public string Role, Property, Path, Key;
            public byte[] Png;
            public bool Srgb, Normal;
            public int Size;
            public TextureWrapMode WrapU, WrapV;
            public FilterMode Filter;
        }

        private readonly List<TextureWrite> textures = new List<TextureWrite>();
        private readonly ShaderGUI validator;
        private readonly ForgeTextureCache cache;
        private Material material;
        private bool saved;

        internal ForgePreparedMaterial(Material material, ShaderGUI validator, ForgeTextureCache cache)
        {
            this.material = material;
            this.validator = validator;
            this.cache = cache;
        }

        public IReadOnlyList<string> TextureRoles => textures.Select(texture => texture.Role).ToArray();

        public string TextureKey(string role) => textures.Single(t => t.Role == role).Key;

        internal void Add(string role, string property, Func<Color32[]> pixels, int width, int height,
            Texture2D sampler, bool srgb, bool normal, string sourceKey)
        {
            string key = sourceKey + "/" + srgb + "/" + normal + "/" + sampler.wrapModeU + "/" + sampler.wrapModeV + "/" + sampler.filterMode;
            if (!cache.Encoded.TryGetValue(key, out var png))
            {
                var encoded = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                try
                {
                    encoded.SetPixels32(pixels());
                    encoded.Apply(false, false);
                    png = encoded.EncodeToPNG();
                    cache.Encoded.Add(key, png);
                }
                finally { Object.DestroyImmediate(encoded); }
            }
            textures.Add(new TextureWrite {
                Role = role, Property = property, Key = key, Png = png, Srgb = srgb, Normal = normal,
                Size = Math.Max(32, Mathf.NextPowerOfTwo(Math.Max(width, height))), WrapU = sampler.wrapModeU,
                WrapV = sampler.wrapModeV, Filter = sampler.filterMode
            });
        }

        public Material Save(string materialPath, Func<string, string> texturePath)
        {
            if (material == null) throw new ObjectDisposedException(nameof(ForgePreparedMaterial));
            if (saved) throw new InvalidOperationException("A prepared material can only be saved once.");
            // All paths/ownership must be resolved by the push transaction before this method.
            foreach (var texture in textures) texture.Path = texturePath(texture.Role);
            ForgeIndex.EnsureFolder(Path.GetDirectoryName(materialPath).Replace('\\', '/'));
            foreach (var texture in textures) ForgeIndex.EnsureFolder(Path.GetDirectoryName(texture.Path).Replace('\\', '/'));
            foreach (var texture in textures)
                if (cache.Written.Add(texture.Path)) File.WriteAllBytes(texture.Path, texture.Png);
            material.name = Path.GetFileNameWithoutExtension(materialPath);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (existing == null)
            {
                if (File.Exists(materialPath)) throw new IOException($"A non-material asset occupies '{materialPath}'.");
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                EditorUtility.CopySerialized(material, existing);
                Object.DestroyImmediate(material);
                material = existing;
                EditorUtility.SetDirty(material);
            }
            saved = true;
            return material;
        }

        public void Finish()
        {
            if (!saved || material == null) throw new InvalidOperationException("Save the material before finishing its texture imports.");
            foreach (var texture in textures)
            {
                if (cache.Imported.Add(texture.Path))
                {
                    AssetDatabase.ImportAsset(texture.Path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    var importer = AssetImporter.GetAtPath(texture.Path) as TextureImporter;
                    if (importer == null) throw new IOException($"Texture importer is unavailable for '{texture.Path}'.");
                    importer.textureType = texture.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    importer.sRGBTexture = texture.Srgb;
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                    importer.alphaIsTransparency = false;
                    importer.convertToNormalmap = false;
                    importer.flipGreenChannel = false;
                    importer.npotScale = TextureImporterNPOTScale.None;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.maxTextureSize = texture.Size;
                    importer.wrapModeU = texture.WrapU;
                    importer.wrapModeV = texture.WrapV;
                    importer.filterMode = texture.Filter;
                    importer.mipmapEnabled = true;
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                }
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(texture.Path);
                if (asset == null) throw new IOException($"Texture import failed for '{texture.Path}'.");
                material.SetTexture(texture.Property, asset);
            }
            validator.ValidateMaterial(material);
            EditorUtility.SetDirty(material);
        }

        public void Dispose()
        {
            if (material != null && !EditorUtility.IsPersistent(material)) Object.DestroyImmediate(material);
            material = null;
            textures.Clear();
        }
    }
}
