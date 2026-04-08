using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace U2GExporter
{
    public static class FbxExporter
    {
        /// <summary>
        /// Copies an FBX file from the Unity project to the Godot output directory.
        /// </summary>
        public static void Export(string assetPath, string outputDir)
        {
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            string destPath = Path.Combine(outputDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            File.Copy(assetPath, destPath, true);

            Debug.Log($"[U2G][INFO] Copied FBX: {assetPath}");
        }

        /// <summary>
        /// Loads an FBX as a GameObject and extracts the hierarchy of node names.
        /// Returns a list of child node names (first-level children of the root).
        /// For single-mesh FBX files where Unity doesn't resolve children on the asset,
        /// instantiates a temporary copy to get the real hierarchy.
        /// </summary>
        public static List<string> GetNodeNames(string fbxPath)
        {
            var names = new List<string>();
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (go == null)
                return names;

            CollectNodeNames(go.transform, names);

            // Unity may not resolve FBX children on the asset itself.
            // Instantiate temporarily to get the full hierarchy.
            if (names.Count == 0)
            {
                var instance = Object.Instantiate(go);
                instance.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    CollectNodeNames(instance.transform, names);
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            // If still empty (single-mesh FBX where the root IS the mesh),
            // use the mesh name from the MeshFilter as Godot creates a child with that name.
            if (names.Count == 0)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    names.Add(mf.sharedMesh.name);
            }

            return names;
        }

        /// <summary>
        /// Gets a flat mapping of child transform names for material override targeting.
        /// </summary>
        public static Dictionary<string, string> GetNodeNameMap(string fbxPath)
        {
            var map = new Dictionary<string, string>();
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (go == null)
                return map;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                Transform child = go.transform.GetChild(i);
                map[child.name] = child.name;
                CollectNodeNamesRecursive(child, child.name, map);
            }

            return map;
        }

        static void CollectNodeNames(Transform parent, List<string> names)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                names.Add(child.name);
                CollectNodeNames(child, names);
            }
        }

        static void CollectNodeNamesRecursive(Transform parent, string parentPath, Dictionary<string, string> map)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                string path = parentPath + "/" + child.name;
                map[child.name] = path;
                CollectNodeNamesRecursive(child, path, map);
            }
        }
    }
}
