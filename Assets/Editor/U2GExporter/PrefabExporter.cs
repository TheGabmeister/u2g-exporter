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
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[U2G][ERROR] Failed to load prefab: {assetPath}");
                return;
            }

            // Instantiate a temporary copy to get the full hierarchy with
            // resolved children and material overrides (LoadAssetAtPath alone
            // doesn't resolve FBX children on prefab variants).
            var instance = Object.Instantiate(prefabAsset);
            instance.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                var writer = new TscnWriter();
                string sceneName = Path.GetFileNameWithoutExtension(assetPath);

                var nodeConverter = new NodeConverter(writer, skipReport);

                // Use the asset (not instance) for FBX detection — AssetDatabase
                // only works on assets, not instantiated clones.
                string fbxPath = FindFbxSource(prefabAsset);

                if (fbxPath != null)
                {
                    // FBX-backed prefab: instance the FBX as the scene root
                    string resPath = PathUtil.FbxToGodotResPath(fbxPath);
                    string extId = writer.AddExtResource("PackedScene", resPath);
                    writer.AddRootInstanceNode(sceneName, extId);

                    // Handedness rotation is handled at the FBX file level
                    // (patched Lcl Rotation), so use the standard transform.
                    float[] rootTransform = CoordConvert.ConvertTransform(instance.transform);
                    if (rootTransform != null)
                        writer.AddPropertyTransform(rootTransform);

                    writer.AddBlankLine();

                    // Material overrides: Godot's FBX import wraps all Model nodes
                    // under a RootNode, so every mesh (including the root) is a child.
                    // Write override nodes for the root renderer and all children.
                    WriteFbxAllMeshOverrides(instance, fbxPath, writer);
                }
                else
                {
                    // Non-FBX prefab
                    ConvertNonFbxPrefabRoot(instance, sceneName, writer, skipReport);
                    writer.AddBlankLine();

                    // Traverse children — flatten nested prefabs one level deep
                    var siblingNames = new HashSet<string>();
                    ConvertPrefabChildren(instance, ".", siblingNames, nodeConverter, skipReport, 0);
                }

                // Write output
                string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
                relativePath = Path.ChangeExtension(relativePath, ".tscn");
                string destPath = Path.Combine(outputDir, relativePath);
                writer.Write(destPath);

                Debug.Log($"[U2G][INFO] Converted prefab: {assetPath}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        static void ConvertNonFbxPrefabRoot(GameObject root, string sceneName, TscnWriter writer, SkipReport skipReport)
        {
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
        }

        /// <summary>
        /// Finds the FBX source for this prefab. Checks:
        /// 1. The prefab's corresponding source object (for prefab variants of FBX models)
        /// 2. MeshFilter references on root and children
        /// </summary>
        static string FindFbxSource(GameObject prefabAsset)
        {
            // Check if this prefab is a variant of an FBX model
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset);
            if (source != null)
            {
                string sourcePath = AssetDatabase.GetAssetPath(source);
                if (!string.IsNullOrEmpty(sourcePath) && sourcePath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    return sourcePath;
            }

            // Check MeshFilter references on root and children
            var meshFilters = prefabAsset.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh == null) continue;
                string meshAssetPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
                if (!string.IsNullOrEmpty(meshAssetPath) && meshAssetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    return meshAssetPath;
            }

            return null;
        }

        /// <summary>
        /// Writes material override nodes for all mesh renderers in an FBX instance.
        /// Godot's FBX import wraps all Model nodes under a RootNode, so every mesh
        /// (including what Unity treats as the "root") is a child in Godot's scene tree.
        /// For single-mesh FBX where Unity puts the mesh on the root GameObject, the
        /// Godot child node name is resolved via FbxExporter.GetNodeNames.
        /// </summary>
        static void WriteFbxAllMeshOverrides(GameObject instance, string fbxPath, TscnWriter writer)
        {
            // Root renderer — in Godot this is a child node, not the instance root.
            // Resolve the FBX node name from the FBX asset (not the clone, which
            // has "(Clone)" appended, nor the prefab, which has a different name).
            var rootRenderer = instance.GetComponent<MeshRenderer>();
            if (rootRenderer != null && rootRenderer.sharedMaterials != null)
            {
                var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                string targetName = fbxAsset != null ? fbxAsset.name : instance.name;

                // For single-mesh FBX, Godot names the child after the mesh, not
                // the root GameObject. Use GetNodeNames to find it.
                if (instance.transform.childCount == 0)
                {
                    var fbxNodeNames = FbxExporter.GetNodeNames(fbxPath);
                    if (fbxNodeNames.Count > 0)
                        targetName = fbxNodeNames[0];
                }

                WriteMaterialOverrideNode(rootRenderer, targetName, ".", writer);
            }

            // Child renderers
            for (int i = 0; i < instance.transform.childCount; i++)
            {
                WriteFbxChildOverrideNode(instance.transform.GetChild(i).gameObject, fbxPath, ".", writer);
            }
        }

        /// <summary>
        /// Writes a material override node for a single child of an FBX instance,
        /// then recurses into grandchildren.
        /// </summary>
        static void WriteFbxChildOverrideNode(GameObject child, string fbxPath, string parentPath, TscnWriter writer)
        {
            WriteMaterialOverrideNode(child.GetComponent<MeshRenderer>(), child.name, parentPath, writer);

            // Recurse into grandchildren
            string childPath = parentPath == "." ? child.name : parentPath + "/" + child.name;
            for (int i = 0; i < child.transform.childCount; i++)
            {
                WriteFbxChildOverrideNode(child.transform.GetChild(i).gameObject, fbxPath, childPath, writer);
            }
        }

        /// <summary>
        /// Writes a single material override node for a renderer, if it has non-FBX materials.
        /// </summary>
        static void WriteMaterialOverrideNode(MeshRenderer renderer, string nodeName, string parentPath, TscnWriter writer)
        {
            if (renderer == null || renderer.sharedMaterials == null) return;

            bool hasOverrides = false;
            for (int m = 0; m < renderer.sharedMaterials.Length; m++)
            {
                Material mat = renderer.sharedMaterials[m];
                if (mat == null) continue;
                string matPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(matPath)) continue;
                if (matPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;

                if (!hasOverrides)
                {
                    writer.AddOverrideNode(nodeName, parentPath);
                    hasOverrides = true;
                }

                string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                string matExtId = writer.AddExtResource("Material", matResPath);
                writer.AddPropertyExtResource($"surface_material_override/{m}", matExtId);
            }
            if (hasOverrides)
                writer.AddBlankLine();
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
