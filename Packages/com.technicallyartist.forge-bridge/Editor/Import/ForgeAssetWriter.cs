using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Import
{
    public static class ForgeAssetWriter
    {
        public static T Save<T>(T source, string path) where T : Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(source, existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            ForgeIndex.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var copy = Object.Instantiate(source);
            copy.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        public static bool IsReferenced(Object asset, string excludedIndex)
        {
            string target = AssetDatabase.GetAssetPath(asset);
            if (AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                p != target && p != excludedIndex && !AssetDatabase.IsValidFolder(p))
                .Any(p => AssetDatabase.GetDependencies(p, true).Contains(target))) return true;
            // Unsaved scene overrides and editor-open controller changes are user-owned too.
            var loaded = new List<Object>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded) loaded.AddRange(scene.GetRootGameObjects());
            }
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) loaded.Add(stage.prefabContentsRoot);
            loaded.AddRange(Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>().Where(o => o != asset));
            loaded.AddRange(Resources.FindObjectsOfTypeAll<ScriptableObject>().Where(o => o != asset &&
                EditorUtility.IsPersistent(o) && EditorUtility.IsDirty(o) && AssetDatabase.GetAssetPath(o) != excludedIndex));
            return loaded.Count != 0 && EditorUtility.CollectDependencies(loaded.ToArray()).Contains(asset);
        }

        public static void SaveChanges(IEnumerable<string> paths)
        {
            foreach (string path in paths.Distinct())
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) AssetDatabase.SaveAssetIfDirty(new GUID(guid));
            }
        }
    }

    // A failed disk/import operation restores both contents and metas, including the ownership index.
    internal sealed class ForgeWriteTransaction : IDisposable
    {
        private readonly string backup = Path.Combine("Library", "ForgeBridge", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> files = new Dictionary<string, string>();
        private readonly Dictionary<string, HashSet<long>> dirtyObjects = new Dictionary<string, HashSet<long>>();
        private readonly string[] assetPaths;
        private bool committed;

        public ForgeWriteTransaction(IEnumerable<string> paths)
        {
            assetPaths = paths.Distinct().ToArray();
            Directory.CreateDirectory(backup);
            try
            {
                foreach (string path in assetPaths)
                {
                    if (!File.Exists(path)) continue;
                    var dirty = new HashSet<long>();
                    foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (asset != null && EditorUtility.IsDirty(asset) &&
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId)) dirty.Add(localId);
                    }
                    if (dirty.Count == 0) continue;
                    dirtyObjects.Add(path, dirty);
                    // Snapshot the complete serialized file, including any unsaved controller
                    // states/transitions, without saving unrelated user assets in the project.
                    AssetDatabase.SaveAssetIfDirty(new GUID(AssetDatabase.AssetPathToGUID(path)));
                }
                foreach (string path in assetPaths.SelectMany(p => new[] { p, p + ".meta" }))
                {
                    string saved = Path.Combine(backup, files.Count.ToString());
                    if (File.Exists(path)) { File.Copy(path, saved); files.Add(path, saved); }
                    else files.Add(path, null);
                }
            }
            catch { Directory.Delete(backup, true); throw; }
        }

        public void Commit() { committed = true; }
        public void Dispose()
        {
            try
            {
                if (!committed)
                {
                    // Refresh alone can leave unsaved CopySerialized changes alive when the
                    // restored file hashes match Unity's previous import. Clear and force reload.
                    foreach (string path in assetPaths)
                        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (asset != null) EditorUtility.ClearDirty(asset);
                    foreach (var pair in files)
                    {
                        if (pair.Value != null) File.Copy(pair.Value, pair.Key, true);
                        else if (File.Exists(pair.Key)) File.Delete(pair.Key);
                    }
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    foreach (string path in assetPaths)
                        if (files[path] != null)
                            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    foreach (var pair in dirtyObjects)
                        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(pair.Key))
                            if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId) && pair.Value.Contains(localId))
                                EditorUtility.SetDirty(asset);
                }
            }
            finally { Directory.Delete(backup, true); }
        }
    }
}
