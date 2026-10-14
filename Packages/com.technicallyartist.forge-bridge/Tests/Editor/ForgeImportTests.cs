using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    public class ForgeImportTests
    {
        private const string Root = "Assets/ForgeImportTests";
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

        [UnityTest] public IEnumerator StaticPush_RefreshesMovedAssetsAndPreservesVariantAndUserMaterial()
        {
            byte[] glb = Triangle();
            var first = ForgeImporter.ImportAsync(Header(glb), glb); yield return Await(first);
            Assert.That(first.Result.Error, Is.Null);
            Assert.That(first.Result.Created, Is.True);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(first.Result.AssetPath);
            Assert.That(prefab.GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(3));
            string prefabGuid = AssetDatabase.AssetPathToGUID(first.Result.AssetPath);
            string meshPath = Root + "/Test/Meshes/Triangle.asset";
            string meshGuid = AssetDatabase.AssetPathToGUID(meshPath);
            var custom = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(custom, Root + "/Custom.mat");
            var edit = PrefabUtility.LoadPrefabContents(first.Result.AssetPath);
            edit.GetComponentInChildren<MeshRenderer>().sharedMaterial = custom;
            PrefabUtility.SaveAsPrefabAsset(edit, first.Result.AssetPath);
            PrefabUtility.UnloadPrefabContents(edit);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.AddComponent<BoxCollider>();
            var variant = PrefabUtility.SaveAsPrefabAsset(instance, Root + "/Variant.prefab");
            Object.DestroyImmediate(instance);
            Assert.That(AssetDatabase.MoveAsset(Root + "/Test", Root + "/Moved"), Is.Empty);
            var refresh = ForgeImporter.ImportAsync(Header(glb), glb); yield return Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(refresh.Result.Created, Is.False);
            Assert.That(refresh.Result.AssetPath, Is.EqualTo(Root + "/Moved/Triangle.prefab"));
            Assert.That(AssetDatabase.AssetPathToGUID(refresh.Result.AssetPath), Is.EqualTo(prefabGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "/Moved/Meshes/Triangle.asset"), Is.EqualTo(meshGuid));
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(refresh.Result.AssetPath);
            Assert.That(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial, Is.EqualTo(custom));
            Assert.That(variant.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(Directory.GetFiles(Root, "*.glb", SearchOption.AllDirectories), Is.Empty);
        }

        [UnityTest] public IEnumerator Push_RefusesAnotherProjectAtSamePathBeforeWrites()
        {
            byte[] glb = Triangle();
            var first = ForgeImporter.ImportAsync(Header(glb), glb); yield return Await(first);
            Assert.That(first.Result.Error, Is.Null);
            string[] before = Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
            var header = Header(glb); header["object"]["projectId"] = "different-project";
            var conflict = ForgeImporter.ImportAsync(header, glb); yield return Await(conflict);
            Assert.That(conflict.Result.Error, Does.Contain("another asset"));
            Assert.That(Directory.GetFiles(Root, "*", SearchOption.AllDirectories), Is.EquivalentTo(before));
        }

        [Test] public void Glb_RefusesExternalImageAndCyclicHierarchy()
        {
            var json = TriangleJson(); json["images"] = new JArray(new JObject { ["uri"] = "https://example.com/a.png" });
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Binary(json)));
            json = TriangleJson(); json["nodes"][0]["children"] = new JArray(0);
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Binary(json)));
        }

        [TestCase("../Escape")][TestCase("/Absolute")][TestCase("a//b")][TestCase("a/./b")][TestCase("E:/Escape")]
        public void Folder_RefusesTraversal(string folder) => Assert.Throws<ArgumentException>(() => ForgeIndex.Folder(Root, folder));

        [Test] public void Controller_AppendsClipsPreservingTransitionsParametersAndBlendTrees()
        {
            ForgeIndex.EnsureFolder(Root);
            var idle = new AnimationClip { name = "Idle" }; var walk = new AnimationClip { name = "Walk" }; var jog = new AnimationClip { name = "Jog" };
            AssetDatabase.CreateAsset(idle, Root + "/Idle.anim"); AssetDatabase.CreateAsset(walk, Root + "/Walk.anim"); AssetDatabase.CreateAsset(jog, Root + "/Jog.anim");
            var warnings = new System.Collections.Generic.List<string>();
            var controller = (AnimatorController)ForgeAnimator.Update(Root + "/Test.controller", null, null, false, new[] { idle, walk }, warnings);
            var machine = controller.layers[0].stateMachine;
            Assert.That(machine.defaultState.motion, Is.EqualTo(idle));
            var transition = machine.states[0].state.AddTransition(machine.states[1].state);
            transition.AddCondition(AnimatorConditionMode.If, 0, "Walk");
            controller.AddParameter("Walk", AnimatorControllerParameterType.Bool);
            machine.states[0].state.name = "User renamed Idle";
            string guid = AssetDatabase.AssetPathToGUID(Root + "/Test.controller");
            ForgeAnimator.Update(Root + "/Test.controller", controller, controller, false, new[] { idle, walk, jog }, warnings);
            Assert.That(machine.states.Length, Is.EqualTo(3));
            Assert.That(controller.parameters.Length, Is.EqualTo(1));
            Assert.That(machine.states[0].state.transitions.Single(), Is.EqualTo(transition));
            Assert.That(machine.states[0].state.name, Is.EqualTo("User renamed Idle"));
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "/Test.controller"), Is.EqualTo(guid));
            var custom = AnimatorController.CreateAnimatorControllerAtPath(Root + "/Custom.controller");
            Assert.That(ForgeAnimator.Update(null, controller, custom, true, new[] { jog }, warnings), Is.EqualTo(custom));
            Assert.That(custom.layers[0].stateMachine.states, Is.Empty);
            AssetDatabase.DeleteAsset(Root + "/Test.controller");
            var recreated = (AnimatorController)ForgeAnimator.Update(Root + "/Test.controller", null, null, false, new[] { idle, walk }, warnings);
            Assert.That(recreated.layers[0].stateMachine.states.Length, Is.EqualTo(2));
        }

        internal static IEnumerator Await<T>(Task<T> task)
        {
            double deadline = EditorApplication.timeSinceStartup + 60;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Import timed out.");
            if (task.IsFaulted) throw task.Exception;
        }
        internal static JObject Header(byte[] bytes) => new JObject
        {
            ["protocol"] = "1.1", ["type"] = "push", ["id"] = "test", ["kind"] = "mesh",
            ["object"] = new JObject { ["projectId"] = "test-project", ["nodeId"] = "test-node", ["name"] = "Triangle" },
            ["target"] = new JObject { ["contentPath"] = "Test", ["assetName"] = "Triangle" },
            ["payload"] = new JObject { ["format"] = "glb", ["bytes"] = bytes.Length }
        };
        internal static byte[] Triangle() => Binary(TriangleJson());
        private static JObject TriangleJson() => JObject.Parse(@"{
          'asset':{'version':'2.0'}, 'scene':0, 'scenes':[{'nodes':[0]}], 'nodes':[{'name':'Triangle','mesh':0}],
          'meshes':[{'primitives':[{'attributes':{'POSITION':0},'indices':1}]}],
          'buffers':[{'byteLength':44}], 'bufferViews':[{'buffer':0,'byteOffset':0,'byteLength':36},{'buffer':0,'byteOffset':36,'byteLength':6}],
          'accessors':[{'bufferView':0,'componentType':5126,'count':3,'type':'VEC3','min':[0,0,0],'max':[1,1,0]},
            {'bufferView':1,'componentType':5123,'count':3,'type':'SCALAR'}] }");
        private static byte[] Binary(JObject root)
        {
            var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
            int length = (json.Length + 3) & ~3;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(12 + 8 + length + 8 + 44);
                writer.Write(length); writer.Write(0x4e4f534a); writer.Write(json);
                for (int i = json.Length; i < length; i++) writer.Write((byte)32);
                writer.Write(44); writer.Write(0x004e4942);
                foreach (float f in new float[] { 0,0,0, 1,0,0, 0,1,0 }) writer.Write(f);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
                return stream.ToArray();
            }
        }
    }
}
