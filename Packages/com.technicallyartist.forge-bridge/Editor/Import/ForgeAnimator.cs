using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ForgeBridge.Editor.Import
{
    public static class ForgeAnimator
    {
        public static RuntimeAnimatorController Update(string path, AnimatorController generated,
            RuntimeAnimatorController assigned, bool preserveAssignment, IReadOnlyList<AnimationClip> clips, List<string> warnings,
            IReadOnlyList<string> clipNames = null)
        {
            if (preserveAssignment) return assigned;
            if (generated == null)
            {
                ForgeIndex.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                generated = AnimatorController.CreateAnimatorControllerAtPath(path);
            }
            // A missing graph is corruption, not an empty controller. Never replace user work
            // with a new graph when the original cannot be recovered. Synced layers borrow one.
            if (generated.layers.Any(layer => layer.syncedLayerIndex < 0 && layer.stateMachine == null))
                throw new InvalidDataException("The generated Animator Controller has a missing state machine. Restore its graph from a backup, or delete the generated controller to recreate it before pushing.");
            // A user can remove every layer. Add a new layer only when a new state needs somewhere to go.
            var referenced = new HashSet<AnimationClip>(generated.animationClips);
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                var clip = clips[clipIndex];
                if (referenced.Contains(clip)) continue;
                if (generated.layers.Length == 0) generated.AddLayer("Base Layer");
                var machine = generated.layers.FirstOrDefault(layer => layer.syncedLayerIndex < 0 && layer.stateMachine != null)?.stateMachine;
                if (machine == null) throw new InvalidDataException("The generated Animator Controller has no writable state machine for the new clip.");
                string machinePath = AssetDatabase.GetAssetPath(machine);
                if (!string.IsNullOrEmpty(machinePath) && machinePath != path)
                    throw new InvalidDataException("The generated Animator Controller uses a state machine owned by another asset. Give it its own state machine before adding Forge clips.");
                bool first = machine.states.Length == 0;
                // Unity names standalone .anim assets after their file. Keep Forge's logical
                // clip name for states instead of repeating the asset's character prefix.
                var state = machine.AddState(clipNames == null ? clip.name : clipNames[clipIndex]);
                state.motion = clip;
                if (first && machine.defaultState == null) machine.defaultState = state;
                warnings.Add("Added Animator state " + state.name + ".");
                referenced.Add(clip);
            }
            EditorUtility.SetDirty(generated);
            return generated;
        }

        /// <summary>Attach the graph after StopAssetEditing, before the transaction's scoped save.</summary>
        public static void Finish(AnimatorController controller)
        {
            if (controller == null) return;
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) throw new IOException("The generated Animator Controller has no saved asset path.");
            var visited = new HashSet<UnityEngine.Object>();
            bool attached = false;
            void Visit(UnityEngine.Object node)
            {
                if (node == null || !visited.Add(node)) return;
                string ownedPath = AssetDatabase.GetAssetPath(node);
                // Shared clips, blend trees and other persistent user assets remain untouched.
                if (!string.IsNullOrEmpty(ownedPath) && ownedPath != path) return;
                if (!EditorUtility.IsPersistent(node))
                {
                    AssetDatabase.AddObjectToAsset(node, path);
                    if (!EditorUtility.IsPersistent(node) || AssetDatabase.GetAssetPath(node) != path)
                        throw new IOException("Could not save Animator graph object '" + node.name + "' in " + path);
                    EditorUtility.SetDirty(node);
                    attached = true;
                }
                if (node is AnimatorStateMachine machine)
                {
                    foreach (var state in machine.states) Visit(state.state);
                    foreach (var child in machine.stateMachines)
                    {
                        Visit(child.stateMachine);
                        foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine)) Visit(transition);
                    }
                    foreach (var transition in machine.anyStateTransitions) Visit(transition);
                    foreach (var transition in machine.entryTransitions) Visit(transition);
                    foreach (var behaviour in machine.behaviours) Visit(behaviour);
                }
                else if (node is AnimatorState state)
                {
                    foreach (var transition in state.transitions) Visit(transition);
                    foreach (var behaviour in state.behaviours) Visit(behaviour);
                    Visit(state.motion);
                }
                else if (node is BlendTree tree)
                    foreach (var child in tree.children) Visit(child.motion);
            }
            // CreateAnimatorControllerAtPath cannot resolve its path during StartAssetEditing;
            // Unity's AddLayer/AddState therefore leave their graph objects transient. Saving
            // the main asset alone would write null references, even with global SaveAssets.
            foreach (var layer in controller.layers) Visit(layer.stateMachine);
            if (attached) EditorUtility.SetDirty(controller);
        }
    }
}
