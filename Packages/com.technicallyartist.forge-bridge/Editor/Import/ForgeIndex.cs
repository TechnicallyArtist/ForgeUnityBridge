using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Import
{
    public sealed class ForgeIndex : ScriptableObject
    {
        public List<ForgeEntry> Entries = new List<ForgeEntry>();

        public static ForgeEntry Find(string projectId, string nodeId, out ForgeIndex index)
        {
            index = null;
            ForgeEntry found = null;
            foreach (string guid in AssetDatabase.FindAssets("t:ForgeIndex"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ForgeIndex>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var entry in candidate.Entries.Where(e => e.ProjectId == projectId && e.NodeId == nodeId))
                {
                    if (found != null) throw new InvalidOperationException("Several Forge indexes contain this identity. Remove the duplicate index before pushing.");
                    index = candidate;
                    found = entry;
                }
            }
            // A copied base prefab is ambiguous, whereas a Variant intentionally inherits identity.
            var prefabs = AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(p => p != null &&
                    PrefabUtility.GetPrefabAssetType(p) != PrefabAssetType.Variant)
                .Select(p => new { Prefab = p, Identity = ForgeSource.Read(p) })
                .Where(p => p.Identity != null && p.Identity.ProjectId == projectId && p.Identity.NodeId == nodeId)
                .Select(p => p.Prefab).ToArray();
            if (prefabs.Length > 1) throw new InvalidOperationException("Several base prefabs carry this Forge identity. Use Prefab Variants for copies.");
            if (found == null && prefabs.Length != 0)
                throw new InvalidOperationException("This Forge prefab's ownership index is missing. Restore its ForgeIndex asset before refreshing.");
            if (found != null && found.Prefab == null && prefabs.Length == 1) found.Prefab = prefabs[0];
            if (found?.Prefab != null)
            {
                var identity = ForgeSource.Read(found.Prefab);
                if (identity == null || identity.ProjectId != projectId || identity.NodeId != nodeId)
                    throw new InvalidOperationException("The indexed prefab's Forge identity is missing or differs from its ownership index. Restore its importer metadata before refreshing.");
            }
            return found;
        }

        public static string SafeName(string value)
        {
            string name = Regex.Replace(value ?? "", @"[^\p{L}\p{N}_-]+", "_").Trim('_');
            if (name.Length > 96) name = name.Substring(0, 96);
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("The asset name has no usable characters.");
            if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) name = "_" + name;
            return name;
        }

        public static string Folder(string root, string folder)
        {
            root = (root ?? "").Replace('\\', '/').TrimEnd('/');
            if (root != "Assets" && !root.StartsWith("Assets/", StringComparison.Ordinal) || root.Split('/').Any(InvalidSegment))
                throw new ArgumentException("Forge folder must be a folder below Assets with no empty, . or .. segments.");
            folder = (folder ?? "").Replace('\\', '/');
            if (folder.Length == 0 || folder.Split('/').Any(InvalidSegment))
                throw new ArgumentException("Folder must be a relative folder with no empty, . or .. segments.");
            string result = root + "/" + folder;
            ValidateAssetPath(result);
            return result;
        }

        /// <summary>Also applied to moved Forge-owned assets, not only new destinations.</summary>
        public static void ValidateAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("\\") || path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Split('/').Any(InvalidSegment))
                throw new ArgumentException("Forge assets must be inside Assets with valid path segments.");
            var assets = Path.GetFullPath("Assets").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var absolute = Path.GetFullPath(path);
            if (!absolute.Equals(assets, comparison) && !absolute.StartsWith(assets + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Forge assets must remain inside Assets.");
            // The final destination often does not exist. Walk every ancestor anyway: an existing
            // junction several levels above a new directory must not redirect a push outside Assets.
            for (var cursor = absolute; cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                try
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("Forge cannot write through a linked file or folder.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (cursor.Equals(assets, comparison)) break;
            }
        }

        private static bool InvalidSegment(string value) => string.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
            value.Length > 255 || value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal) ||
            value.Any(c => c < 32 || ":*?\"<>|".IndexOf(c) >= 0) ||
            Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase);

        public static bool HasPathCollision(string path)
        {
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta")) return true;
            if (AssetDatabase.GetAllAssetPaths().Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))) return true;
            // Also catch files Unity has not imported yet, including case-only aliases on Linux.
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) return false;
            string name = Path.GetFileName(path);
            return Directory.EnumerateFileSystemEntries(parent).Any(existing =>
                string.Equals(Path.GetFileName(existing), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(existing), name + ".meta", StringComparison.OrdinalIgnoreCase));
        }

        public static void EnsureFolder(string path)
        {
            ValidateAssetPath(path);
            // CreateFolder writes the directory immediately, but its AssetDatabase record can be
            // deferred inside StartAssetEditing. Asking CreateFolder again would make "Folder 1".
            if (Directory.Exists(path) || AssetDatabase.IsValidFolder(path)) return;
            if (path == "Assets") throw new IOException("The project Assets folder is unavailable.");
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
                throw new IOException("Could not create " + path);
        }
    }

    [Serializable]
    public sealed class ForgeEntry
    {
        public string ProjectId;
        public string NodeId;
        public string BaseName;
        public GameObject Prefab;
        public List<ForgeOwnedAsset> Assets = new List<ForgeOwnedAsset>();
        public List<ForgeSlot> Slots = new List<ForgeSlot>();

        public T Get<T>(string key) where T : Object => Assets.FirstOrDefault(a => a.Key == key)?.Asset as T;
        public string Resolve(string key, string proposed)
        {
            Object owned = Assets.FirstOrDefault(a => a.Key == key)?.Asset;
            string path = owned == null ? proposed : AssetDatabase.GetAssetPath(owned);
            ForgeIndex.ValidateAssetPath(path);
            if (owned == null && ForgeIndex.HasPathCollision(path))
                throw new InvalidOperationException(path + " already exists and is not owned by this Forge node; nothing was written.");
            return path;
        }
        public void Set(string key, Object asset)
        {
            var item = Assets.FirstOrDefault(a => a.Key == key);
            if (item == null) { item = new ForgeOwnedAsset { Key = key }; Assets.Add(item); }
            item.Asset = asset;
            if (key.StartsWith("folder/", StringComparison.Ordinal)) item.FolderGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
        }
    }

    [Serializable] public sealed class ForgeOwnedAsset { public string Key; public Object Asset; public string FolderGuid; }
    [Serializable] public sealed class ForgeSlot { public string Key; public string Identity; public Material Generated; }
}
