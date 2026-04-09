using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace U2GExporter
{
    /// <summary>
    /// Shared hierarchy traversal and node emission logic used by both SceneExporter and PrefabExporter.
    /// </summary>
    public class NodeConverter
    {
        readonly TscnWriter _writer;
        readonly SkipReport _skipReport;
        readonly HashSet<string> _fbxMultiMeshWarned = new HashSet<string>();

        // Track which FBX files have which sub-meshes referenced, for multi-mesh detection
        readonly Dictionary<string, HashSet<string>> _fbxMeshReferences = new Dictionary<string, HashSet<string>>();

        // Source object → emitted Godot node path mapping for override resolution
        readonly Dictionary<int, string> _objectToNodePath = new Dictionary<int, string>();

        public NodeConverter(TscnWriter writer, SkipReport skipReport)
        {
            _writer = writer;
            _skipReport = skipReport;
        }

        public Dictionary<int, string> ObjectToNodePathMap => _objectToNodePath;

        /// <summary>
        /// Recursively converts a Unity GameObject and its children into Godot .tscn nodes.
        /// </summary>
        public void ConvertHierarchy(GameObject go, string parentPath, HashSet<string> siblingNames)
        {
            string nodeName = GetUniqueName(go.name, siblingNames);
            string currentPath = parentPath == "." ? nodeName : parentPath + "/" + nodeName;

            // Record object → path mapping
            _objectToNodePath[go.GetInstanceID()] = currentPath;
            _objectToNodePath[go.transform.GetInstanceID()] = currentPath;

            bool isInactive = !go.activeSelf;

            // Determine node type from components
            var meshFilter = go.GetComponent<MeshFilter>();
            var meshRenderer = go.GetComponent<MeshRenderer>();
            var light = go.GetComponent<Light>();
            var camera = go.GetComponent<Camera>();

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
                // FBX-backed mesh: instance the FBX
                TrackFbxMeshReference(fbxPath, meshFilter.sharedMesh.name);

                string resPath = PathUtil.FbxToGodotResPath(fbxPath);
                string extId = _writer.AddExtResource("PackedScene", resPath);
                _writer.AddInstanceNode(nodeName, parentPath, extId);

                // Handedness rotation is handled at the FBX file level
                // (patched Lcl Rotation), so use the standard transform.
                float[] t = CoordConvert.ConvertTransform(go.transform);
                if (t != null)
                    _writer.AddPropertyTransform(t);

                // Visibility
                if (isInactive || (meshRenderer != null && !meshRenderer.enabled))
                    _writer.AddPropertyBool("visible", false);

                // Material overrides: Godot wraps all FBX Model nodes under a
                // RootNode (Node3D), so every mesh — including what Unity treats as
                // the "root" — is a child.  Always write a child override node.
                WriteFbxMaterialOverrides(fbxPath, meshRenderer, nodeName, parentPath);
            }
            else if (hasMesh && fbxPath == null)
            {
                // Non-FBX mesh: placeholder Node3D
                string meshAssetPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                _writer.AddNode(nodeName, "Node3D", parentPath);

                float[] t = CoordConvert.ConvertTransform(go.transform);
                _writer.AddPropertyTransform(t);

                if (isInactive)
                    _writer.AddPropertyBool("visible", false);

                string meshInfo = meshFilter.sharedMesh.name;
                if (!string.IsNullOrEmpty(meshAssetPath))
                    meshInfo += $" (source: {meshAssetPath})";

                Debug.LogWarning($"[U2G][WARN] Unsupported mesh source for '{go.name}': {meshInfo}. Created Node3D placeholder.");
                _skipReport.Add("Unsupported Mesh Sources", $"{go.name}: {meshInfo}");
            }
            else if (light != null)
            {
                var lightData = LightExporter.Extract(light);
                if (lightData.IsValid)
                {
                    _writer.AddNode(nodeName, lightData.GodotType, parentPath);

                    float[] t = CoordConvert.ConvertTransform(go.transform);
                    _writer.AddPropertyTransform(t);

                    if (isInactive || !light.enabled)
                        _writer.AddPropertyBool("visible", false);

                    LightExporter.WriteProperties(_writer, lightData);
                }
                else
                {
                    _writer.AddNode(nodeName, "Node3D", parentPath);
                    float[] t = CoordConvert.ConvertTransform(go.transform);
                    _writer.AddPropertyTransform(t);
                    if (isInactive)
                        _writer.AddPropertyBool("visible", false);
                }
            }
            else if (camera != null)
            {
                _writer.AddNode(nodeName, "Camera3D", parentPath);

                float[] t = CoordConvert.ConvertTransform(go.transform);
                _writer.AddPropertyTransform(t);

                if (isInactive || !camera.enabled)
                {
                    _writer.AddPropertyBool("visible", false);
                    _writer.AddPropertyBool("current", false);
                }

                CameraExporter.WriteProperties(_writer, camera);
            }
            else
            {
                // Empty / grouping node
                _writer.AddNode(nodeName, "Node3D", parentPath);

                float[] t = CoordConvert.ConvertTransform(go.transform);
                _writer.AddPropertyTransform(t);

                if (isInactive)
                    _writer.AddPropertyBool("visible", false);
            }

            _writer.AddBlankLine();

            // Recurse into children.
            // If this node is an FBX instance, children from the same FBX are part of
            // the instance and should only get material override nodes, not separate
            // FBX instances. Other children are processed normally.
            var childSiblingNames = new HashSet<string>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                Transform child = go.transform.GetChild(i);

                if (hasMesh && fbxPath != null && IsPartOfSameFbx(child.gameObject, fbxPath))
                {
                    WriteFbxChildMaterialOverrides(child.gameObject, fbxPath, currentPath);
                    continue;
                }

                ConvertHierarchy(child.gameObject, currentPath, childSiblingNames);
            }
        }

        /// <summary>
        /// Converts a prefab-instanced node (writes instance=ExtResource).
        /// </summary>
        public void ConvertPrefabInstance(GameObject go, string parentPath, HashSet<string> siblingNames,
            string prefabAssetPath)
        {
            string nodeName = GetUniqueName(go.name, siblingNames);
            string currentPath = parentPath == "." ? nodeName : parentPath + "/" + nodeName;

            _objectToNodePath[go.GetInstanceID()] = currentPath;
            _objectToNodePath[go.transform.GetInstanceID()] = currentPath;

            string resPath = PathUtil.PrefabToGodotResPath(prefabAssetPath);
            string extId = _writer.AddExtResource("PackedScene", resPath);
            _writer.AddInstanceNode(nodeName, parentPath, extId);

            float[] t = CoordConvert.ConvertTransform(go.transform);
            _writer.AddPropertyTransform(t);

            if (!go.activeSelf)
                _writer.AddPropertyBool("visible", false);

            _writer.AddBlankLine();
        }

        public void AddBlankLine()
        {
            _writer.AddBlankLine();
        }

        /// <summary>
        /// Writes a transform override node for a child within a prefab instance.
        /// </summary>
        public void WritePrefabInstanceTransformOverride(GameObject instanceRoot, string childRelativePath, Transform instanceChild)
        {
            if (!_objectToNodePath.TryGetValue(instanceRoot.GetInstanceID(), out string instancePath))
            {
                Debug.LogWarning($"[U2G][WARN] Cannot resolve instance path for '{instanceRoot.name}' — skipping transform override.");
                return;
            }

            SplitLastSegment(childRelativePath, out string parentSuffix, out string nodeName);
            string overrideParent = string.IsNullOrEmpty(parentSuffix) ? instancePath : instancePath + "/" + parentSuffix;

            _writer.AddOverrideNode(nodeName, overrideParent);
            float[] t = CoordConvert.ConvertTransform(instanceChild);
            _writer.AddPropertyTransform(t);
            _writer.AddBlankLine();
        }

        /// <summary>
        /// Writes material override nodes for a child within a prefab instance.
        /// For FBX-backed meshes, targets the FBX child node. Non-FBX meshes are warned and skipped.
        /// </summary>
        public void WritePrefabInstanceMaterialOverride(GameObject instanceRoot, GameObject prefabSourceRoot,
            Renderer sourceRenderer, List<(int index, Material mat)> overrides)
        {
            if (!_objectToNodePath.TryGetValue(instanceRoot.GetInstanceID(), out string instancePath))
            {
                Debug.LogWarning($"[U2G][WARN] Cannot resolve instance path for '{instanceRoot.name}' — skipping material override.");
                return;
            }

            string childRelativePath = GetRelativePath(prefabSourceRoot.transform, sourceRenderer.transform);
            if (childRelativePath == null)
            {
                Debug.LogWarning($"[U2G][WARN] Could not resolve path for material override on '{sourceRenderer.name}'.");
                return;
            }

            // Check if the mesh is FBX-backed
            var meshFilter = sourceRenderer.GetComponent<MeshFilter>();
            string fbxPath = null;
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                string meshAssetPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                if (!string.IsNullOrEmpty(meshAssetPath) && meshAssetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    fbxPath = meshAssetPath;
            }

            if (fbxPath == null)
            {
                Debug.LogWarning($"[U2G][WARN] Material override on non-FBX mesh '{sourceRenderer.name}' in prefab instance — cannot apply.");
                return;
            }

            var nodeNames = FbxExporter.GetNodeNames(fbxPath);
            if (nodeNames.Count == 0)
            {
                Debug.LogWarning($"[U2G][WARN] No FBX child nodes in '{fbxPath}' — skipping material override.");
                return;
            }

            string targetNodeName = nodeNames[0];
            string overrideParent = string.IsNullOrEmpty(childRelativePath) ? instancePath : instancePath + "/" + childRelativePath;

            _writer.AddOverrideNode(targetNodeName, overrideParent);

            foreach (var (index, mat) in overrides)
            {
                string matPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(matPath)) continue;
                string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                string matExtId = _writer.AddExtResource("Material", matResPath);
                _writer.AddPropertyExtResource($"surface_material_override/{index}", matExtId);
            }

            _writer.AddBlankLine();
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

        static void SplitLastSegment(string path, out string parentPart, out string namePart)
        {
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                namePart = path.Substring(lastSlash + 1);
                parentPart = path.Substring(0, lastSlash);
            }
            else
            {
                namePart = path;
                parentPart = null;
            }
        }

        /// <summary>
        /// Checks if a GameObject's mesh is from the specified FBX file.
        /// </summary>
        static bool IsPartOfSameFbx(GameObject go, string fbxPath)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            string meshAssetPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
            return !string.IsNullOrEmpty(meshAssetPath) &&
                   meshAssetPath.Equals(fbxPath, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Writes material override nodes for a child of an FBX instance, using the child's
        /// name to target the corresponding node inside the FBX. Recurses into grandchildren
        /// that are also part of the same FBX.
        /// </summary>
        void WriteFbxChildMaterialOverrides(GameObject child, string fbxPath, string fbxInstancePath)
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
                        _writer.AddOverrideNode(child.name, fbxInstancePath);
                        hasOverrides = true;
                    }

                    string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                    string matExtId = _writer.AddExtResource("Material", matResPath);
                    _writer.AddPropertyExtResource($"surface_material_override/{m}", matExtId);
                }
                if (hasOverrides)
                    _writer.AddBlankLine();
            }

            // Recurse into grandchildren that are also part of the same FBX
            string childPath = fbxInstancePath + "/" + child.name;
            for (int i = 0; i < child.transform.childCount; i++)
            {
                var grandchild = child.transform.GetChild(i).gameObject;
                if (IsPartOfSameFbx(grandchild, fbxPath))
                    WriteFbxChildMaterialOverrides(grandchild, fbxPath, childPath);
            }
        }

        void WriteFbxMaterialOverrides(string fbxPath, MeshRenderer renderer, string nodeName, string parentPath)
        {
            if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                return;

            // Resolve the Godot child node name for the root mesh.
            // Godot wraps all FBX Model nodes under RootNode, so the root mesh
            // is a child.  For multi-mesh FBX the child name is the FBX root
            // object name; for single-mesh FBX, Godot names the child after the
            // mesh, which GetNodeNames resolves via instantiation.
            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxAsset == null)
                return;

            string targetNodeName = fbxAsset.name;
            var nodeNames = FbxExporter.GetNodeNames(fbxPath);
            if (fbxAsset.transform.childCount == 0 && nodeNames.Count > 0)
                targetNodeName = nodeNames[0];

            bool hasOverrides = false;
            for (int i = 0; i < renderer.sharedMaterials.Length; i++)
            {
                Material mat = renderer.sharedMaterials[i];
                if (mat == null) continue;

                string matPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(matPath)) continue;
                if (matPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;

                if (!hasOverrides)
                {
                    // Create child override node
                    string instancePath = parentPath == "." ? nodeName : parentPath + "/" + nodeName;
                    _writer.AddOverrideNode(targetNodeName, instancePath);
                    hasOverrides = true;
                }

                string matResPath = PathUtil.MaterialToGodotResPath(matPath);
                string matExtId = _writer.AddExtResource("Material", matResPath);
                _writer.AddPropertyExtResource($"surface_material_override/{i}", matExtId);
            }

            if (hasOverrides)
                _writer.AddBlankLine();
        }

        void TrackFbxMeshReference(string fbxPath, string meshName)
        {
            if (!_fbxMeshReferences.TryGetValue(fbxPath, out var meshes))
            {
                meshes = new HashSet<string>();
                _fbxMeshReferences[fbxPath] = meshes;
            }

            meshes.Add(meshName);

            if (meshes.Count > 1 && !_fbxMultiMeshWarned.Contains(fbxPath))
            {
                _fbxMultiMeshWarned.Add(fbxPath);
                Debug.LogWarning($"[U2G][WARN] Multiple different sub-meshes from FBX '{fbxPath}' " +
                    $"are referenced: {string.Join(", ", meshes)}. Each reference instances the entire FBX — " +
                    "visual duplicates may occur. Please fix manually in Godot.");
            }
        }

        public static string GetUniqueName(string name, HashSet<string> siblingNames)
        {
            if (siblingNames.Add(name))
                return name;

            int suffix = 2;
            string candidate;
            do
            {
                candidate = name + "_" + suffix;
                suffix++;
            }
            while (!siblingNames.Add(candidate));

            return candidate;
        }
    }
}
