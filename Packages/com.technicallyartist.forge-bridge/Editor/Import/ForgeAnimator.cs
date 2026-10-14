using System.Collections.Generic;
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
            // A user can remove every layer. Add a new layer only when a new state needs somewhere to go.
            var referenced = new HashSet<AnimationClip>(generated.animationClips);
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                var clip = clips[clipIndex];
                if (referenced.Contains(clip)) continue;
                if (generated.layers.Length == 0) generated.AddLayer("Base Layer");
                var machine = generated.layers[0].stateMachine;
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
    }
}
