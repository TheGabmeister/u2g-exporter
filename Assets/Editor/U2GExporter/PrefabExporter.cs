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

            // Convert the prefab root as the scene root node
            ConvertPrefabRoot(prefabRoot, sceneName, writer, nodeConverter, skipReport);
            writer.AddBlankLine();

            // Traverse children — flatten nested prefabs one level deep
            var siblingNames = new HashSet<string>();
            ConvertPrefabChildren(prefabRoot, ".", siblingNames, nodeConverter, skipReport, 0);

            // Write output
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            relativePath = Path.ChangeExtension(relativePath, ".tscn");
            string destPath = Path.Combine(outputDir, relativePath);
            writer.Write(destPath);

            Debug.Log($"[U2G][INFO] Converted prefab: {assetPath}");
        }

        static void ConvertPrefabRoot(GameObject root, string sceneName, TscnWriter writer,
            NodeConverter nodeConverter, SkipReport skipReport)
        {
            var meshFilter = root.GetComponent<MeshFilter>();
            var meshRenderer = root.GetComponent<MeshRenderer>();
            bool hasMesh = meshFilter != null && meshRenderer != null && meshFilter.sharedMesh != null;
            string fbxPath = null;

            if (hasMesh)
            {
                string meshAssetPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                if (!string.IsNullOrEmpty(meshAssetPath) && meshAssetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPath = meshAssetPath;
            }

            if (hasMesh && fbxPath != null)
            {
                // FBX-backed root: instance the FBX as the scene root
                string resPath = PathUtil.FbxToGodotResPath(fbxPath);
                string extId = writer.AddExtResource("PackedScene", resPath);
                writer.AddRootInstanceNode(sceneName, extId);

                // Material overrides on the FBX child nodes
                if (meshRenderer.sharedMaterials != null && meshRenderer.sharedMaterials.Length > 0)
                {
                    var nodeNames = FbxExporter.GetNodeNames(fbxPath);
                    if (nodeNames.Count > 0)
                    {
                        string targetNodeName = nodeNames[0];
                        bool hasOverrides = false;

                        for (int i = 0; i < meshRenderer.sharedMaterials.Length; i++)
                        {
                            Material mat = meshRenderer.sharedMaterials[i];
                            if (mat == null) continue;
                            string matPath = AssetDatabase.GetAssetPath(mat);
                            if (string.IsNullOrEmpty(matPath)) continue;

                            if (!hasOverrides)
                            {
                                writer.AddOverrideNode(targetNodeName, ".");
                                hasOverrides = true;
                            }

                            string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                            string matExtId = writer.AddExtResource("Material", matResPath);
                            writer.AddPropertyExtResource($"surface_material_override/{i}", matExtId);
                        }

                        if (hasOverrides)
                            writer.AddBlankLine();
                    }
                }
            }
            else
            {
                // Non-FBX or no mesh: plain Node3D root
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

                if (hasMesh && fbxPath == null)
                {
                    string meshInfo = meshFilter.sharedMesh.name;
                    Debug.LogWarning($"[U2G][WARN] Prefab root '{root.name}' has non-FBX mesh: {meshInfo}. Created Node3D.");
                    skipReport.Add("Unsupported Mesh Sources", $"{root.name}: {meshInfo}");
                }
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
