using System.IO;
using UnityEngine;

namespace U2GExporter
{
    public static class TextureExporter
    {
        /// <summary>
        /// Copies a texture file from the Unity project to the Godot output directory,
        /// preserving the folder structure (minus Assets/ prefix).
        /// </summary>
        public static void Export(string assetPath, string outputDir)
        {
            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            string destPath = Path.Combine(outputDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            File.Copy(assetPath, destPath, true);

            if (DependencyResolver.IsWarnTexture(assetPath))
            {
                Debug.LogWarning($"[U2G][WARN] Texture '{assetPath}' is a PSD/EXR file. " +
                    "Godot cannot import this format — please convert to PNG/JPG manually.");
            }

            Debug.Log($"[U2G][INFO] Copied texture: {assetPath}");
        }
    }
}
