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

            // Patch the exported copy so Godot imports at the same visual scale
            // as Unity — fixes both UnitScaleFactor mismatches and Blender-style
            // baked unit conversion scales on Model nodes.
            PatchFbxForGodot(assetPath, destPath);

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
        /// Patches an exported FBX copy so Godot imports it at the same visual
        /// scale as Unity. Handles two independent issues in a single file read:
        ///
        /// 1. UnitScaleFactor — Godot always applies USF × 0.01 to vertices.
        ///    We set USF so that USF × 0.01 = Unity's effective scale.
        ///
        /// 2. Baked unit conversion scale — Some FBX exporters (notably Blender)
        ///    bake an Lcl Scaling of (OrigUSF/USF, OrigUSF/USF, OrigUSF/USF) on
        ///    Model nodes to compensate for the UnitScaleFactor. Unity collapses
        ///    this into vertex data on import, but Godot preserves it, causing
        ///    double-scaling. We detect and remove these baked scales.
        /// </summary>
        static void PatchFbxForGodot(string unityAssetPath, string destFbxPath)
        {
            var importer = AssetImporter.GetAtPath(unityAssetPath) as ModelImporter;
            if (importer == null) return;

            try
            {
                byte[] data = File.ReadAllBytes(destFbxPath);
                bool modified = false;

                // Read original USF and OriginalUSF before any patches.
                int usfOffset = FindUnitScaleFactorOffset(data);
                int origUsfOffset = FindOriginalUnitScaleFactorOffset(data);

                double originalUsf = usfOffset >= 0
                    ? BitConverter.ToDouble(data, usfOffset) : 1.0;
                double origUsf = origUsfOffset >= 0
                    ? BitConverter.ToDouble(data, origUsfOffset) : originalUsf;

                // --- Patch 1: UnitScaleFactor ---
                double targetUsf = importer.useFileScale
                    ? (double)importer.fileScale * importer.globalScale / 0.01
                    : (double)importer.globalScale / 0.01;

                if (usfOffset >= 0 && Math.Abs(originalUsf - targetUsf) > 1e-6)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(targetUsf), 0, data, usfOffset, 8);
                    modified = true;
                    Debug.Log($"[U2G][INFO] Patched UnitScaleFactor: {originalUsf} -> {targetUsf} in {Path.GetFileName(destFbxPath)}");
                }

                // --- Patch 2: Baked unit conversion scale ---
                // When USF != OriginalUSF, the FBX exporter may have baked an
                // Lcl Scaling of (OrigUSF/USF) on Model nodes to cancel out the
                // unit conversion. Remove it so Godot doesn't double-apply.
                if (Math.Abs(originalUsf - origUsf) > 1e-6)
                {
                    double expectedScale = origUsf / originalUsf;
                    modified |= PatchBakedUnitConversionScale(data, expectedScale,
                        Path.GetFileName(destFbxPath));
                }

                if (modified)
                    File.WriteAllBytes(destFbxPath, data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[U2G][WARN] Failed to patch FBX '{destFbxPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Finds Lcl Scaling entries with a uniform value matching the expected
        /// baked compensation scale and resets them to (1, 1, 1).
        /// </summary>
        static bool PatchBakedUnitConversionScale(byte[] data, double expectedScale, string fileName)
        {
            byte[] needle = Encoding.ASCII.GetBytes("Lcl Scaling");
            bool patched = false;
            int idx = 0;

            while (true)
            {
                idx = FindByteSequence(data, needle, idx);
                if (idx < 0) break;

                int pos = idx + needle.Length;

                // Skip 3 S+len+string type descriptor fields
                bool ok = true;
                for (int s = 0; s < 3; s++)
                {
                    if (pos >= data.Length || data[pos] != (byte)'S')
                    { ok = false; break; }
                    pos++;
                    if (pos + 4 > data.Length) { ok = false; break; }
                    int len = BitConverter.ToInt32(data, pos);
                    pos += 4 + len;
                }

                // Read 3 consecutive D+double values (x, y, z)
                if (ok && pos + 27 <= data.Length
                    && data[pos] == (byte)'D'
                    && data[pos + 9] == (byte)'D'
                    && data[pos + 18] == (byte)'D')
                {
                    double x = BitConverter.ToDouble(data, pos + 1);
                    double y = BitConverter.ToDouble(data, pos + 10);
                    double z = BitConverter.ToDouble(data, pos + 19);

                    if (Math.Abs(x - expectedScale) < 1e-6
                        && Math.Abs(y - expectedScale) < 1e-6
                        && Math.Abs(z - expectedScale) < 1e-6)
                    {
                        byte[] one = BitConverter.GetBytes(1.0);
                        Buffer.BlockCopy(one, 0, data, pos + 1, 8);
                        Buffer.BlockCopy(one, 0, data, pos + 10, 8);
                        Buffer.BlockCopy(one, 0, data, pos + 19, 8);
                        patched = true;
                        Debug.Log($"[U2G][INFO] Patched baked Lcl Scaling ({expectedScale}) -> 1.0 in {fileName}");
                    }
                }

                idx += needle.Length;
            }

            return patched;
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

        /// <summary>
        /// Finds the byte offset of the OriginalUnitScaleFactor double value.
        /// </summary>
        static int FindOriginalUnitScaleFactorOffset(byte[] data)
        {
            byte[] needle = Encoding.ASCII.GetBytes("OriginalUnitScaleFactor");
            int idx = FindByteSequence(data, needle, 0);
            if (idx < 0) return -1;

            int pos = idx + needle.Length;
            for (int s = 0; s < 3; s++)
            {
                if (pos >= data.Length || data[pos] != (byte)'S')
                    return -1;
                pos++;
                if (pos + 4 > data.Length) return -1;
                int len = BitConverter.ToInt32(data, pos);
                pos += 4 + len;
            }

            if (pos >= data.Length || data[pos] != (byte)'D')
                return -1;
            pos++;

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
