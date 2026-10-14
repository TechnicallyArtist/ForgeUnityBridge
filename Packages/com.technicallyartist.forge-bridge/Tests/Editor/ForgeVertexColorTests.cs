using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ForgeBridge.Editor.Tests
{
    public sealed class ForgeVertexColorTests
    {
        private const string Root = "Assets/ForgeVertexColorTests";
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

        [UnityTest] public IEnumerator ColoredPush_RefusesDecodedTintAndLeavesExistingAssetsUnchanged()
        {
            byte[] original = ForgeImportTests.Triangle();
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(original), original);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var before = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            // Normalized U8/U16 with padding, float RGB and float RGBA cover the decoder's
            // meaningful representations; only the last vertex is tinted to exercise all indices.
            foreach (var format in new[] { (5121, 4), (5123, 4), (5126, 3), (5126, 4) })
            {
                byte[] colored = ColoredTriangle(format.Item1, format.Item2, new Color(.3f, 1, 1, 1));
                var push = ForgeImporter.ImportAsync(ForgeImportTests.Header(colored), colored);
                yield return ForgeImportTests.Await(push);
                Assert.That(push.Result.Error, Does.Contain("COLOR_0"), "Color representation " + format);
                Assert.That(Directory.GetFiles(Root, "*", SearchOption.AllDirectories), Is.EquivalentTo(before.Keys));
                foreach (var pair in before) Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value), pair.Key);
            }
        }

        [UnityTest] public IEnumerator WhiteColorsAndOpaqueVertexAlpha_ImportAndRefreshNormally()
        {
            string prefabGuid = null;
            foreach (var format in new[] { (5121, 4), (5123, 4), (5126, 3), (5126, 4) })
            {
                byte[] white = ColoredTriangle(format.Item1, format.Item2, new Color(1, 1, 1, .2f));
                var push = ForgeImporter.ImportAsync(ForgeImportTests.Header(white), white);
                yield return ForgeImportTests.Await(push);
                Assert.That(push.Result.Error, Is.Null, "Color representation " + format);
                string guid = AssetDatabase.AssetPathToGUID(push.Result.AssetPath);
                if (prefabGuid == null) prefabGuid = guid;
                else Assert.That(guid, Is.EqualTo(prefabGuid));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(push.Result.AssetPath);
                Assert.That(prefab.GetComponentInChildren<MeshFilter>().sharedMesh.colors.Length, Is.EqualTo(3));
            }
        }

        [UnityTest] public IEnumerator VertexAlpha_RefusesMaskedAndBlendedMaterialsBeforeCreatingAssets()
        {
            foreach (string mode in new[] { "MASK", "BLEND" })
            {
                byte[] alpha = ColoredTriangle(5126, 4, new Color(1, 1, 1, .2f), mode);
                var push = ForgeImporter.ImportAsync(ForgeImportTests.Header(alpha), alpha);
                yield return ForgeImportTests.Await(push);
                Assert.That(push.Result.Error, Does.Contain("COLOR_0"));
                Assert.That(Directory.Exists(Root), Is.False);
            }
        }

        [Test] public void VertexColorValidation_UsesRenderedIndicesIncludingBaseVertex()
        {
            var mesh = new Mesh { vertices = new Vector3[6], colors = new[] { Color.red, Color.red, Color.red, Color.white, Color.white, Color.white } };
            try
            {
                mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false, 3);
                Assert.DoesNotThrow(() => ForgeMaterials.ValidateVertexColors(mesh, new[] { true }));
                var colors = mesh.colors;
                colors[4] = Color.red;
                mesh.colors = colors;
                Assert.Throws<System.NotSupportedException>(() => ForgeMaterials.ValidateVertexColors(mesh, new[] { true }));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static byte[] ColoredTriangle(int component, int width, Color last, string alphaMode = "OPAQUE")
        {
            int element = component == 5121 ? 1 : component == 5123 ? 2 : 4;
            int stride = (element * width + 7) & ~3; // Four padding bytes after each color.
            int length = 44 + 3 * stride;
            var json = JObject.Parse(@"{
              'asset':{'version':'2.0'}, 'scene':0, 'scenes':[{'nodes':[0]}],
              'nodes':[{'name':'Triangle','mesh':0}],
              'meshes':[{'primitives':[{'attributes':{'POSITION':0,'COLOR_0':2},'indices':1,'material':0}]}],
              'materials':[{'name':'White','alphaMode':'OPAQUE'}],
              'buffers':[{'byteLength':0}],
              'bufferViews':[{'buffer':0,'byteOffset':0,'byteLength':36},{'buffer':0,'byteOffset':36,'byteLength':6},
                {'buffer':0,'byteOffset':44,'byteLength':0,'byteStride':0}],
              'accessors':[{'bufferView':0,'componentType':5126,'count':3,'type':'VEC3','min':[0,0,0],'max':[1,1,0]},
                {'bufferView':1,'componentType':5123,'count':3,'type':'SCALAR'},
                {'bufferView':2,'componentType':5126,'count':3,'type':'VEC4'}] }");
            json["buffers"][0]["byteLength"] = length;
            json["bufferViews"][2]["byteLength"] = 3 * stride;
            json["bufferViews"][2]["byteStride"] = stride;
            json["accessors"][2]["componentType"] = component;
            json["accessors"][2]["type"] = "VEC" + width;
            if (component != 5126) json["accessors"][2]["normalized"] = true;
            json["materials"][0]["alphaMode"] = alphaMode;
            byte[] text = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
            int jsonLength = (text.Length + 3) & ~3;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(28 + jsonLength + length);
                writer.Write(jsonLength); writer.Write(0x4e4f534a); writer.Write(text);
                for (int i = text.Length; i < jsonLength; i++) writer.Write((byte)32);
                writer.Write(length); writer.Write(0x004e4942);
                foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    Color color = vertex == 2 ? last : Color.white;
                    for (int channel = 0; channel < width; channel++)
                    {
                        float value = color[channel];
                        if (component == 5121) writer.Write((byte)Mathf.RoundToInt(value * 255));
                        else if (component == 5123) writer.Write((ushort)Mathf.RoundToInt(value * 65535));
                        else writer.Write(value);
                    }
                    for (int pad = element * width; pad < stride; pad++) writer.Write((byte)97);
                }
                return stream.ToArray();
            }
        }
    }
}
