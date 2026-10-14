using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Import
{
    [InitializeOnLoad]
    public static class ForgeImporter
    {
        private static readonly SemaphoreSlim Queue = new SemaphoreSlim(1, 1);
        static ForgeImporter() { BridgeService.ImportAsync = ImportAsync; }

        public static async Task<PushResult> ImportAsync(JObject header, byte[] bytes)
        {
            await Queue.WaitAsync();
            try { return await Import(header, bytes); }
            catch (Exception error) { return new PushResult { Error = error.Message }; }
            finally { Queue.Release(); }
        }

        private static async Task<PushResult> Import(JObject header, byte[] bytes)
        {
            if ((string)header["kind"] != "mesh") throw new InvalidDataException("Unknown push kind: " + header["kind"]);
            if ((string)header["payload"]?["format"] != "glb") throw new InvalidDataException("A mesh push must carry a glb.");
            if ((long?)header["payload"]?["bytes"] != bytes.LongLength) throw new InvalidDataException("payload.bytes does not match the received payload.");
            string projectId = Required(header["object"], "projectId");
            string nodeId = Required(header["object"], "nodeId");
            var pipeline = ForgeMaterials.DetectPipeline();
            JObject json = ForgeGlb.Validate(bytes);
            ForgeEntry entry = ForgeIndex.Find(projectId, nodeId, out ForgeIndex index);
            bool created = entry?.Prefab == null;
            if (entry == null) entry = new ForgeEntry { ProjectId = projectId, NodeId = nodeId, BaseName = ForgeIndex.SafeName(Required(header["target"], "assetName")) };
            string folder = created ? ForgeIndex.Folder(BridgeSettings.instance.ForgeFolder, Required(header["target"], "contentPath")) :
                Path.GetDirectoryName(AssetDatabase.GetAssetPath(entry.Prefab)).Replace('\\', '/');
            string prefabPath = created ? folder + "/" + entry.BaseName + ".prefab" : AssetDatabase.GetAssetPath(entry.Prefab);
            if (created && ForgeIndex.HasPathCollision(prefabPath)) throw new InvalidOperationException(prefabPath + " belongs to another asset. Choose a different Folder or Asset name.");
            string indexPath = index != null ? AssetDatabase.GetAssetPath(index) : BridgeSettings.instance.ForgeFolder.TrimEnd('/') + "/ForgeIndex.asset";
            ForgeIndex.ValidateAssetPath(prefabPath);
            ForgeIndex.ValidateAssetPath(indexPath);
            if (index == null && File.Exists(indexPath))
            {
                index = AssetDatabase.LoadAssetAtPath<ForgeIndex>(indexPath);
                if (index == null) throw new InvalidOperationException(indexPath + " is not a Forge ownership index.");
            }
            else if (index == null && ForgeIndex.HasPathCollision(indexPath)) throw new InvalidOperationException(indexPath + " collides with an existing asset or metadata file.");
            var logger = new CollectingLogger();
            using (var generator = new ForgeGltfMaterialGenerator(pipeline))
            using (var gltf = new GltfImport(materialGenerator: generator, deferAgent: new UninterruptedDeferAgent(), logger: logger))
            {
                bool loaded = await gltf.Load(bytes, importSettings: new ImportSettings
                { AnimationMethod = AnimationMethod.Mecanim, NodeNameMethod = NameImportMethod.OriginalUnique, TexturesReadable = true });
                ThrowImportErrors(loaded, logger);
                Scene preview = EditorSceneManager.NewPreviewScene();
                var prepared = new Dictionary<int, ForgePreparedMaterial>();
                var transient = new List<Object>();
                try
                {
                    var root = new GameObject(entry.BaseName);
                    SceneManager.MoveGameObjectToScene(root, preview);
                    var instantiator = new GameObjectInstantiator(gltf, root.transform, logger,
                        new InstantiationSettings { SceneObjectCreation = SceneObjectCreation.Never, Mask = ComponentType.Mesh | ComponentType.Animation });
                    ThrowImportErrors(await gltf.InstantiateMainSceneAsync(instantiator), logger);
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length == 0) throw new InvalidDataException("The glb contains no renderable mesh.");
                    var meshes = gltf.Meshes?.ToArray();
                    if (meshes == null || meshes.Length == 0) throw new InvalidDataException("The glb contains no mesh data.");
                    var sourceClips = gltf.GetAnimationClips() ?? System.Array.Empty<AnimationClip>();
                    var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.bones.Length > 0).ToArray();
                    bool rigged = skins.Length > 0;
                    var warnings = logger.Items?.Where(i => i.Type == LogType.Warning).Select(i => i.ToString()).ToList() ?? new List<string>();
                    var paths = new Dictionary<string, string>();
                    string Plan(string key, string proposed)
                    {
                        string resolved = entry.Resolve(key, proposed);
                        if (paths.Any(p => p.Key != key && string.Equals(p.Value, resolved, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Two generated assets resolve to " + resolved + ". Rename duplicate material or clip names in Forge.");
                        paths[key] = resolved;
                        return resolved;
                    }
                    for (int i = 0; i < meshes.Length; i++) Plan("mesh/" + i, folder + "/Meshes/" + entry.BaseName + (i == 0 ? "" : "_" + i) + ".asset");
                    int materialCount = gltf.MaterialCount;
                    var materialKeys = new Dictionary<int, string>();
                    var sourceMaterialIndices = new Dictionary<Material, int>();
                    for (int i = 0; i < materialCount; i++) sourceMaterialIndices.Add(gltf.GetMaterial(i), i);
                    var usedMaterialIndices = new SortedSet<int>();
                    foreach (var renderer in renderers)
                    {
                        var sourceMaterials = renderer.sharedMaterials;
                        var usesVertexAlpha = new bool[sourceMaterials.Length];
                        for (int slot = 0; slot < sourceMaterials.Length; slot++)
                        {
                            int materialIndex = sourceMaterials[slot] != null && sourceMaterialIndices.TryGetValue(sourceMaterials[slot], out int sourceIndex) ? sourceIndex : -1;
                            usedMaterialIndices.Add(materialIndex);
                            usesVertexAlpha[slot] = materialIndex >= 0 && gltf.GetSourceMaterial(materialIndex).GetAlphaMode() != GLTFast.Schema.MaterialBase.AlphaMode.Opaque;
                        }
                        var sourceMesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                        ForgeMaterials.ValidateVertexColors(sourceMesh, usesVertexAlpha);
                    }
                    // Only instantiated slots produce assets. A material-less primitive uses -1;
                    // a fully assigned mesh needs neither a Default.mat nor unused glTF materials.
                    foreach (int i in usedMaterialIndices)
                    {
                        var material = ForgeMaterials.Prepare(gltf, i, pipeline);
                        prepared.Add(i, material);
                        string slot = i < 0 ? "Default" : ForgeIndex.SafeName(gltf.GetSourceMaterial(i).name ?? "Slot" + i);
                        bool duplicateName = i >= 0 && Enumerable.Range(0, i).Any(j => (gltf.GetSourceMaterial(j).name ?? "Slot" + j) == (gltf.GetSourceMaterial(i).name ?? "Slot" + i));
                        if (duplicateName) slot += "_" + i;
                        string materialKey = i < 0 ? "$default" : (gltf.GetSourceMaterial(i).name ?? "$unnamed" + i);
                        if (duplicateName || materialKeys.Values.Contains(materialKey)) materialKey += "#" + i;
                        materialKeys.Add(i, materialKey);
                        Plan("material/" + materialKey, folder + "/Materials/" + entry.BaseName + "_" + slot + ".mat");
                        foreach (string role in material.TextureRoles) Plan("texture/" + materialKey + "/" + role, folder + "/Textures/" + entry.BaseName + "_" + slot + "_" + role + ".png");
                    }
                    var clips = new List<AnimationClip>();
                    for (int i = 0; i < sourceClips.Length; i++)
                    {
                        AnimationClip clip = Object.Instantiate(sourceClips[i]); transient.Add(clip);
                        clip.name = sourceClips[i].name;
                        clip.legacy = false;
                        JToken metadata = json["animations"]?[i]?["extras"]?["forge"];
                        if (metadata?["frameRate"] != null)
                        {
                            float rate = (float)metadata["frameRate"];
                            if (float.IsNaN(rate) || float.IsInfinity(rate) || rate <= 0 || rate > 1000) throw new InvalidDataException("Invalid animation frame rate.");
                            clip.frameRate = rate;
                        }
                        var settings = AnimationUtility.GetAnimationClipSettings(clip);
                        settings.loopTime = (bool?)metadata?["loop"] ?? false;
                        AnimationUtility.SetAnimationClipSettings(clip, settings);
                        clips.Add(clip);
                        Plan("clip/" + clip.name, folder + "/Animations/" + entry.BaseName + "_" + ForgeIndex.SafeName(clip.name) + ".anim");
                    }
                    if (clips.Select(c => c.name).Distinct().Count() != clips.Count) throw new InvalidDataException("Animation names must be unique.");
                    Avatar avatar = null;
                    Transform hips = null;
                    if (rigged)
                    {
                        hips = skins.SelectMany(s => s.bones).FirstOrDefault(b => b != null && (b.name == "Hips" || b.name.EndsWith(":Hips", StringComparison.Ordinal)));
                        if (hips == null) throw new InvalidDataException("A Forge rig must contain a Hips joint.");
                        foreach (var skin in skins) skin.rootBone = hips;
                        avatar = AvatarBuilder.BuildGenericAvatar(root, hips.name); transient.Add(avatar);
                        if (!avatar.isValid) throw new InvalidDataException("The character hierarchy could not produce a Generic avatar.");
                        avatar.name = entry.BaseName + "_Avatar";
                        Plan("avatar", folder + "/Meshes/" + avatar.name + ".asset");
                    }
                    var previousAnimator = entry.Prefab != null ? entry.Prefab.GetComponent<Animator>() : null;
                    var generatedController = entry.Get<AnimatorController>("controller");
                    var assignedController = previousAnimator != null ? previousAnimator.runtimeAnimatorController : null;
                    bool customController = previousAnimator != null && assignedController != generatedController;
                    if (rigged && !customController) Plan("controller", folder + "/Animations/" + entry.BaseName + ".controller");
                    var userSlots = UserSlots(entry);
                    var clipNames = clips.Select(c => c.name).ToArray();
                    using (var transaction = new ForgeWriteTransaction(paths.Values.Concat(new[] { prefabPath, indexPath })))
                    {
                        AssetDatabase.StartAssetEditing();
                        try
                        {
                            ForgeIndex.EnsureFolder(folder);
                            if (index == null)
                            {
                                ForgeIndex.EnsureFolder(Path.GetDirectoryName(indexPath).Replace('\\', '/'));
                                index = ScriptableObject.CreateInstance<ForgeIndex>();
                                AssetDatabase.CreateAsset(index, indexPath);
                            }
                            if (!index.Entries.Contains(entry)) index.Entries.Add(entry);
                            var meshMap = new Dictionary<Mesh, Mesh>();
                            for (int i = 0; i < meshes.Length; i++)
                            {
                                string key = "mesh/" + i;
                                var mesh = ForgeAssetWriter.Save(meshes[i], paths[key]); entry.Set(key, mesh); meshMap.Add(meshes[i], mesh);
                            }
                            var materialMap = new Dictionary<Material, Material>();
                            var materialIdentities = new Dictionary<Material, string>();
                            Material defaultMaterial = null;
                            foreach (int i in usedMaterialIndices)
                            {
                                string materialKey = materialKeys[i];
                                Material material = prepared[i].Save(paths["material/" + materialKey], role => paths["texture/" + materialKey + "/" + role]);
                                entry.Set("material/" + materialKey, material);
                                materialIdentities[material] = materialKey;
                                if (i < 0) defaultMaterial = material;
                                else materialMap[gltf.GetMaterial(i)] = material;
                            }
                            entry.Slots.Clear();
                            foreach (var renderer in renderers)
                            {
                                if (renderer is SkinnedMeshRenderer skin) skin.sharedMesh = meshMap[skin.sharedMesh];
                                else { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) filter.sharedMesh = meshMap[filter.sharedMesh]; }
                                var materials = renderer.sharedMaterials;
                                var occurrences = new Dictionary<string, int>();
                                for (int slot = 0; slot < materials.Length; slot++)
                                {
                                    Material generated = materials[slot] != null && materialMap.TryGetValue(materials[slot], out var replacement) ? replacement : defaultMaterial;
                                    string key = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform) + "/" + slot;
                                    string materialKey = materialIdentities[generated];
                                    occurrences.TryGetValue(materialKey, out int occurrence);
                                    occurrences[materialKey] = occurrence + 1;
                                    string identity = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform) + "/" + materialKey + "/" + occurrence;
                                    entry.Slots.Add(new ForgeSlot { Key = key, Identity = identity, Generated = generated });
                                    materials[slot] = userSlots.TryGetValue(identity, out var assigned) ? assigned : generated;
                                }
                                renderer.sharedMaterials = materials;
                            }
                            for (int i = 0; i < clips.Count; i++)
                            {
                                string key = "clip/" + clips[i].name;
                                clips[i] = ForgeAssetWriter.Save(clips[i], paths[key]); entry.Set(key, clips[i]);
                            }
                            foreach (var oldAnimator in root.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(oldAnimator);
                            if (rigged)
                            {
                                var animator = root.AddComponent<Animator>();
                                animator.avatar = ForgeAssetWriter.Save(avatar, paths["avatar"]); entry.Set("avatar", animator.avatar);
                                animator.applyRootMotion = false;
                                animator.runtimeAnimatorController = ForgeAnimator.Update(customController ? null : paths["controller"], generatedController,
                                    assignedController, customController, clips, warnings, clipNames);
                                if (!customController) entry.Set("controller", animator.runtimeAnimatorController);
                            }
                            PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool saved);
                            if (!saved) throw new IOException("Could not save " + prefabPath);
                            EditorUtility.SetDirty(index);
                        }
                        finally { AssetDatabase.StopAssetEditing(); }
                        foreach (var material in prepared.Values) material.Finish();
                        foreach (var pair in paths.Where(p => p.Key.StartsWith("texture/", StringComparison.Ordinal))) entry.Set(pair.Key, AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Value));
                        entry.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (entry.Prefab == null) throw new IOException("Unity could not load the saved prefab.");
                        ForgeSource.Write(prefabPath, projectId, nodeId);
                        EditorUtility.SetDirty(index);
                        ForgeAssetWriter.SaveChanges(paths.Values.Concat(new[] { indexPath }));
                        transaction.Commit();
                    }
                    // Prune after the saved prefab releases its old references. Controllers and Variants count as owners.
                    foreach (var item in entry.Assets.Where(a => (a.Key.StartsWith("clip/", StringComparison.Ordinal) && !paths.ContainsKey(a.Key)) || (!rigged && a.Key == "controller")).ToArray())
                    {
                        try
                        {
                            if (item.Asset == null) { entry.Assets.Remove(item); continue; }
                            if (ForgeAssetWriter.IsReferenced(item.Asset, indexPath)) warnings.Add("Kept " + AssetDatabase.GetAssetPath(item.Asset) + " because another asset references it.");
                            else if (AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(item.Asset))) entry.Assets.Remove(item);
                            else warnings.Add("Could not remove unused " + AssetDatabase.GetAssetPath(item.Asset));
                        }
                        catch (Exception error) { warnings.Add("Unused asset cleanup could not finish: " + error.Message); }
                    }
                    EditorUtility.SetDirty(index);
                    try { ForgeAssetWriter.SaveChanges(new[] { indexPath }); }
                    catch (Exception error) { warnings.Add("Could not save cleanup changes to the Forge index: " + error.Message); }
                    return new PushResult { AssetPath = prefabPath, Created = created, Warnings = warnings };
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                    foreach (var material in prepared.Values) material.Dispose();
                    foreach (Object item in transient) if (item != null) Object.DestroyImmediate(item);
                }
            }
        }

        private static Dictionary<string, Material> UserSlots(ForgeEntry entry)
        {
            var assigned = new Dictionary<string, Material>();
            if (entry.Prefab == null) return assigned;
            foreach (var renderer in entry.Prefab.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    string key = AnimationUtility.CalculateTransformPath(renderer.transform, entry.Prefab.transform) + "/" + i;
                    var previous = entry.Slots.FirstOrDefault(s => s.Key == key);
                    if (previous != null && previous.Generated != materials[i]) assigned[previous.Identity ?? key] = materials[i];
                }
            }
            return assigned;
        }
        private static string Required(JToken token, string field)
        {
            if (token?[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token[field])) throw new InvalidDataException(field + " is required.");
            return (string)token[field];
        }
        private static void ThrowImportErrors(bool success, CollectingLogger logger)
        {
            var errors = logger.Items?.Where(i => i.Type == LogType.Error || i.Type == LogType.Exception).Select(i => i.ToString()).ToArray() ?? System.Array.Empty<string>();
            if (!success || errors.Length > 0) throw new InvalidDataException("glTF import failed: " + string.Join("; ", errors));
        }
    }
}
