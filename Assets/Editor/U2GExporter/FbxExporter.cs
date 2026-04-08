using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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

            // Patch UnitScaleFactor in the exported copy so Godot imports at the
            // same visual scale as Unity — no per-instance transform hacks needed.
            PatchUnitScaleFactorForGodot(assetPath, destPath);

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

        /// <summary>
        /// Patches the UnitScaleFactor in a copied FBX file so that Godot's import
        /// (vertices × USF × 0.01) produces the same visual scale as Unity.
        ///
        /// Unity's effective vertex scale depends on ModelImporter settings:
        ///   useFileScale ON  → fileScale × globalScale
        ///   useFileScale OFF → globalScale only
        /// We set USF_new so that USF_new × 0.01 = Unity's effective scale.
        /// </summary>
        static void PatchUnitScaleFactorForGodot(string unityAssetPath, string destFbxPath)
        {
            var importer = AssetImporter.GetAtPath(unityAssetPath) as ModelImporter;
            if (importer == null) return;

            double targetUsf = importer.useFileScale
                ? (double)importer.fileScale * importer.globalScale / 0.01
                : (double)importer.globalScale / 0.01;

            try
            {
                byte[] data = File.ReadAllBytes(destFbxPath);
                int offset = FindUnitScaleFactorOffset(data);
                if (offset < 0) return;

                double currentUsf = BitConverter.ToDouble(data, offset);
                if (Math.Abs(currentUsf - targetUsf) < 1e-6) return;

                byte[] newBytes = BitConverter.GetBytes(targetUsf);
                Buffer.BlockCopy(newBytes, 0, data, offset, 8);
                File.WriteAllBytes(destFbxPath, data);

                Debug.Log($"[U2G][INFO] Patched UnitScaleFactor: {currentUsf} -> {targetUsf} in {Path.GetFileName(destFbxPath)}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[U2G][WARN] Failed to patch UnitScaleFactor in '{destFbxPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Finds the byte offset of the UnitScaleFactor double value in FBX binary data.
        /// Skips over OriginalUnitScaleFactor. Returns -1 if not found.
        /// </summary>
        static int FindUnitScaleFactorOffset(byte[] data)
        {
            byte[] needle = Encoding.ASCII.GetBytes("UnitScaleFactor");

            int idx = FindByteSequence(data, needle, 0);
            if (idx < 0) return -1;

            // Verify this is not "OriginalUnitScaleFactor" by checking the preceding byte.
            // In FBX binary, the property name is preceded by a 1-byte 'S' type marker
            // and a 4-byte length. If the character before our match is part of "Original",
            // skip to the next occurrence.
            if (idx > 0 && data[idx - 1] != needle.Length)
            {
                byte[] original = Encoding.ASCII.GetBytes("Original");
                if (idx >= original.Length)
                {
                    bool isOriginal = true;
                    for (int i = 0; i < original.Length; i++)
                    {
                        if (data[idx - original.Length + i] != original[i])
                        {
                            isOriginal = false;
                            break;
                        }
                    }
                    if (isOriginal)
                        idx = FindByteSequence(data, needle, idx + needle.Length);
                }
            }

            if (idx < 0) return -1;

            // Navigate past the property name to the value.
            // FBX P-node property format after name:
            //   S + 4-byte len + type1 string
            //   S + 4-byte len + type2 string
            //   S + 4-byte len + flags string
            //   D + 8-byte double value
            int pos = idx + needle.Length;
            for (int s = 0; s < 3; s++)
            {
                if (pos >= data.Length || data[pos] != (byte)'S')
                    return -1;
                pos++; // skip 'S'
                if (pos + 4 > data.Length) return -1;
                int len = BitConverter.ToInt32(data, pos);
                pos += 4 + len;
            }

            if (pos >= data.Length || data[pos] != (byte)'D')
                return -1;
            pos++; // skip 'D'

            if (pos + 8 > data.Length) return -1;
            return pos;
        }

        static int FindByteSequence(byte[] haystack, byte[] needle, int startIndex)
        {
            for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
