using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GLTFast;
using GLTFast.Logging;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Import
{
    // Exposes glTF node identity without relying on names (which glTFast may uniquify).
    internal sealed class ForgeInstantiator : GameObjectInstantiator
    {
        public ForgeInstantiator(IGltfReadable gltf, Transform parent, ICodeLogger logger, InstantiationSettings settings)
            : base(gltf, parent, logger, settings) { }
        public GameObject Node(int index) => m_Nodes.TryGetValue((uint)index, out var node) ? node : null;
    }

    internal sealed class ForgeCollision
    {
        public readonly Dictionary<Mesh, string> MeshKeys = new Dictionary<Mesh, string>();
        private readonly List<MeshCollider> hulls = new List<MeshCollider>();

        public static ForgeCollision Prepare(JObject json, ForgeInstantiator instance, GameObject root)
        {
            var result = new ForgeCollision();
            var nodes = json["nodes"] as JArray;
            if (nodes == null) return result;
            Transform parent = null;
            int boxes = 0, convexes = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                var tag = nodes[i]["extras"]?["forge"]?["collision"];
                if (tag == null) continue;
                var node = instance.Node(i);
                if (node == null) throw new InvalidDataException("Collision node must belong to the main scene.");
                if (node.transform.childCount != 0) throw new InvalidDataException("Collision nodes cannot have children.");
                if (parent == null)
                {
                    if (root.transform.Find("Collision") != null) throw new InvalidDataException("The render hierarchy uses the reserved Collision name.");
                    parent = new GameObject("Collision").transform;
                    parent.SetParent(root.transform, false);
                }
                string kind = (string)tag["kind"];
                if (kind == "box")
                {
                    if (nodes[i]["mesh"] != null) throw new InvalidDataException("A box collision node cannot hold a render mesh.");
                    var size = tag["size"] as JArray;
                    if (size == null || size.Count != 3) throw new InvalidDataException("Collision box size needs three values.");
                    Vector3 dimensions = new Vector3(Positive(size[0]), Positive(size[1]), Positive(size[2]));
                    node.name = "Box_" + boxes++;
                    node.transform.SetParent(parent, true);
                    var collider = node.AddComponent<BoxCollider>();
                    collider.center = Vector3.zero;
                    collider.size = dimensions;
                }
                else if (kind == "convex")
                {
                    var filter = node.GetComponent<MeshFilter>();
                    var renderer = node.GetComponent<MeshRenderer>();
                    if (filter == null || renderer == null || node.GetComponent<SkinnedMeshRenderer>() != null)
                        throw new InvalidDataException("Convex collision requires one static triangle mesh.");
                    Mesh mesh = filter.sharedMesh;
                    ValidateHull(mesh);
                    string key = "mesh/collision/" + convexes;
                    node.name = "Convex_" + convexes++;
                    node.transform.SetParent(parent, true);
                    if (!result.MeshKeys.ContainsKey(mesh)) result.MeshKeys.Add(mesh, key);
                    Object.DestroyImmediate(renderer);
                    Object.DestroyImmediate(filter);
                    var collider = node.AddComponent<MeshCollider>();
                    collider.convex = true;
                    collider.sharedMesh = mesh;
                    result.hulls.Add(collider);
                }
                else throw new InvalidDataException("Unknown collision shape: " + kind);
            }
            return result;
        }

        public void AssignSavedMeshes(Dictionary<Mesh, Mesh> meshMap)
        {
            foreach (var collider in hulls) collider.sharedMesh = meshMap[collider.sharedMesh];
        }

        public static void PreserveUserColliders(GameObject previous, GameObject replacement)
        {
            if (previous == null) return;
            foreach (var collider in previous.GetComponentsInChildren<Collider>(true))
            {
                string path = AnimationUtility.CalculateTransformPath(collider.transform, previous.transform);
                if (path == "Collision" || path.StartsWith("Collision/", StringComparison.Ordinal)) continue;
                Transform target = string.IsNullOrEmpty(path) ? replacement.transform : replacement.transform.Find(path);
                if (target == null) target = CopyPath(collider.transform, previous.transform, replacement.transform);
                var copy = target.gameObject.AddComponent(collider.GetType());
                EditorUtility.CopySerialized(collider, copy);
            }
        }

        private static Transform CopyPath(Transform source, Transform oldRoot, Transform newRoot)
        {
            if (source == oldRoot) return newRoot;
            var parent = CopyPath(source.parent, oldRoot, newRoot);
            var existing = parent.Find(source.name);
            if (existing != null) return existing;
            var copy = new GameObject(source.name).transform;
            copy.SetParent(parent, false);
            copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation;
            copy.localScale = source.localScale;
            copy.gameObject.layer = source.gameObject.layer;
            copy.gameObject.SetActive(source.gameObject.activeSelf);
            return copy;
        }

        private static float Positive(JToken value)
        {
            if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new InvalidDataException("Collision size must be numeric.");
            float number = (float)value;
            if (float.IsNaN(number) || float.IsInfinity(number) || number <= 0) throw new InvalidDataException("Collision size must be positive and finite.");
            return number;
        }

        private static void ValidateHull(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount < 4 || mesh.vertexCount > 32 || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles)
                throw new InvalidDataException("Convex collision needs 4-32 points and one triangle primitive.");
            var points = mesh.vertices;
            if (points.Any(p => float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.z)))
                throw new InvalidDataException("Collision points must be finite.");
            var triangles = mesh.triangles;
            if (triangles.Length < 12 || triangles.Length / 3 > 255) throw new InvalidDataException("Invalid convex collision triangle count.");
            float tolerance = Mathf.Max(mesh.bounds.size.magnitude * 1e-5f, 1e-7f);
            var center = points.Aggregate(Vector3.zero, (sum, point) => sum + point) / points.Length;
            double volume = 0;
            var edges = new Dictionary<(int, int), int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                var normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                volume += Vector3.Dot(points[a] - center, normal) / 6.0;
                if (normal.sqrMagnitude < 1e-20f) throw new InvalidDataException("Degenerate convex collision triangle.");
                normal.Normalize();
                if (Vector3.Dot(normal, center - points[a]) > tolerance) throw new InvalidDataException("Convex collision winding must face outward.");
                if (points.Any(p => Vector3.Dot(normal, p - points[a]) > tolerance)) throw new InvalidDataException("Collision mesh is not convex.");
                foreach (var edge in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                    edges.TryGetValue(key, out int count); edges[key] = count + 1;
                }
            }
            if (volume <= 0) throw new InvalidDataException("Collision hull must enclose positive volume.");
            if (edges.Values.Any(count => count != 2)) throw new InvalidDataException("Collision hull must be closed.");
        }
    }
}
