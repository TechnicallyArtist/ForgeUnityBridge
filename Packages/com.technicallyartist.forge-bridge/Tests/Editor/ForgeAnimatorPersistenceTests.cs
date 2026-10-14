using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForgeBridge.Editor.Import;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public sealed class ForgeAnimatorPersistenceTests
    {
        private const string Root = "Assets/ForgeAnimatorPersistenceTests";
        private const string ControllerPath = Root + "/Test/Animations/Character.controller";
        private const string PrefabPath = Root + "/Test/Character.prefab";
        private string previousFolder;

        // UnitySetUp is not replayed after EnterPlayMode's domain reload. A normal SetUp
        // would delete the saved assets just as the second half of the Play test resumes.
        [UnitySetUp] public IEnumerator SetUp()
        {
            previousFolder = BridgeSettings.instance.ForgeFolder;
            BridgeSettings.instance.ForgeFolder = Root;
            AssetDatabase.DeleteAsset(Root);
            yield break;
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            if (!string.IsNullOrEmpty(previousFolder)) BridgeSettings.instance.ForgeFolder = previousFolder;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
        }

        [UnityTest] public IEnumerator ImportedController_PersistsGraphAndPlaysAfterUnloadAndReload()
        {
            byte[] bytes = Character();
            var header = ForgeImportTests.Header(bytes);
            header["target"]["assetName"] = "Character";
            var push = ForgeImporter.ImportAsync(header, bytes);
            yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Is.Null);
            // Check before any global SaveAssets: cached transient graph objects can otherwise
            // make animationClips/defaultState assertions pass despite an empty saved controller.
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            AssertGraphPersistent(controller);
            string guid = AssetDatabase.AssetPathToGUID(ControllerPath);
            Resources.UnloadAsset(controller);
            AssetDatabase.ImportAsset(ControllerPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            AssertGraphPersistent(controller);
            Assert.That(AssetDatabase.AssetPathToGUID(ControllerPath), Is.EqualTo(guid));
            Assert.That(controller.layers[0].stateMachine.defaultState.name, Is.EqualTo("Idle"));
            Assert.That(controller.animationClips.Length, Is.EqualTo(2));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Animator>().runtimeAnimatorController, Is.EqualTo(controller));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var character = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene);
            character.name = "Persisted Character";
            character.GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Assert.That(EditorSceneManager.SaveScene(scene, Root + "/Playback.unity"), Is.True);
            BridgeSettings.instance.ForgeFolder = previousFolder;
            yield return new EnterPlayMode();
            yield return AssertPlayingSavedCharacter();
            yield return new ExitPlayMode();
        }

        // Allocate playback locals after the reload; coroutine closure fields from before
        // EnterPlayMode are not durable test state.
        private static IEnumerator AssertPlayingSavedCharacter()
        {
            var playing = GameObject.Find("Persisted Character").GetComponent<Animator>();
            double initializationDeadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!playing.isInitialized && Time.realtimeSinceStartupAsDouble < initializationDeadline) yield return null;
            Assert.That(playing.isInitialized, Is.True, "The saved scene must initialize its native Animator Controller.");
            // isInitialized can become true before the first Animator evaluation. Wait for
            // the normal player loop, without forcing Play or Animator.Update from the test.
            while (!playing.GetCurrentAnimatorStateInfo(0).IsName("Idle") && Time.realtimeSinceStartupAsDouble < initializationDeadline) yield return null;
            var initialState = playing.GetCurrentAnimatorStateInfo(0);
            Assert.That(initialState.IsName("Idle"), Is.True,
                $"Expected Idle ({Animator.StringToHash("Idle")}/{Animator.StringToHash("Base Layer.Idle")}); " +
                $"actual state {initialState.shortNameHash}/{initialState.fullPathHash}, time {initialState.normalizedTime}; " +
                $"controller {playing.runtimeAnimatorController?.name}, enabled {playing.enabled}, active {playing.gameObject.activeInHierarchy}, " +
                $"clips {string.Join(",", playing.GetCurrentAnimatorClipInfo(0).Select(info => info.clip.name))}.");
            float beforeTime = playing.GetCurrentAnimatorStateInfo(0).normalizedTime;
            Transform hips = null;
            var hair = new List<Transform>();
            foreach (var bone in playing.GetComponentsInChildren<Transform>())
            {
                if (bone.name == "mixamorig:Hips") hips = bone;
                if (bone.name.StartsWith("Hair_", StringComparison.Ordinal)) hair.Add(bone);
            }
            Assert.That(hips, Is.Not.Null);
            Assert.That(hair.Count, Is.EqualTo(5));
            var beforeHips = hips.localRotation;
            var beforeHair = new Quaternion[hair.Count];
            for (int i = 0; i < hair.Count; i++) beforeHair[i] = hair[i].localRotation;
            float hipsChange = 0, hairChange = 0;
            double until = Time.realtimeSinceStartupAsDouble + .75;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
                hipsChange = Mathf.Max(hipsChange, Quaternion.Angle(beforeHips, hips.localRotation));
                for (int i = 0; i < hair.Count; i++) hairChange = Mathf.Max(hairChange, Quaternion.Angle(beforeHair[i], hair[i].localRotation));
            }
            Assert.That(playing.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(beforeTime));
            Assert.That(hipsChange, Is.GreaterThan(.01f));
            Assert.That(hairChange, Is.GreaterThan(.01f));
        }

        [Test] public void AppendedStates_PersistUserGraphAndLocalIdsWithoutSavingUnrelatedAssets()
        {
            var clips = CreateClips();
            var controller = WriteController(null, clips.Take(2).ToArray());
            var machine = controller.layers[0].stateMachine;
            machine.states[0].state.name = "User Idle";
            var transition = machine.states[0].state.AddTransition(machine.states[1].state);
            transition.duration = .37f; transition.hasExitTime = true;
            controller.AddParameter("User Speed", AnimatorControllerParameterType.Float);
            controller.AddLayer("User Layer");
            var nested = controller.layers[1].stateMachine.AddStateMachine("Nested");
            var tree = new BlendTree { name = "User Blend", blendParameter = "User Speed" };
            AssetDatabase.AddObjectToAsset(tree, ControllerPath);
            tree.AddChild(clips[0]);
            nested.AddState("User Blend State").motion = tree;
            EditorUtility.SetDirty(controller);
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            var originalIds = LocalIds();
            string guid = AssetDatabase.AssetPathToGUID(ControllerPath);

            var unrelated = new Material(Shader.Find("Standard")) { color = Color.white };
            string unrelatedPath = Root + "/User.mat";
            AssetDatabase.CreateAsset(unrelated, unrelatedPath);
            byte[] unrelatedBytes = File.ReadAllBytes(unrelatedPath);
            unrelated.color = Color.red; EditorUtility.SetDirty(unrelated);
            controller = WriteController(controller, clips);
            Resources.UnloadAsset(controller);
            AssetDatabase.ImportAsset(ControllerPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            machine = controller.layers[0].stateMachine;
            Assert.That(machine.states.Select(state => state.state.name), Is.EquivalentTo(new[] { "User Idle", "Walk", "Jog" }));
            Assert.That(machine.states.Single(state => state.state.name == "User Idle").state.transitions.Single().duration, Is.EqualTo(.37f));
            Assert.That(controller.parameters.Single().name, Is.EqualTo("User Speed"));
            Assert.That(controller.layers[1].stateMachine.stateMachines.Single().stateMachine.states.Single().state.motion, Is.TypeOf<BlendTree>());
            Assert.That(LocalIds(), Is.SupersetOf(originalIds));
            Assert.That(AssetDatabase.AssetPathToGUID(ControllerPath), Is.EqualTo(guid));
            var appendedIds = LocalIds();
            WriteController(controller, clips);
            Assert.That(LocalIds(), Is.EquivalentTo(appendedIds), "Repeat imports must not duplicate graph subassets.");
            Assert.That(File.ReadAllBytes(unrelatedPath), Is.EqualTo(unrelatedBytes));
            Assert.That(EditorUtility.IsDirty(unrelated), Is.True);
            Assert.That(unrelated.color, Is.EqualTo(Color.red));
        }

        [Test] public void FailedAppend_RestoresDirtyTransitionAndGraphLocalIds()
        {
            var clips = CreateClips();
            var controller = WriteController(null, clips.Take(2).ToArray());
            var machine = controller.layers[0].stateMachine;
            var transition = machine.states[0].state.AddTransition(machine.states[1].state);
            transition.duration = .2f; transition.hasExitTime = true;
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            var originalIds = LocalIds();
            string guid = AssetDatabase.AssetPathToGUID(ControllerPath);
            transition.duration = .73f; EditorUtility.SetDirty(transition);
            Assert.Throws<IOException>(() =>
            {
                var transactionType = typeof(ForgeAssetWriter).Assembly.GetType("ForgeBridge.Editor.Import.ForgeWriteTransaction", true);
                using (var transaction = (IDisposable)Activator.CreateInstance(transactionType, new object[] { new[] { ControllerPath } }))
                {
                    WriteController(controller, clips);
                    throw new IOException("Simulated failure after graph attachment.");
                }
            });
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            machine = controller.layers[0].stateMachine;
            Assert.That(machine.states.Length, Is.EqualTo(2));
            transition = machine.states[0].state.transitions.Single();
            Assert.That(transition.duration, Is.EqualTo(.73f));
            Assert.That(EditorUtility.IsDirty(transition), Is.True);
            Assert.That(LocalIds(), Is.EquivalentTo(originalIds));
            Assert.That(AssetDatabase.AssetPathToGUID(ControllerPath), Is.EqualTo(guid));
        }

        [Test] public void MissingStateMachine_RefusesWithoutReplacingItsController()
        {
            var clips = CreateClips();
            var controller = WriteController(null, clips.Take(2).ToArray());
            var layers = controller.layers; layers[0].stateMachine = null; controller.layers = layers;
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            byte[] before = File.ReadAllBytes(ControllerPath);
            var error = Assert.Throws<InvalidDataException>(() => ForgeAnimator.Update(ControllerPath, controller, controller, false, clips, new List<string>()));
            StringAssert.Contains("Restore", error.Message);
            Assert.That(File.ReadAllBytes(ControllerPath), Is.EqualTo(before));
        }

        [Test] public void ForeignStateMachine_RefusesAppendWithoutModifyingItsOwner()
        {
            var clips = CreateClips();
            var controller = WriteController(null, clips.Take(1).ToArray());
            string foreignPath = Root + "/Foreign.controller";
            var foreign = AnimatorController.CreateAnimatorControllerAtPath(foreignPath);
            foreign.layers[0].stateMachine.AddState("Foreign Walk").motion = clips[1];
            ForgeAssetWriter.SaveChanges(new[] { foreignPath });
            var layers = controller.layers; layers[0].stateMachine = foreign.layers[0].stateMachine; controller.layers = layers;
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            byte[] before = File.ReadAllBytes(foreignPath);
            Assert.Throws<InvalidDataException>(() => ForgeAnimator.Update(ControllerPath, controller, controller, false, new[] { clips[2] }, new List<string>()));
            Assert.That(foreign.layers[0].stateMachine.states.Length, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(foreignPath), Is.EqualTo(before));
        }

        [Test] public void EmptyController_AddsSavedLayerWithoutDiscardingOldGraphSubassets()
        {
            var clips = CreateClips();
            var controller = WriteController(null, clips.Take(2).ToArray());
            var originalIds = LocalIds();
            controller.layers = Array.Empty<AnimatorControllerLayer>();
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            controller = WriteController(controller, clips);
            Assert.That(controller.layers.Length, Is.EqualTo(1));
            Assert.That(EditorUtility.IsPersistent(controller.layers[0].stateMachine), Is.True);
            Assert.That(controller.layers[0].stateMachine.states.Length, Is.EqualTo(3));
            Assert.That(LocalIds(), Is.SupersetOf(originalIds));
        }

        private static AnimationClip[] CreateClips()
        {
            ForgeIndex.EnsureFolder(Root + "/Test/Animations");
            return new[] { "Idle", "Walk", "Jog" }.Select(name =>
            {
                var clip = new AnimationClip { name = name };
                AssetDatabase.CreateAsset(clip, Root + "/" + name + ".anim");
                return clip;
            }).ToArray();
        }

        private static AnimatorController WriteController(AnimatorController controller, IReadOnlyList<AnimationClip> clips)
        {
            AssetDatabase.StartAssetEditing();
            try { controller = (AnimatorController)ForgeAnimator.Update(ControllerPath, controller, controller, false, clips, new List<string>()); }
            finally { AssetDatabase.StopAssetEditing(); }
            ForgeAnimator.Finish(controller);
            ForgeAssetWriter.SaveChanges(new[] { ControllerPath });
            return controller;
        }

        private static long[] LocalIds() => AssetDatabase.LoadAllAssetsAtPath(ControllerPath).Select(asset =>
        {
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId), Is.True);
            return localId;
        }).ToArray();

        private static void AssertGraphPersistent(AnimatorController controller)
        {
            Assert.That(controller, Is.Not.Null);
            var machine = controller.layers[0].stateMachine;
            Assert.That(machine, Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(machine), Is.True, "The state machine must be a native subasset, not just a cached object.");
            Assert.That(AssetDatabase.GetAssetPath(machine), Is.EqualTo(ControllerPath));
            Assert.That(machine.states.Select(state => state.state.name), Is.EquivalentTo(new[] { "Idle", "Walk" }));
            foreach (var state in machine.states)
            {
                Assert.That(EditorUtility.IsPersistent(state.state), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(state.state), Is.EqualTo(ControllerPath));
                Assert.That(state.state.motion, Is.Not.Null);
            }
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(ControllerPath).OfType<AnimatorState>().Count(), Is.EqualTo(2));
        }

        private static byte[] Character()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.technicallyartist.forge-bridge/package.json");
            return File.ReadAllBytes(Path.Combine(package.resolvedPath, "Fixtures", "forge-character-idle-walk-hair.glb"));
        }
    }
}
