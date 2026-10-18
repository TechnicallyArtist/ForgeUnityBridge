using System.Collections;
using System.IO;
using System.Linq;
using ForgeBridge.Editor.Import;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeCollisionTests
    {
        private const string Root = "Assets/ForgeCollisionTests";
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
        private static byte[] Fixture(string name) => File.ReadAllBytes(
            "Packages/com.technicallyartist.forge-bridge/Tests/Editor/Fixtures/Collision-" + name + ".glb");

        [UnityTest] public IEnumerator Floor_PreservesOpeningAndUserCollidersOnRefreshAndRemoval()
        {
            var bytes = Fixture("floor");
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            string path = first.Result.AssetPath;
            string guid = AssetDatabase.AssetPathToGUID(path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab.transform.Find("Collision").GetComponentsInChildren<BoxCollider>(), Has.Length.EqualTo(4));
            Assert.That(prefab.GetComponentsInChildren<MeshRenderer>(), Has.Length.EqualTo(1));
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance, scene);
                var physics = scene.GetPhysicsScene();
                Assert.That(physics, Is.Not.EqualTo(Physics.defaultPhysicsScene), "Physics tests must be isolated.");
                physics.Simulate(0.02f);
                Assert.That(physics.Raycast(new Vector3(0, 2, 0), Vector3.down, out _, 5), Is.False, "Floor hole must stay open.");
                Assert.That(physics.Raycast(new Vector3(1, 2, 0), Vector3.down, out _, 5), Is.True);
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = new Vector3(1, 1, 0);
                sphere.transform.localScale = Vector3.one * 0.2f;
                SceneManager.MoveGameObjectToScene(sphere, scene);
                sphere.AddComponent<Rigidbody>();
                var falling = Object.Instantiate(sphere);
                falling.transform.position = new Vector3(0, 1, 0);
                SceneManager.MoveGameObjectToScene(falling, scene);
                for (int i = 0; i < 150; i++) physics.Simulate(0.02f);
                Assert.That(falling.transform.position.y, Is.LessThan(-2), "Sphere must fall through the opening.");
                Assert.That(sphere.transform.position.y, Is.InRange(0.12f, 0.18f), "Sphere should rest on floor collision.");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            var edit = PrefabUtility.LoadPrefabContents(path);
            edit.AddComponent<SphereCollider>().radius = 0.123f;
            PrefabUtility.SaveAsPrefabAsset(edit, path);
            PrefabUtility.UnloadPrefabContents(edit);
            var variantInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            variantInstance.AddComponent<CapsuleCollider>();
            string variantPath = Root + "/Variant.prefab";
            PrefabUtility.SaveAsPrefabAsset(variantInstance, variantPath);
            Object.DestroyImmediate(variantInstance);
            foreach (var payload in new[] { bytes, ForgeImportTests.Triangle() })
            {
                var refresh = ForgeImporter.ImportAsync(ForgeImportTests.Header(payload), payload);
                yield return ForgeImportTests.Await(refresh);
                Assert.That(refresh.Result.Error, Is.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab.GetComponent<SphereCollider>().radius, Is.EqualTo(0.123f));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).GetComponent<CapsuleCollider>(), Is.Not.Null);
            }
            Assert.That(prefab.transform.Find("Collision"), Is.Null);
        }

        [UnityTest] public IEnumerator Ramp_UsesSavedConvexMeshWithStableGuidAndPrunesItForSteps()
        {
            var bytes = Fixture("ramp");
            var first = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(first.Result.AssetPath);
            var hull = prefab.GetComponentsInChildren<MeshCollider>().Single();
            Assert.That(hull.convex, Is.True);
            Assert.That(hull.sharedMesh.vertexCount, Is.InRange(4, 32));
            CheckRampPhysics(prefab);
            string meshPath = AssetDatabase.GetAssetPath(hull.sharedMesh);
            Assert.That(meshPath, Does.Contain("/Meshes/Collision/"));
            string guid = AssetDatabase.AssetPathToGUID(meshPath);
            var refresh = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(meshPath), Is.EqualTo(guid));
            bytes = Fixture("steps");
            var steps = ForgeImporter.ImportAsync(ForgeImportTests.Header(bytes), bytes);
            yield return ForgeImportTests.Await(steps);
            Assert.That(steps.Result.Error, Is.Null);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(steps.Result.AssetPath);
            Assert.That(prefab.GetComponentsInChildren<BoxCollider>(), Has.Length.EqualTo(5));
            Assert.That(prefab.GetComponentsInChildren<MeshCollider>(), Is.Empty);
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), Is.Null);
        }
        private static void CheckRampPhysics(GameObject prefab)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance, scene);
                var physics = scene.GetPhysicsScene();
                Assert.That(physics, Is.Not.EqualTo(Physics.defaultPhysicsScene));
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.localScale = Vector3.one * 0.1f;
                cube.transform.position = new Vector3(0, 1.8f, 1.2f);
                SceneManager.MoveGameObjectToScene(cube, scene);
                cube.AddComponent<Rigidbody>();
                for (int i = 0; i < 150; i++) physics.Simulate(0.02f);
                Assert.That(cube.transform.position.y, Is.InRange(1.03f, 1.08f));
                Object.DestroyImmediate(cube);
                foreach (float heightScale in new[] { 1f, 3f })
                {
                    instance.transform.localScale = new Vector3(1, heightScale, 1);
                    var walker = new GameObject("Walker");
                    SceneManager.MoveGameObjectToScene(walker, scene);
                    walker.transform.position = new Vector3(0, 0.2f * heightScale + 0.21f, -0.1f);
                    var controller = walker.AddComponent<CharacterController>();
                    controller.height = 0.4f;
                    controller.radius = 0.08f;
                    controller.skinWidth = 0.005f;
                    controller.stepOffset = 0.02f;
                    controller.slopeLimit = 45;
                    Physics.SyncTransforms();
                    for (int i = 0; i < 115; i++)
                    {
                        controller.Move(new Vector3(0, -0.02f, 0.01f));
                        physics.Simulate(0.02f);
                    }
                    if (heightScale == 1) Assert.That(walker.transform.position.y, Is.GreaterThan(1.05f), "Controller must climb the walkable ramp.");
                    else Assert.That(walker.transform.position.z, Is.LessThan(0.5f), "Controller must refuse the steep ramp.");
                    Object.DestroyImmediate(walker);
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
