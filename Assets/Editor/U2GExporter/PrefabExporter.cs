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

            var nodeConverter = new NodeConverter(writer, skipReport);

            // Convert the prefab root as the scene root node.
            // Returns true if root is FBX-backed (children are part of the FBX instance).
            bool rootIsFbxInstance = ConvertPrefabRoot(prefabRoot, sceneName, writer, skipReport);
            writer.AddBlankLine();

            if (rootIsFbxInstance)
            {
                // Children are part of the FBX instance — don't re-process them.
                // Instead, scan for material overrides on descendants.
                WriteFbxDescendantMaterialOverrides(prefabRoot.transform, ".", writer);
            }
            else
            {
                // Traverse children — flatten nested prefabs one level deep
                var siblingNames = new HashSet<string>();
                ConvertPrefabChildren(prefabRoot, ".", siblingNames, nodeConverter, skipReport, 0);
            }

            // Write output
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            relativePath = Path.ChangeExtension(relativePath, ".tscn");
            string destPath = Path.Combine(outputDir, relativePath);
            writer.Write(destPath);

            Debug.Log($"[U2G][INFO] Converted prefab: {assetPath}");
        }

        /// <summary>
        /// Converts the prefab root as the scene root node.
        /// Returns true if the root is an FBX instance (children are part of the FBX).
        /// </summary>
        static bool ConvertPrefabRoot(GameObject root, string sceneName, TscnWriter writer, SkipReport skipReport)
        {
            // Check if any node in the hierarchy is FBX-backed.
            // For FBX prefabs, the mesh is often on a child (e.g., Suzanne) not the root.
            string fbxPath = FindFbxSource(root);

            if (fbxPath != null)
            {
                // FBX-backed prefab: instance the FBX as the scene root
                string resPath = PathUtil.FbxToGodotResPath(fbxPath);
                string extId = writer.AddExtResource("PackedScene", resPath);
                writer.AddRootInstanceNode(sceneName, extId);
                return true;
            }

            // Non-FBX root
            var light = root.GetComponent<Light>();
            var camera = root.GetComponent<Camera>();

            if (light != null)
            {
                var lightData = LightExporter.Extract(light);
                writer.AddRootNode(sceneName, lightData.IsValid ? lightData.GodotType : "Node3D");
                if (lightData.IsValid)
                    LightExporter.WriteProperties(writer, lightData);
            }
            else if (camera != null)
            {
                writer.AddRootNode(sceneName, "Camera3D");
                CameraExporter.WriteProperties(writer, camera);
            }
            else
            {
                writer.AddRootNode(sceneName, "Node3D");
            }

            var meshFilter = root.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                string meshInfo = meshFilter.sharedMesh.name;
                Debug.LogWarning($"[U2G][WARN] Prefab root '{root.name}' has non-FBX mesh: {meshInfo}. Created Node3D.");
                skipReport.Add("Unsupported Mesh Sources", $"{root.name}: {meshInfo}");
            }

            return false;
        }

        /// <summary>
        /// Finds the FBX source for this prefab by checking the root and its descendants.
        /// </summary>
        static string FindFbxSource(GameObject root)
        {
            // Check root first
            string fbxPath = GetFbxPathFromMesh(root);
            if (fbxPath != null) return fbxPath;

            // Check children (FBX prefabs often have mesh on a child node)
            for (int i = 0; i < root.transform.childCount; i++)
            {
                fbxPath = GetFbxPathFromMesh(root.transform.GetChild(i).gameObject);
                if (fbxPath != null) return fbxPath;
            }

            return null;
        }

        static string GetFbxPathFromMesh(GameObject go)
        {
            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return null;

            string meshAssetPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
            if (!string.IsNullOrEmpty(meshAssetPath) && meshAssetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                return meshAssetPath;

            return null;
        }

        /// <summary>
        /// Walks descendants of an FBX instance root and writes material override nodes
        /// for any MeshRenderer that has materials assigned.
        /// </summary>
        static void WriteFbxDescendantMaterialOverrides(Transform parent, string parentPath, TscnWriter writer)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                string childPath = parentPath == "." ? child.name : parentPath + "/" + child.name;

                var renderer = child.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterials != null)
                {
                    bool hasOverrides = false;
                    for (int m = 0; m < renderer.sharedMaterials.Length; m++)
                    {
                        Material mat = renderer.sharedMaterials[m];
                        if (mat == null) continue;

                        string matPath = AssetDatabase.GetAssetPath(mat);
                        if (string.IsNullOrEmpty(matPath) || matPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                            continue; // Skip FBX-embedded default materials

                        if (!hasOverrides)
                        {
                            writer.AddOverrideNode(child.name, parentPath);
                            hasOverrides = true;
                        }

                        string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                        string matExtId = writer.AddExtResource("Material", matResPath);
                        writer.AddPropertyExtResource($"surface_material_override/{m}", matExtId);
                    }

                    if (hasOverrides)
                        writer.AddBlankLine();
                }

                // Recurse into deeper children
                WriteFbxDescendantMaterialOverrides(child, childPath, writer);
            }
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

                if (isNestedPrefab)
                {
                    if (nestingDepth > 0)
                    {
                        Debug.LogWarning($"[U2G][WARN] Nested prefab depth > 1 detected at '{childGo.name}' " +
                            $"inside prefab. Flattening, but deep nesting may lose fidelity.");
                    }

                    // Scan descendants for even deeper nesting before flattening
                    WarnDeeperNesting(childGo, nestingDepth + 1);

                    // Flatten: ConvertHierarchy emits the full subtree inline
                    converter.ConvertHierarchy(childGo, parentPath, siblingNames);
                }
                else
                {
                    converter.ConvertHierarchy(childGo, parentPath, siblingNames);
                }
            }
        }

        static void WarnDeeperNesting(GameObject go, int depth)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(child);
                if (source != null && PrefabUtility.IsAnyPrefabInstanceRoot(child))
                {
                    Debug.LogWarning($"[U2G][WARN] Nested prefab depth > 1 detected at '{child.name}' " +
                        $"inside prefab. Flattening, but deep nesting may lose fidelity.");
                    WarnDeeperNesting(child, depth + 1);
                }
                else
                {
                    WarnDeeperNesting(child, depth);
                }
            }
        }
    }
}
