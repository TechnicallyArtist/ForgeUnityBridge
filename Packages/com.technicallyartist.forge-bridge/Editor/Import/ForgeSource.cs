using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ForgeBridge.Editor.Import
{
    // Unity refuses editor-only MonoBehaviours on prefabs. Identity belongs in the
    // prefab importer's .meta instead, under a namespaced userData entry.
    public static class ForgeSource
    {
        public static ForgeIdentity Read(GameObject prefab)
        {
            if (prefab == null) return null;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(prefab));
            if (importer == null || string.IsNullOrEmpty(importer.userData)) return null;
            try
            {
                var identity = JObject.Parse(importer.userData)["forgeBridge"] as JObject;
                if (identity == null || identity["projectId"]?.Type != JTokenType.String || identity["nodeId"]?.Type != JTokenType.String)
                    return null;
                string projectId = (string)identity["projectId"], nodeId = (string)identity["nodeId"];
                return string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(nodeId) ? null :
                    new ForgeIdentity { ProjectId = projectId, NodeId = nodeId };
            }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        public static void Write(string prefabPath, string projectId, string nodeId)
        {
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Forge identity requires projectId and nodeId.");
            var importer = AssetImporter.GetAtPath(prefabPath);
            if (importer == null) throw new InvalidOperationException("Cannot write Forge identity for " + prefabPath);
            JObject data;
            try { data = string.IsNullOrEmpty(importer.userData) ? new JObject() : JObject.Parse(importer.userData); }
            catch (Newtonsoft.Json.JsonException) { throw new InvalidOperationException("The prefab contains non-JSON user metadata. Forge cannot replace it."); }
            data["forgeBridge"] = new JObject { ["projectId"] = projectId, ["nodeId"] = nodeId };
            importer.userData = data.ToString(Newtonsoft.Json.Formatting.None);
            AssetDatabase.WriteImportSettingsIfDirty(prefabPath);
        }
    }

    public sealed class ForgeIdentity { public string ProjectId; public string NodeId; }
}
