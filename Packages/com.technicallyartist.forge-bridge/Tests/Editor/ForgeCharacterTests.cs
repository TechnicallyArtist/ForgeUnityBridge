using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public sealed class ForgeCharacterTests
    {
        private const string Root = "Assets/ForgeCharacterTests";
        private const string FixtureRoot = "Packages/com.technicallyartist.forge-bridge/Fixtures/";
        private const string AssetFolder = Root + "/Fixtures";
        private const string PrefabPath = AssetFolder + "/Subject.prefab";
        private const string ControllerPath = AssetFolder + "/Animations/Subject.controller";
        private string previousFolder;

        [SetUp]
        public void SetUp()
        {
            previousFolder = BridgeSettings.instance.ForgeFolder;
            BridgeSettings.instance.ForgeFolder = Root;
            AssetDatabase.DeleteAsset(Root);
        }

        [TearDown]
        public void TearDown()
        {
            BridgeSettings.instance.ForgeFolder = previousFolder;
            AssetDatabase.DeleteAsset(Root);
        }

        [UnityTest]
        public IEnumerator CharacterPush_MatchesForgeJointPosesIncludingHairAndWritesGenericAssets()
        {
            byte[] bytes = Character();
            var push = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Is.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(ForgeSource.Read(prefab)?.ProjectId, Is.EqualTo("character-test-project"));
            var animator = prefab.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.avatar, Is.Not.Null);
            Assert.That(animator.avatar.isValid, Is.True);
            Assert.That(animator.avatar.isHuman, Is.False);
            Assert.That(AssetDatabase.GetAssetPath(animator.avatar), Does.StartWith(AssetFolder + "/Meshes/"));
            var skin = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(skin, Is.Not.Null);
            Assert.That(skin.rootBone.name, Is.EqualTo("mixamorig:Hips"));
            Assert.That(skin.bones.Length, Is.EqualTo(30));
            Assert.That(skin.bones.Count(bone => bone.name.StartsWith("Hair_", StringComparison.Ordinal)), Is.EqualTo(5));
            Assert.That(skin.sharedMesh.bindposes.Length, Is.EqualTo(skin.bones.Length));
            Assert.That(skin.sharedMesh.boneWeights.Length, Is.EqualTo(skin.sharedMesh.vertexCount));
            Assert.That(skin.sharedMesh.boneWeights.All(weight =>
                Mathf.Abs(weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3 - 1) < .001f), Is.True);
            Assert.That(AssetDatabase.GetAssetPath(skin.sharedMesh), Is.EqualTo(AssetFolder + "/Meshes/Subject.asset"));
            var controller = (AnimatorController)animator.runtimeAnimatorController;
            Assert.That(controller.layers.Length, Is.EqualTo(1));
            Assert.That(controller.layers[0].stateMachine.states.Select(state => state.state.name), Is.EquivalentTo(new[] { "Idle", "Walk" }));
            Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.EqualTo(Clip("Idle")));
            Assert.That(Directory.GetFiles(Root, "*.glb", SearchOption.AllDirectories), Is.Empty);

            var expected = JObject.Parse(File.ReadAllText(FixturePath("forge-character-poses.json")));
            foreach (var expectedClip in expected["clips"])
            {
                string name = (string)expectedClip["name"];
                var clip = Clip(name);
                Assert.That(clip, Is.Not.Null, name);
                Assert.That(clip.legacy, Is.False);
                Assert.That(clip.frameRate, Is.EqualTo(30));
                Assert.That(clip.length, Is.EqualTo((float)expectedClip["duration"]).Within(.001f));
                Assert.That(AnimationUtility.GetAnimationClipSettings(clip).loopTime, Is.True);
                var curves = AnimationUtility.GetCurveBindings(clip);
                foreach (Transform hair in skin.bones.Where(bone => bone.name.StartsWith("Hair_", StringComparison.Ordinal)))
                    Assert.That(curves.Any(curve => curve.path.EndsWith(hair.name, StringComparison.Ordinal) && curve.propertyName.Contains("Rotation")), Is.True, hair.name + " rotation curve");
                foreach (var sample in expectedClip["samples"])
                {
                    var instance = Object.Instantiate(prefab);
                    try
                    {
                        instance.GetComponent<Animator>().enabled = false;
                        clip.SampleAnimation(instance, (float)sample["time"]);
                        AssertPose(instance, sample, name);
                    }
                    finally { Object.DestroyImmediate(instance); }
                }
            }
        }

        [UnityTest]
        public IEnumerator CharacterRefresh_PreservesEveryGuidAndCustomControllerWhileAddingClips()
        {
            byte[] bytes = Character();
            var first = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var guids = AssetGuids();
            var custom = AnimatorController.CreateAnimatorControllerAtPath(Root + "/Custom.controller");
            custom.AddParameter("UserParameter", AnimatorControllerParameterType.Float);
            custom.layers[0].stateMachine.AddState("User state").motion = Clip("Idle");
            EditPrefab(PrefabPath, prefab => prefab.GetComponent<Animator>().runtimeAnimatorController = custom);
            AssetDatabase.SaveAssets();
            var customBytes = File.ReadAllBytes(Root + "/Custom.controller");

            bytes = WithAnimationChange(bytes, animations =>
            {
                var jog = (JObject)animations.Last.DeepClone();
                jog["name"] = "Jog";
                animations.Add(jog);
            });
            var refresh = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(refresh.Result.Created, Is.False);
            foreach (var pair in guids) Assert.That(AssetDatabase.AssetPathToGUID(pair.Key), Is.EqualTo(pair.Value), pair.Key);
            Assert.That(Clip("Jog"), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Animator>().runtimeAnimatorController, Is.EqualTo(custom));
            Assert.That(File.ReadAllBytes(Root + "/Custom.controller"), Is.EqualTo(customBytes), "A user controller must not be rewritten.");
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath).layers[0].stateMachine.states.Length, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ControllerAppend_PreservesUserGraphVariantAssignmentAndRecreatesDeletedController()
        {
            byte[] bytes = Character();
            var first = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            string controllerGuid = AssetDatabase.AssetPathToGUID(ControllerPath);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.Single(state => state.state.motion == Clip("Idle")).state;
            var walk = machine.states.Single(state => state.state.motion == Clip("Walk")).state;
            var transition = idle.AddTransition(walk);
            transition.hasExitTime = false;
            transition.AddCondition(AnimatorConditionMode.If, 0, "Walk");
            controller.AddParameter("Walk", AnimatorControllerParameterType.Bool);
            idle.name = "User renamed Idle";
            controller.AddLayer("User layer");
            var custom = AnimatorController.CreateAnimatorControllerAtPath(Root + "/VariantCustom.controller");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            try
            {
                instance.GetComponent<Animator>().runtimeAnimatorController = custom;
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.GetComponent<Animator>());
                PrefabUtility.SaveAsPrefabAsset(instance, Root + "/Variant.prefab");
            }
            finally { Object.DestroyImmediate(instance); }
            AssetDatabase.SaveAssets();
            bytes = WithAnimationChange(bytes, animations =>
            {
                var jog = (JObject)animations.Last.DeepClone(); jog["name"] = "Jog"; animations.Add(jog);
            });
            var refresh = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            Assert.That(AssetDatabase.AssetPathToGUID(ControllerPath), Is.EqualTo(controllerGuid));
            Assert.That(controller.layers.Length, Is.EqualTo(2));
            Assert.That(controller.parameters.Single().name, Is.EqualTo("Walk"));
            machine = controller.layers[0].stateMachine;
            idle = machine.states.Single(state => state.state.name == "User renamed Idle").state;
            Assert.That(machine.states.Length, Is.EqualTo(3));
            Assert.That(idle.transitions.Single().conditions.Single().parameter, Is.EqualTo("Walk"));
            Assert.That(idle.transitions.Single().destinationState.motion, Is.EqualTo(Clip("Walk")));
            Assert.That(refresh.Result.Warnings.Any(warning => warning.Contains("Jog")), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Variant.prefab").GetComponent<Animator>().runtimeAnimatorController, Is.EqualTo(custom));
            Assert.That(AssetDatabase.DeleteAsset(ControllerPath), Is.True);
            var recreated = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(recreated);
            Assert.That(recreated.Result.Error, Is.Null);
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.layers[0].stateMachine.states.Length, Is.EqualTo(3));
            Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.EqualTo(Clip("Idle")));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Variant.prefab").GetComponent<Animator>().runtimeAnimatorController, Is.EqualTo(custom));
        }

        [UnityTest]
        public IEnumerator StaticRiggedStatic_RefreshesSamePrefabAndPreservesVariant()
        {
            byte[] bytes = ForgeImportTests.Triangle();
            var first = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            string prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
            string meshGuid = AssetDatabase.AssetPathToGUID(AssetFolder + "/Meshes/Subject.asset");
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath), Is.Null);
            bytes = Character();
            var rigged = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(rigged);
            Assert.That(rigged.Result.Error, Is.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab.GetComponent<Animator>(), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren<SkinnedMeshRenderer>(), Is.Not.Null);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                instance.AddComponent<BoxCollider>();
                PrefabUtility.SaveAsPrefabAsset(instance, Root + "/Variant.prefab");
            }
            finally { Object.DestroyImmediate(instance); }
            string variantGuid = AssetDatabase.AssetPathToGUID(Root + "/Variant.prefab");
            bytes = ForgeImportTests.Triangle();
            var plain = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(plain);
            Assert.That(plain.Result.Error, Is.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(PrefabPath), Is.EqualTo(prefabGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetFolder + "/Meshes/Subject.asset"), Is.EqualTo(meshGuid));
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab.GetComponent<Animator>(), Is.Null);
            Assert.That(prefab.GetComponentInChildren<SkinnedMeshRenderer>(), Is.Null);
            Assert.That(prefab.GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(3));
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Variant.prefab");
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "/Variant.prefab"), Is.EqualTo(variantGuid));
            Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(variant.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(variant.GetComponent<Animator>(), Is.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath), Is.Null, "An unreferenced generated controller is removed on a static push.");
        }

        [UnityTest]
        public IEnumerator RemovedClip_IsKeptWhileReferencedAndDeletedAfterItsLastReferenceIsRemoved()
        {
            byte[] bytes = Character();
            var first = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            string idleGuid = AssetDatabase.AssetPathToGUID(ClipPath("Idle"));
            string walkGuid = AssetDatabase.AssetPathToGUID(ClipPath("Walk"));
            bytes = WithAnimationChange(bytes, animations => animations.Last.Remove());
            var retained = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(retained);
            Assert.That(retained.Result.Error, Is.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(ClipPath("Walk")), Is.EqualTo(walkGuid));
            Assert.That(retained.Result.Warnings.Any(warning => warning.Contains("Walk") && warning.Contains("referenc")), Is.True);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var machine = controller.layers[0].stateMachine;
            var walkState = machine.states.Single(state => state.state.motion == Clip("Walk")).state;
            machine.RemoveState(walkState);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            var removed = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(removed);
            Assert.That(removed.Result.Error, Is.Null);
            Assert.That(Clip("Walk"), Is.Null);
            Assert.That(File.Exists(ClipPath("Walk")), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID(ClipPath("Idle")), Is.EqualTo(idleGuid));
        }

        [UnityTest]
        public IEnumerator TexturedForgeFixture_PushWritesNativeMapsAndRefreshesTheirGuids()
        {
            byte[] bytes = File.ReadAllBytes(FixturePath("forge-textured-cube.glb"));
            var push = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Is.Null);
            var pipeline = ForgeMaterials.DetectPipeline();
            var material = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.That(material.shader.name, Is.EqualTo(ForgeMaterials.ShaderName(pipeline)));
            Assert.That(Directory.GetFiles(AssetFolder + "/Materials", "*.mat"), Has.Length.EqualTo(1), "Fully assigned meshes must not create an unused Default material.");
            var maps = new Dictionary<string, string>
            {
                ["BaseColor"] = pipeline == ForgeRenderPipeline.HighDefinition ? "_BaseColorMap" : pipeline == ForgeRenderPipeline.Universal ? "_BaseMap" : "_MainTex",
                ["Normal"] = pipeline == ForgeRenderPipeline.HighDefinition ? "_NormalMap" : "_BumpMap",
                ["Emissive"] = pipeline == ForgeRenderPipeline.HighDefinition ? "_EmissiveColorMap" : "_EmissionMap",
                ["MetallicRoughness"] = pipeline == ForgeRenderPipeline.HighDefinition ? "_MaskMap" : "_MetallicGlossMap"
            };
            foreach (var map in maps)
            {
                var texture = material.GetTexture(map.Value);
                Assert.That(texture, Is.Not.Null, map.Key);
                Assert.That(EditorUtility.IsPersistent(texture), Is.True, map.Key);
                string path = AssetDatabase.GetAssetPath(texture);
                Assert.That(path, Is.EqualTo(AssetFolder + "/Textures/Subject_PaintedMetal_" + map.Key + ".png"));
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer.sRGBTexture, Is.EqualTo(map.Key == "BaseColor" || map.Key == "Emissive"), map.Key);
                if (map.Key == "Normal") Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.NormalMap));
            }
            var sourceMr = ReadPixels(EmbeddedTexture(bytes, 1));
            var expected = ForgeMaterials.PackMetallicRoughness(sourceMr, null, 1, 1, 1, pipeline);
            var actual = ReadPixels(File.ReadAllBytes(AssetDatabase.GetAssetPath(material.GetTexture(maps["MetallicRoughness"]))));
            Assert.That(actual, Is.EqualTo(expected), "The actual Forge glb's packed image must survive native translation.");
            Assert.That(material.IsKeywordEnabled(pipeline == ForgeRenderPipeline.HighDefinition ? "_EMISSIVE_COLOR_MAP" : "_EMISSION"), Is.True);
            var guids = AssetGuids();
            var refresh = ForgeImporter.ImportAsync(Header(bytes), bytes);
            yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(AssetGuids(), Is.EquivalentTo(guids));
        }

        private static void AssertPose(GameObject instance, JToken sample, string clipName)
        {
            var transforms = instance.GetComponentsInChildren<Transform>();
            var differences = new List<string>();
            foreach (var joint in sample["joints"])
            {
                string name = (string)joint["name"];
                var actual = transforms.SingleOrDefault(transform => transform.name == name);
                if (actual == null) { differences.Add(name + " is missing"); continue; }
                float worldDistance = Vector3.Distance(actual.position, Position(joint["worldPosition"]));
                float localDistance = Vector3.Distance(actual.localPosition, Position(joint["localPosition"]));
                float worldAngle = Quaternion.Angle(actual.rotation, Rotation(joint["worldRotation"]));
                float localAngle = Quaternion.Angle(actual.localRotation, Rotation(joint["localRotation"]));
                if (worldDistance > .0005f || localDistance > .0005f || worldAngle > .1f || localAngle > .1f)
                    differences.Add($"{name}: world/local position error {worldDistance * 1000:F3}/{localDistance * 1000:F3} mm, rotation {worldAngle:F3}/{localAngle:F3} degrees");
            }
            Assert.That(differences, Is.Empty, clipName + " at " + sample["time"] + " seconds:\n" + string.Join("\n", differences.Take(10)));
        }

        // The checked-in poses come from Forge's own right-handed pose evaluator.
        private static Vector3 Position(JToken vector) => new Vector3(-(float)vector[0], (float)vector[1], (float)vector[2]);
        private static Quaternion Rotation(JToken rotation) => new Quaternion((float)rotation[0], -(float)rotation[1], -(float)rotation[2], (float)rotation[3]);
        private static string ClipPath(string name) => AssetFolder + "/Animations/Subject_" + name + ".anim";
        private static AnimationClip Clip(string name) => AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath(name));
        private static byte[] Character() => File.ReadAllBytes(FixturePath("forge-character-idle-walk-hair.glb"));
        private static string FixturePath(string name)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(FixtureRoot + name);
            if (package == null) throw new InvalidOperationException("Cannot resolve the Forge Bridge package fixture location.");
            return Path.Combine(package.resolvedPath, "Fixtures", name);
        }
        private static JObject Header(byte[] bytes)
        {
            var result = ForgeImportTests.Header(bytes);
            result["object"]["projectId"] = "character-test-project";
            result["object"]["name"] = "Subject";
            result["target"]["assetName"] = "Subject";
            result["target"]["contentPath"] = "Fixtures";
            return result;
        }

        private static Dictionary<string, string> AssetGuids() => AssetDatabase.GetAllAssetPaths()
            .Where(path => path.StartsWith(AssetFolder + "/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(path))
            .ToDictionary(path => path, path => AssetDatabase.AssetPathToGUID(path));

        private static void EditPrefab(string path, Action<GameObject> edit)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try { edit(root); PrefabUtility.SaveAsPrefabAsset(root, path); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static byte[] WithAnimationChange(byte[] glb, Action<JArray> change)
        {
            var json = ForgeGlb.Validate(glb);
            change((JArray)json["animations"]);
            var encoded = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
            int padded = (encoded.Length + 3) & ~3;
            int tailOffset = 20 + BitConverter.ToInt32(glb, 12);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(20 + padded + glb.Length - tailOffset);
                writer.Write(padded); writer.Write(0x4e4f534a); writer.Write(encoded);
                for (int i = encoded.Length; i < padded; i++) writer.Write((byte)32);
                writer.Write(glb, tailOffset, glb.Length - tailOffset);
                return stream.ToArray();
            }
        }

        private static byte[] EmbeddedTexture(byte[] glb, int textureIndex)
        {
            var json = ForgeGlb.Validate(glb);
            int image = (int)json["textures"][textureIndex]["source"];
            var view = json["bufferViews"][(int)json["images"][image]["bufferView"]];
            int offset = 20 + BitConverter.ToInt32(glb, 12);
            while (BitConverter.ToUInt32(glb, offset + 4) != 0x004e4942) offset += 8 + BitConverter.ToInt32(glb, offset);
            var result = new byte[(int)view["byteLength"]];
            Buffer.BlockCopy(glb, offset + 8 + ((int?)view["byteOffset"] ?? 0), result, 0, result.Length);
            return result;
        }

        private static Color32[] ReadPixels(byte[] png)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try { Assert.That(texture.LoadImage(png), Is.True); return texture.GetPixels32(); }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
