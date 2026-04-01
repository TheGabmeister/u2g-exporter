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

                // Transform
                float[] t = CoordConvert.ConvertTransform(go.transform);
                _writer.AddPropertyTransform(t);

                // Visibility
                if (isInactive || (meshRenderer != null && !meshRenderer.enabled))
                    _writer.AddPropertyBool("visible", false);

                // Material overrides via child node overrides
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

            // Recurse into children
            var childSiblingNames = new HashSet<string>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                Transform child = go.transform.GetChild(i);
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

        void WriteFbxMaterialOverrides(string fbxPath, MeshRenderer renderer, string nodeName, string parentPath)
        {
            if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                return;

            // Load the FBX to get node names
            var nodeNames = FbxExporter.GetNodeNames(fbxPath);
            if (nodeNames.Count == 0)
                return;

            // For single-mesh FBX or when we can identify the child node,
            // write material overrides on the first child node
            string targetNodeName = nodeNames.Count > 0 ? nodeNames[0] : null;
            if (targetNodeName == null)
                return;

            bool hasOverrides = false;
            for (int i = 0; i < renderer.sharedMaterials.Length; i++)
            {
                Material mat = renderer.sharedMaterials[i];
                if (mat == null) continue;

                string matPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(matPath)) continue;

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
