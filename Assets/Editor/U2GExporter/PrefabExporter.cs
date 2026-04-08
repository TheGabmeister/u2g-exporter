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
                Debug.Log($"[U2G][DEBUG] FindFbxSource result: '{fbxPath ?? "null"}'");

                if (fbxPath != null)
                {
                    // FBX-backed prefab: instance the FBX as the scene root
                    string resPath = PathUtil.FbxToGodotResPath(fbxPath);
                    string extId = writer.AddExtResource("PackedScene", resPath);
                    writer.AddRootInstanceNode(sceneName, extId);
                    writer.AddBlankLine();

                    // Use the instantiated copy for material traversal (has resolved children)
                    Debug.Log($"[U2G][DEBUG] Instance childCount={instance.transform.childCount}");
                    var allRenderers = instance.GetComponentsInChildren<MeshRenderer>(true);
                    Debug.Log($"[U2G][DEBUG] Total MeshRenderers found: {allRenderers.Length}");
                    foreach (var r in allRenderers)
                    {
                        Debug.Log($"[U2G][DEBUG]   Renderer on '{r.gameObject.name}', isRoot={r.transform == instance.transform}, matCount={r.sharedMaterials?.Length ?? 0}");
                        if (r.sharedMaterials != null)
                        {
                            for (int dm = 0; dm < r.sharedMaterials.Length; dm++)
                            {
                                var dmat = r.sharedMaterials[dm];
                                string dpath = dmat != null ? AssetDatabase.GetAssetPath(dmat) : "(null mat)";
                                bool isFbxMat = dpath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
                                Debug.Log($"[U2G][DEBUG]     mat[{dm}] name='{dmat?.name}' path='{dpath}' isFbxEmbedded={isFbxMat}");
                            }
                        }
                    }
                    WriteFbxInstanceMaterialOverrides(instance, fbxPath, writer);
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
        /// Writes material override nodes for an FBX-backed prefab.
        /// Checks the root's MeshRenderer and all descendants. Uses FBX node names
        /// to target the correct child node in the Godot FBX instance.
        /// </summary>
        static void WriteFbxInstanceMaterialOverrides(GameObject instance, string fbxPath, TscnWriter writer)
        {
            // Load the FBX to get the child node names (Godot's FBX structure)
            var fbxNodeNames = FbxExporter.GetNodeNames(fbxPath);
            string fbxChildName = fbxNodeNames.Count > 0 ? fbxNodeNames[0] : null;

            // Collect all MeshRenderers in the prefab instance
            var renderers = instance.GetComponentsInChildren<MeshRenderer>(true);

            foreach (var renderer in renderers)
            {
                if (renderer.sharedMaterials == null) continue;

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
                        // Determine the override target node name.
                        // If the renderer is on the root, target the FBX's first child node.
                        // If it's on a child, use the child's own name.
                        string targetName = (renderer.transform == instance.transform)
                            ? fbxChildName
                            : renderer.transform.name;

                        if (targetName == null)
                        {
                            Debug.LogWarning($"[U2G][WARN] Cannot determine FBX child node for material override on '{renderer.name}'.");
                            break;
                        }

                        // Compute parent path for the override node
                        string overrideParent = GetOverrideParentPath(instance.transform, renderer.transform);
                        writer.AddOverrideNode(targetName, overrideParent);
                        hasOverrides = true;
                    }

                    string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                    string matExtId = writer.AddExtResource("Material", matResPath);
                    writer.AddPropertyExtResource($"surface_material_override/{m}", matExtId);
                }

                if (hasOverrides)
                    writer.AddBlankLine();
            }
        }

        /// <summary>
        /// Computes the Godot parent path for an override node within an FBX instance.
        /// If the renderer is on the root, parent is ".".
        /// If it's on a child, parent is the path from root to the renderer's parent.
        /// </summary>
        static string GetOverrideParentPath(Transform root, Transform target)
        {
            if (target == root)
                return ".";

            // Build path from root to target's parent
            var parts = new List<string>();
            Transform current = target.parent;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return parts.Count == 0 ? "." : string.Join("/", parts);
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
