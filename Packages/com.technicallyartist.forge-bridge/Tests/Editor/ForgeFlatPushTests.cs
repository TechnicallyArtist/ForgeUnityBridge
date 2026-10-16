using System;
using System.Collections;
using System.IO;
using System.Linq;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeFlatPushTests
    {
        private const string Root = "Assets/ForgeFlatPushTests";
        private string previousFolder;
        [SetUp] public void SetUp()
        {
            previousFolder = BridgeSettings.instance.ForgeFolder;
            BridgeSettings.instance.ForgeFolder = Root;
            AssetDatabase.DeleteAsset(Root);
        }
        [TearDown] public void TearDown()
        {
            BridgeSettings.instance.ForgeFolder = previousFolder;
            AssetDatabase.DeleteAsset(Root);
        }

        [UnityTest] public IEnumerator SharedImages_WriteThreeTexturesAndKeepGuids()
        {
            byte[] bytes = Parts(32);
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            Assert.That(Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories).Length, Is.EqualTo(3));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(first.Result.AssetPath);
            var materials = prefab.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).ToArray();
            Assert.That(materials.Length, Is.EqualTo(32));
            foreach (string property in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap" })
                Assert.That(materials.Select(m => m.GetTexture(property)).Distinct().Count(), Is.EqualTo(1));
            var packed = materials[0].GetTexture("_MetallicGlossMap") as Texture2D;
            string packedPath = AssetDatabase.GetAssetPath(packed);
            var importer = (TextureImporter)AssetImporter.GetAtPath(packedPath);
            importer.isReadable = true; importer.SaveAndReimport();
            Color32 pixel = AssetDatabase.LoadAssetAtPath<Texture2D>(packedPath).GetPixels32()[0];
            Assert.That(pixel.r, Is.EqualTo(192).Within(1));
            Assert.That(pixel.a, Is.EqualTo(191).Within(1));
            var paths = Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).ToArray();
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var repeat = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(repeat);
            Assert.That(repeat.Result.Error, Is.Null);
            Assert.That(paths.Select(AssetDatabase.AssetPathToGUID), Is.EqualTo(guids));
        }

        [UnityTest] public IEnumerator DifferentPackedInputs_ShareOnlyBaseColorAndNormal()
        {
            byte[] bytes = Parts(2, distinctPacked: true);
            var push = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Is.Null);
            Assert.That(Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories).Length, Is.EqualTo(4));
            var materials = AssetDatabase.LoadAssetAtPath<GameObject>(push.Result.AssetPath).GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).ToArray();
            Assert.That(materials[0].GetTexture("_MainTex"), Is.EqualTo(materials[1].GetTexture("_MainTex")));
            Assert.That(materials[0].GetTexture("_MetallicGlossMap"), Is.Not.EqualTo(materials[1].GetTexture("_MetallicGlossMap")));
        }

        [UnityTest] public IEnumerator ShrinkingPush_PrunesOwnedAssetsAndKeepsPrefabGuidAndUserMaterial()
        {
            byte[] bytes = Parts(3, distinctPacked: true);
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            string guid = AssetDatabase.AssetPathToGUID(first.Result.AssetPath);
            var custom = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(custom, Root + "/User.mat");
            var edit = PrefabUtility.LoadPrefabContents(first.Result.AssetPath);
            edit.GetComponentsInChildren<Renderer>()[0].sharedMaterial = custom;
            PrefabUtility.SaveAsPrefabAsset(edit, first.Result.AssetPath);
            PrefabUtility.UnloadPrefabContents(edit);
            bytes = Parts(1, distinctPacked: true);
            var refresh = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(refresh.Result.AssetPath), Is.EqualTo(guid));
            Assert.That(Directory.GetFiles(Root + "/Test/Meshes", "*.asset").Length, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(Root + "/Test/Materials", "*.mat").Length, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories).Length, Is.EqualTo(3));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(refresh.Result.AssetPath).GetComponentInChildren<Renderer>().sharedMaterial, Is.EqualTo(custom));
        }

        [UnityTest] public IEnumerator ShrinkingPush_KeepsMaterialReferencedByUnsavedSceneObject()
        {
            byte[] bytes = Parts(3, distinctPacked: true);
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(first.Result.AssetPath);
            var old = prefab.GetComponentsInChildren<Renderer>()[2].sharedMaterial;
            string path = AssetDatabase.GetAssetPath(old);
            var user = new GameObject("User reference");
            user.AddComponent<MeshRenderer>().sharedMaterial = old;
            try
            {
                bytes = Parts(1, distinctPacked: true);
                var refresh = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
                yield return ForgeImportTests.Await(refresh);
                Assert.That(refresh.Result.Error, Is.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path), Is.Not.Null);
                Assert.That(refresh.Result.Warnings.Any(w => w.Contains(path) && w.Contains("another asset references it")), Is.True);
            }
            finally { Object.DestroyImmediate(user); }
        }

        [UnityTest] public IEnumerator LegacyTextureKeys_MigrateWithoutChangingGuids()
        {
            byte[] bytes = Parts(1);
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var entry = ForgeIndex.Find("test-project", "test-node", out var index);
            var textures = entry.Assets.Where(a => a.Key.StartsWith("texture/", StringComparison.Ordinal)).ToArray();
            var guids = textures.Select(t => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t.Asset))).ToArray();
            for (int i = 0; i < textures.Length; i++) textures[i].Key = "texture/legacy/" + i;
            EditorUtility.SetDirty(index); AssetDatabase.SaveAssets();
            var repeat = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(repeat);
            Assert.That(repeat.Result.Error, Is.Null);
            Assert.That(textures.Select(t => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t.Asset))), Is.EqualTo(guids));
            Assert.That(entry.Assets.Any(a => a.Key.StartsWith("texture/legacy/", StringComparison.Ordinal)), Is.False);
        }

        [UnityTest] public IEnumerator EmptyTextureFolder_IsRemovedOnlyWhenForgeCreatedIt()
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                AssetDatabase.DeleteAsset(Root);
                string folder = Root + "/Test/Textures";
                if (mode == 1) ForgeIndex.EnsureFolder(folder);
                byte[] bytes = Parts(1);
                var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
                yield return ForgeImportTests.Await(first);
                Assert.That(first.Result.Error, Is.Null);
                if (mode == 2)
                {
                    string originalGuid = AssetDatabase.AssetPathToGUID(folder);
                    string replacement = Root + "/UserTextures";
                    ForgeIndex.EnsureFolder(replacement);
                    Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                    Assert.That(AssetDatabase.MoveAsset(replacement, folder), Is.Empty);
                    Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.Not.EqualTo(originalGuid));
                }
                bytes = ForgeImportTests.Triangle();
                var refresh = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
                yield return ForgeImportTests.Await(refresh);
                Assert.That(refresh.Result.Error, Is.Null);
                Assert.That(AssetDatabase.IsValidFolder(folder), Is.EqualTo(mode != 0), "folder mode " + mode);
            }
        }

        internal static byte[] Parts(int count, bool distinctPacked = false)
        {
            var doc = ForgeImportTests.TriangleJson();
            var nodes = new JArray(); var meshes = new JArray(); var materials = new JArray(); var roots = new JArray();
            var images = new JArray(); var textures = new JArray();
            string Image(Color32 pixel)
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try { texture.SetPixels32(Enumerable.Repeat(pixel, 4).ToArray()); texture.Apply(); return "data:image/png;base64," + Convert.ToBase64String(texture.EncodeToPNG()); }
                finally { Object.DestroyImmediate(texture); }
            }
            images.Add(new JObject { ["uri"] = Image(new Color32(180, 90, 40, 255)) });
            images.Add(new JObject { ["uri"] = Image(new Color32(128, 128, 255, 255)) });
            for (int i = 0; i < (distinctPacked ? count : 1); i++) images.Add(new JObject { ["uri"] = Image(new Color32(255, (byte)(64+i), 192, 255)) });
            for (int i = 0; i < images.Count; i++) textures.Add(new JObject { ["source"] = i });
            // UV0 in a self-contained data buffer, shared by all triangles.
            byte[] uv = new float[] { 0, 0, 1, 0, 0, 1 }.SelectMany(BitConverter.GetBytes).ToArray();
            ((JArray)doc["buffers"]).Add(new JObject { ["byteLength"] = uv.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(uv) });
            ((JArray)doc["bufferViews"]).Add(new JObject { ["buffer"] = 1, ["byteLength"] = uv.Length });
            ((JArray)doc["accessors"]).Add(new JObject { ["bufferView"] = 2, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC2" });
            byte[] normals = Enumerable.Repeat(new float[] { 0, 0, 1 }, 3).SelectMany(v => v).SelectMany(BitConverter.GetBytes).ToArray();
            ((JArray)doc["buffers"]).Add(new JObject { ["byteLength"] = normals.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(normals) });
            ((JArray)doc["bufferViews"]).Add(new JObject { ["buffer"] = 2, ["byteLength"] = normals.Length });
            ((JArray)doc["accessors"]).Add(new JObject { ["bufferView"] = 3, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" });
            for (int i = 0; i < count; i++)
            {
                roots.Add(i);
                nodes.Add(new JObject { ["name"] = "Part"+i, ["mesh"] = i });
                var primitive = doc["meshes"][0]["primitives"][0].DeepClone();
                primitive["attributes"]["NORMAL"] = 3; primitive["attributes"]["TEXCOORD_0"] = 2; primitive["material"] = i;
                meshes.Add(new JObject { ["primitives"] = new JArray(primitive) });
                materials.Add(new JObject { ["name"] = "Slot"+i,
                    ["pbrMetallicRoughness"] = new JObject { ["baseColorTexture"] = new JObject { ["index"] = 0 },
                        ["metallicRoughnessTexture"] = new JObject { ["index"] = distinctPacked ? 2+i : 2 }, ["metallicFactor"] = 1, ["roughnessFactor"] = 1 },
                    ["normalTexture"] = new JObject { ["index"] = 1 } });
            }
            doc["nodes"] = nodes; doc["meshes"] = meshes; doc["materials"] = materials;
            doc["scenes"][0]["nodes"] = roots; doc["images"] = images; doc["textures"] = textures;
            return ForgeImportTests.Binary(doc);
        }
    }
}
