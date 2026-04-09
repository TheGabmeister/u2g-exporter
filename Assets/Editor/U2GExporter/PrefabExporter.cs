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

                    // Material overrides: for multi-mesh FBX the root is a MeshInstance3D
                    // in Godot, so write root materials directly on the instance node,
                    // then child overrides as separate nodes.
                    // For single-mesh FBX, write a child override targeting the mesh node.
                    bool isMultiMesh = HasFbxChildren(instance, fbxPath);
                    if (isMultiMesh)
                        WriteFbxRootMaterials(instance, fbxPath, writer);

                    writer.AddBlankLine();

                    if (isMultiMesh)
                        WriteFbxChildrenOverrides(instance, fbxPath, writer);
                    else
                        WriteSingleMeshRootOverride(instance, fbxPath, writer);
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
        /// Checks if any direct children of a GameObject reference meshes from the
        /// specified FBX file (i.e., this is a multi-mesh FBX).
        /// </summary>
        static bool HasFbxChildren(GameObject instance, string fbxPath)
        {
            for (int i = 0; i < instance.transform.childCount; i++)
            {
                var mf = instance.transform.GetChild(i).GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    string meshAssetPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (!string.IsNullOrEmpty(meshAssetPath) &&
                        meshAssetPath.Equals(fbxPath, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Writes material override properties directly on the root instance node.
        /// Used for multi-mesh FBX where the root FBX node has a mesh (MeshInstance3D in Godot).
        /// </summary>
        static void WriteFbxRootMaterials(GameObject instance, string fbxPath, TscnWriter writer)
        {
            var renderer = instance.GetComponent<MeshRenderer>();
            if (renderer == null || renderer.sharedMaterials == null) return;

            for (int m = 0; m < renderer.sharedMaterials.Length; m++)
            {
                Material mat = renderer.sharedMaterials[m];
                if (mat == null) continue;
                string matPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(matPath)) continue;
                if (matPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;

                string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                string matExtId = writer.AddExtResource("Material", matResPath);
                writer.AddPropertyExtResource($"surface_material_override/{m}", matExtId);
            }
        }

        /// <summary>
        /// Writes material override nodes for child renderers in a multi-mesh FBX instance.
        /// Each child's name is used to target the corresponding FBX node in Godot.
        /// </summary>
        static void WriteFbxChildrenOverrides(GameObject instance, string fbxPath, TscnWriter writer)
        {
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
            var renderer = child.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterials != null)
            {
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

            // Recurse into grandchildren
            string childPath = parentPath == "." ? child.name : parentPath + "/" + child.name;
            for (int i = 0; i < child.transform.childCount; i++)
            {
                WriteFbxChildOverrideNode(child.transform.GetChild(i).gameObject, fbxPath, childPath, writer);
            }
        }

        /// <summary>
        /// Writes a material override for a single-mesh FBX where the mesh is on a child
        /// node. Uses GetNodeNames to find the child node name in Godot's FBX import.
        /// </summary>
        static void WriteSingleMeshRootOverride(GameObject instance, string fbxPath, TscnWriter writer)
        {
            var renderer = instance.GetComponent<MeshRenderer>();
            if (renderer == null || renderer.sharedMaterials == null) return;

            var fbxNodeNames = FbxExporter.GetNodeNames(fbxPath);
            string targetName = fbxNodeNames.Count > 0 ? fbxNodeNames[0] : null;
            if (targetName == null) return;

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
                    writer.AddOverrideNode(targetName, ".");
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
