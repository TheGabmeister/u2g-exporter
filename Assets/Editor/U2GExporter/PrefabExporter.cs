using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace U2GExporter
{
    public static class PrefabExporter
    {
        public static void Export(string assetPath, string outputDir, SkipReport skipReport)
        {
            var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabRoot == null)
            {
                Debug.LogError($"[U2G][ERROR] Failed to load prefab: {assetPath}");
                return;
            }

            var writer = new TscnWriter();
            string sceneName = Path.GetFileNameWithoutExtension(assetPath);

            // Root node
            writer.AddRootNode(sceneName, "Node3D");
            writer.AddBlankLine();

            var nodeConverter = new NodeConverter(writer, skipReport);
            var siblingNames = new HashSet<string>();

            // Traverse prefab hierarchy — flatten nested prefabs one level deep
            ConvertPrefabChildren(prefabRoot, ".", siblingNames, nodeConverter, skipReport, 0);

            // Write output
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            relativePath = Path.ChangeExtension(relativePath, ".tscn");
            string destPath = Path.Combine(outputDir, relativePath);
            writer.Write(destPath);

            Debug.Log($"[U2G][INFO] Converted prefab: {assetPath}");
        }

        static void ConvertPrefabChildren(GameObject go, string parentPath, HashSet<string> siblingNames,
            NodeConverter converter, SkipReport skipReport, int nestingDepth)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                Transform child = go.transform.GetChild(i);
                GameObject childGo = child.gameObject;

                // Check if this child is a nested prefab instance
                var source = PrefabUtility.GetCorrespondingObjectFromSource(childGo);
                bool isNestedPrefab = source != null && PrefabUtility.IsAnyPrefabInstanceRoot(childGo);

                if (isNestedPrefab && nestingDepth == 0)
                {
                    // One level deep: flatten the nested prefab's hierarchy
                    converter.ConvertHierarchy(childGo, parentPath, siblingNames);
                }
                else if (isNestedPrefab && nestingDepth > 0)
                {
                    // Deeper nesting: warn and still flatten
                    Debug.LogWarning($"[U2G][WARN] Nested prefab depth > 1 detected at '{childGo.name}' " +
                        $"inside prefab. Flattening, but deep nesting may lose fidelity.");
                    converter.ConvertHierarchy(childGo, parentPath, siblingNames);
                }
                else
                {
                    converter.ConvertHierarchy(childGo, parentPath, siblingNames);
                }
            }
        }
    }
}
