using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeOwnershipTests
    {
        private const string Root = "Assets/ForgeOwnershipTests";
        private const string PrefabPath = Root + "/Source.prefab";
        private string projectId;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(Root);
            ForgeIndex.EnsureFolder(Root);
            projectId = "ownership-test-" + Guid.NewGuid().ToString("N");
            var root = new GameObject("Source");
            try { PrefabUtility.SaveAsPrefabAsset(root, PrefabPath); }
            finally { Object.DestroyImmediate(root); }
        }

        [TearDown] public void TearDown() => AssetDatabase.DeleteAsset(Root);

        [TestCase("{\"forgeBridge\":42}")]
        [TestCase("{\"forgeBridge\":\"another-plugin\"}")]
        [TestCase("{\"forgeBridge\":{\"projectId\":{},\"nodeId\":\"n\"}}")]
        [TestCase("{\"forgeBridge\":{\"projectId\":\"\",\"nodeId\":\"n\"}}")]
        [TestCase("opaque user metadata")]
        public void Metadata_ReadIgnoresInvalidIdentityWithoutBreakingOtherPrefabs(string data)
        {
            AssetImporter.GetAtPath(PrefabPath).userData = data;
            Assert.That(ForgeSource.Read(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath)), Is.Null);
        }

        [Test]
        public void Metadata_WritePreservesOtherJsonFields()
        {
            var importer = AssetImporter.GetAtPath(PrefabPath);
            importer.userData = "{\"otherTool\":{\"text\":\"user content\",\"enabled\":true}}";
            ForgeSource.Write(PrefabPath, projectId, "node");
            var data = JObject.Parse(importer.userData);
            Assert.That((string)data["otherTool"]["text"], Is.EqualTo("user content"));
            Assert.That((bool)data["otherTool"]["enabled"], Is.True);
            var identity = ForgeSource.Read(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.That(identity.ProjectId, Is.EqualTo(projectId));
            Assert.That(identity.NodeId, Is.EqualTo("node"));
        }

        [Test]
        public void Metadata_WriteRefusesOpaqueUserDataWithoutReplacingIt()
        {
            var importer = AssetImporter.GetAtPath(PrefabPath);
            importer.userData = "user-owned opaque text";
            Assert.Throws<InvalidOperationException>(() => ForgeSource.Write(PrefabPath, projectId, "node"));
            Assert.That(importer.userData, Is.EqualTo("user-owned opaque text"));
        }

        [Test]
        public void Metadata_WriteLeavesAssetDatabaseInSyncWithMetaFile()
        {
            // A .meta written without a reimport leaves the asset database's record stale; Unity's
            // preview worker then fails with "Build asset version error" until something reimports it.
            ForgeSource.Write(PrefabPath, projectId, "node");
            var written = AssetDatabase.GetAssetDependencyHash(PrefabPath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.GetAssetDependencyHash(PrefabPath), Is.EqualTo(written));
            Assert.That(ForgeSource.Read(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath))?.ProjectId, Is.EqualTo(projectId));
        }

        [Test]
        public void Identity_DuplicateBasePrefabRefusesWhileVariantIsAllowed()
        {
            ForgeSource.Write(PrefabPath, projectId, "node");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var index = ScriptableObject.CreateInstance<ForgeIndex>();
            var entry = new ForgeEntry { ProjectId = projectId, NodeId = "node", Prefab = prefab, BaseName = "Source" };
            index.Entries.Add(entry);
            AssetDatabase.CreateAsset(index, Root + "/Index.asset");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try { PrefabUtility.SaveAsPrefabAsset(instance, Root + "/Variant.prefab"); }
            finally { Object.DestroyImmediate(instance); }
            Assert.That(ForgeIndex.Find(projectId, "node", out _), Is.SameAs(entry));
            Assert.That(AssetDatabase.CopyAsset(PrefabPath, Root + "/Copy.prefab"), Is.True);
            var copied = ForgeSource.Read(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Copy.prefab"));
            Assert.That(copied, Is.Not.Null, "A copied base prefab retains importer identity metadata.");
            Assert.Throws<InvalidOperationException>(() => ForgeIndex.Find(projectId, "node", out _));
        }

        [Test]
        public void Identity_IndexCannotClaimPrefabWithDifferentMetadata()
        {
            ForgeSource.Write(PrefabPath, "another-project", "another-node");
            var index = ScriptableObject.CreateInstance<ForgeIndex>();
            index.Entries.Add(new ForgeEntry { ProjectId = projectId, NodeId = "node", Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) });
            AssetDatabase.CreateAsset(index, Root + "/Index.asset");
            Assert.Throws<InvalidOperationException>(() => ForgeIndex.Find(projectId, "node", out _));
            var identity = ForgeSource.Read(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.That(identity.ProjectId, Is.EqualTo("another-project"));
        }

        [Test]
        public void Collision_RejectsUnimportedCaseAliasesAndDirectories()
        {
            File.WriteAllText(Root + "/Unimported.txt", "user-owned");
            Assert.That(ForgeIndex.HasPathCollision(Root + "/unimported.TXT"), Is.True);
            Assert.That(ForgeIndex.HasPathCollision(Root), Is.True);
            var entry = new ForgeEntry();
            Assert.Throws<InvalidOperationException>(() => entry.Resolve("new", Root + "/unimported.TXT"));
        }

        [Test]
        public void Folder_RepeatedCreationInsideAssetEditingDoesNotCreateNumberedSiblings()
        {
            string path = Root + "/Repeated/Materials/Textures";
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < 10; i++) ForgeIndex.EnsureFolder(path);
            }
            finally { AssetDatabase.StopAssetEditing(); }
            Assert.That(Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')),
                Is.EquivalentTo(new[] { Root + "/Repeated", Root + "/Repeated/Materials", path }));
            Assert.That(AssetDatabase.IsValidFolder(path), Is.True);
        }

        [Test]
        public void Pruning_KeepsControllerAssignedOnlyInUnsavedScene()
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Root + "/Assigned.controller");
            var instance = new GameObject("Unsaved assignment");
            try
            {
                instance.AddComponent<Animator>().runtimeAnimatorController = controller;
                Assert.That(ForgeAssetWriter.IsReferenced(controller, Root + "/Index.asset"), Is.True);
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void Pruning_KeepsClipReferencedOnlyByUnsavedControllerState()
        {
            var clip = new AnimationClip { name = "User state clip" };
            AssetDatabase.CreateAsset(clip, Root + "/Referenced.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Root + "/User.controller");
            controller.layers[0].stateMachine.AddState("Unsaved state").motion = clip;
            Assert.That(ForgeAssetWriter.IsReferenced(clip, Root + "/Index.asset"), Is.True);
        }

        [UnityTest]
        public IEnumerator Refresh_MaterialAndSlotReorderPreservesNamedGuidAndUserAssignment()
        {
            string previousFolder = BridgeSettings.instance.ForgeFolder;
            BridgeSettings.instance.ForgeFolder = Root;
            try
            {
                var firstBytes = TwoMaterials(false);
                var firstHeader = ForgeImportTests.Header(firstBytes);
                firstHeader["object"]["projectId"] = projectId;
                var first = ForgeImporter.ImportAsync(firstHeader, firstBytes);
                yield return ForgeImportTests.Await(first);
                Assert.That(first.Result.Error, Is.Null);
                string redPath = Root + "/Test/Materials/Triangle_Red.mat";
                string redGuid = AssetDatabase.AssetPathToGUID(redPath);
                Assert.That(redGuid, Is.Not.Empty);
                var custom = new Material(Shader.Find("Standard")) { name = "Custom Red" };
                AssetDatabase.CreateAsset(custom, Root + "/Custom.mat");
                var edit = PrefabUtility.LoadPrefabContents(first.Result.AssetPath);
                try
                {
                    var renderer = edit.GetComponentInChildren<MeshRenderer>();
                    Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(2));
                    var slots = renderer.sharedMaterials; slots[0] = custom; renderer.sharedMaterials = slots;
                    PrefabUtility.SaveAsPrefabAsset(edit, first.Result.AssetPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(edit); }
                var nextBytes = TwoMaterials(true);
                var nextHeader = ForgeImportTests.Header(nextBytes);
                nextHeader["object"]["projectId"] = projectId;
                var refresh = ForgeImporter.ImportAsync(nextHeader, nextBytes);
                yield return ForgeImportTests.Await(refresh);
                Assert.That(refresh.Result.Error, Is.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(redPath), Is.EqualTo(redGuid));
                var updated = AssetDatabase.LoadAssetAtPath<GameObject>(refresh.Result.AssetPath).GetComponentInChildren<MeshRenderer>().sharedMaterials;
                Assert.That(updated[1], Is.EqualTo(custom));
                Assert.That(updated[0], Is.Not.EqualTo(custom));
            }
            finally { BridgeSettings.instance.ForgeFolder = previousFolder; }
        }

        private static byte[] TwoMaterials(bool reversed)
        {
            var source = ForgeImportTests.Triangle();
            int oldJsonLength = BitConverter.ToInt32(source, 12);
            var root = JObject.Parse(Encoding.UTF8.GetString(source, 20, oldJsonLength));
            var red = new JObject { ["name"] = "Red", ["pbrMetallicRoughness"] = new JObject { ["baseColorFactor"] = new JArray(1, 0, 0, 1) } };
            var blue = new JObject { ["name"] = "Blue", ["pbrMetallicRoughness"] = new JObject { ["baseColorFactor"] = new JArray(0, 0, 1, 1) } };
            root["materials"] = reversed ? new JArray(blue, red) : new JArray(red, blue);
            var first = (JObject)root["meshes"][0]["primitives"][0];
            var second = (JObject)first.DeepClone(); first["material"] = 0; second["material"] = 1;
            root["meshes"][0]["primitives"] = new JArray(first, second);
            var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
            int length = (json.Length + 3) & ~3;
            var binaryChunk = source.Skip(20 + oldJsonLength).ToArray();
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(20 + length + binaryChunk.Length);
                writer.Write(length); writer.Write(0x4e4f534a); writer.Write(json);
                for (var i = json.Length; i < length; i++) writer.Write((byte)32);
                writer.Write(binaryChunk);
                return stream.ToArray();
            }
        }
    }
}
