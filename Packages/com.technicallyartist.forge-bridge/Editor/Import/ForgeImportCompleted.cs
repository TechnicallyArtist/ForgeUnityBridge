using System.Collections.Generic;
using UnityEngine;

namespace ForgeBridge.Editor.Import
{
    /// <summary>
    /// What one successful push imported, for a game to post-process. Raised by <see cref="ForgeImporter.ImportCompleted"/>
    /// on the main thread after the assets are saved and before the push's result is sent, for every push: requested
    /// through <see cref="ForgeSync.RequestPushAsync"/> or sent from Forge.
    /// </summary>
    public sealed class ForgeImportCompleted
    {
        public string ProjectId;
        public string NodeId;
        /// <summary>The `push-project` request id when Unity asked for this push; null for a push sent from Forge.</summary>
        public string RequestId;
        public bool Requested => RequestId != null;
        public string AssetPath;
        public bool Created;
        public GameObject Prefab;
        /// <summary>Render meshes, without collision hulls.</summary>
        public List<Mesh> Meshes = new List<Mesh>();
        public List<AnimationClip> Clips = new List<AnimationClip>();
        /// <summary>The Animator's controller on a rigged prefab: the generated one or the user's own; null when static.</summary>
        public RuntimeAnimatorController Controller;
        /// <summary>The kept project backup (`Backup~/&lt;name&gt;.ta`), or null when the push carried none.</summary>
        public string BackupPath;
        /// <summary>The push's warnings. A handler may add its own; they reach the Forge tab with the result.</summary>
        public List<string> Warnings;
    }
}
