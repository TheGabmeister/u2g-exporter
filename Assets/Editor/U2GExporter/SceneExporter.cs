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

            foreach (var mod in modifications)
            {
                if (mod.target == null)
                    continue;

                string propPath = mod.propertyPath;

                // We handle transform and material overrides; warn on others
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

                if (!isTransformOverride && !isMaterialOverride && !isCommonIgnorable)
                {
                    // Only warn for non-trivial overrides
                    if (!propPath.StartsWith("m_") || propPath.Contains("Component"))
                    {
                        Debug.LogWarning($"[U2G][WARN] Unsupported prefab override: {propPath} on {mod.target.name}");
                    }
                }

                // Note: Transform and material overrides are already baked into the
                // prefab instance's transform (read via go.transform) and material arrays
                // (read via MeshRenderer.sharedMaterials). The instance=ExtResource approach
                // in ConvertPrefabInstance already writes the instance's world-local transform.
                // For V1, we rely on the transform being read directly from the instance
                // and don't need to re-apply modifications separately.
            }
        }
    }
}
