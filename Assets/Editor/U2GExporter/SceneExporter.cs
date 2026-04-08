using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U2GExporter
{
    public static class SceneExporter
    {
        /// <summary>
        /// Exports a Unity scene to a Godot .tscn file.
        /// The scene must be loaded additively before calling this.
        /// </summary>
        public static void Export(Scene scene, string assetPath, string outputDir, SkipReport skipReport)
        {
            var writer = new TscnWriter();
            string sceneName = Path.GetFileNameWithoutExtension(assetPath);

            // Root node (synthetic)
            writer.AddRootNode(sceneName, "Node3D");
            writer.AddBlankLine();

            var nodeConverter = new NodeConverter(writer, skipReport);
            var rootSiblingNames = new HashSet<string>();

            // Get root objects in scene order
            GameObject[] roots = scene.GetRootGameObjects();

            foreach (var root in roots)
            {
                ConvertSceneNode(root, ".", rootSiblingNames, nodeConverter, skipReport);
            }

            // Write output
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            relativePath = Path.ChangeExtension(relativePath, ".tscn");
            string destPath = Path.Combine(outputDir, relativePath);
            writer.Write(destPath);

            Debug.Log($"[U2G][INFO] Converted scene: {assetPath}");
        }

        static void ConvertSceneNode(GameObject go, string parentPath, HashSet<string> siblingNames,
            NodeConverter converter, SkipReport skipReport)
        {
            // Check if this is a prefab instance at the root or nested level
            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(go);
            bool isPrefabInstanceRoot = prefabSource != null && PrefabUtility.IsAnyPrefabInstanceRoot(go);

            if (isPrefabInstanceRoot)
            {
                string prefabPath = AssetDatabase.GetAssetPath(prefabSource);
                if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".prefab"))
                {
                    // Instance the prefab
                    converter.ConvertPrefabInstance(go, parentPath, siblingNames, prefabPath);

                    // Apply overrides
                    ApplyPrefabOverrides(go, converter, skipReport);
                    return;
                }

                // If the source is an FBX, handle as FBX instance
                if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                {
                    converter.ConvertHierarchy(go, parentPath, siblingNames);
                    return;
                }
            }

            // Regular node: convert hierarchy normally
            converter.ConvertHierarchy(go, parentPath, siblingNames);
        }

        static void ApplyPrefabOverrides(GameObject instanceRoot, NodeConverter converter, SkipReport skipReport)
        {
            var modifications = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (modifications == null)
                return;

            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
            if (prefabSource == null)
                return;

            // Group material overrides by source Renderer
            var materialOverrides = new Dictionary<Renderer, List<(int index, Material mat)>>();
            // Track child transforms that have overrides
            var childTransformOverrides = new HashSet<Transform>();

            foreach (var mod in modifications)
            {
                if (mod.target == null)
                    continue;

                string propPath = mod.propertyPath;

                bool isTransformOverride = propPath.StartsWith("m_LocalPosition") ||
                                           propPath.StartsWith("m_LocalRotation") ||
                                           propPath.StartsWith("m_LocalScale");
                bool isMaterialOverride = propPath.StartsWith("m_Materials.Array.data[");
                bool isCommonIgnorable = propPath == "m_Name" ||
                                         propPath == "m_RootOrder" ||
                                         propPath == "m_IsActive" ||
                                         propPath.StartsWith("m_Layer") ||
                                         propPath.StartsWith("m_TagString") ||
                                         propPath.StartsWith("m_StaticEditorFlags");

                if (isTransformOverride)
                {
                    // Root transform is baked (read via go.transform in ConvertPrefabInstance).
                    // Child transform overrides need explicit override nodes.
                    var targetTransform = mod.target as Transform;
                    if (targetTransform != null && targetTransform != prefabSource.transform)
                        childTransformOverrides.Add(targetTransform);
                    continue;
                }

                if (isMaterialOverride)
                {
                    var renderer = mod.target as Renderer;
                    if (renderer == null) continue;

                    int startIdx = propPath.IndexOf('[') + 1;
                    int endIdx = propPath.IndexOf(']');
                    if (startIdx <= 0 || endIdx < 0) continue;
                    if (!int.TryParse(propPath.Substring(startIdx, endIdx - startIdx), out int matIndex))
                        continue;

                    Material mat = mod.objectReference as Material;
                    if (mat == null) continue;

                    if (!materialOverrides.TryGetValue(renderer, out var list))
                    {
                        list = new List<(int, Material)>();
                        materialOverrides[renderer] = list;
                    }
                    list.Add((matIndex, mat));
                    continue;
                }

                if (!isCommonIgnorable && (!propPath.StartsWith("m_") || propPath.Contains("Component")))
                {
                    Debug.LogWarning($"[U2G][WARN] Unsupported prefab override: {propPath} on {mod.target.name}");
                }
            }

            // Apply child transform overrides
            foreach (var sourceTransform in childTransformOverrides)
            {
                string relativePath = GetRelativePath(prefabSource.transform, sourceTransform);
                if (relativePath == null)
                {
                    Debug.LogWarning($"[U2G][WARN] Could not resolve path for transform override on '{sourceTransform.name}'.");
                    continue;
                }

                Transform instanceChild = FindInstanceChild(instanceRoot.transform, relativePath);
                if (instanceChild == null)
                {
                    Debug.LogWarning($"[U2G][WARN] Could not find instance child at '{relativePath}' — skipping transform override.");
                    continue;
                }

                converter.WritePrefabInstanceTransformOverride(instanceRoot, relativePath, instanceChild);
            }

            // Apply material overrides
            foreach (var kvp in materialOverrides)
            {
                converter.WritePrefabInstanceMaterialOverride(instanceRoot, prefabSource, kvp.Key, kvp.Value);
            }
        }

        static string GetRelativePath(Transform root, Transform target)
        {
            var parts = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            if (current != root) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform FindInstanceChild(Transform instanceRoot, string relativePath)
        {
            string[] parts = relativePath.Split('/');
            Transform current = instanceRoot;
            foreach (string name in parts)
            {
                current = current.Find(name);
                if (current == null) return null;
            }
            return current;
        }
    }
}
