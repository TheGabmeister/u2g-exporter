using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace U2GExporter
{
    public enum AssetType
    {
        Scene,
        Prefab,
        Material,
        Texture,
        Fbx,
        Other
    }

    public class ClassifiedAsset
    {
        public string Path;
        public AssetType Type;
    }

    public static class DependencyResolver
    {
        static readonly HashSet<string> TextureExtensions = new HashSet<string>
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".psd", ".exr"
        };

        static readonly HashSet<string> WarnTextureExtensions = new HashSet<string>
        {
            ".psd", ".exr"
        };

        /// <summary>
        /// Discovers all assets in the selected folder, resolves dependencies,
        /// filters, and classifies them.
        /// </summary>
        public static List<ClassifiedAsset> Resolve(string selectedFolderPath, SkipReport skipReport)
        {
            // Phase 1: Discover root assets
            string[] guids = AssetDatabase.FindAssets("", new[] { selectedFolderPath });
            var allPaths = new HashSet<string>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                    allPaths.Add(path);
            }

            // Phase 2: Filter (only assets within the selected folder)
            var filtered = new HashSet<string>();
            foreach (string path in allPaths)
            {
                if (path.StartsWith("Packages/"))
                    continue;
                if (path.EndsWith(".cs"))
                {
                    skipReport.Add("C# Scripts", path);
                    continue;
                }
                if (ContainsEditorSegment(path))
                    continue;
                filtered.Add(path);
            }

            // Phase 5: Classify
            var result = new List<ClassifiedAsset>();
            foreach (string path in filtered.OrderBy(p => p))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                AssetType type;

                if (ext == ".unity")
                {
                    type = AssetType.Scene;
                }
                else if (ext == ".prefab")
                {
                    type = AssetType.Prefab;
                }
                else if (ext == ".mat")
                {
                    type = AssetType.Material;
                }
                else if (TextureExtensions.Contains(ext))
                {
                    type = AssetType.Texture;
                }
                else if (ext == ".fbx")
                {
                    type = AssetType.Fbx;
                }
                else
                {
                    ClassifyOther(path, ext, skipReport);
                    continue;
                }

                result.Add(new ClassifiedAsset { Path = path, Type = type });
            }

            return result;
        }

        /// <summary>
        /// Returns true if the selected folder contains scene files directly.
        /// External dependency scenes are excluded from conversion.
        /// </summary>
        public static List<ClassifiedAsset> GetScenesInFolder(List<ClassifiedAsset> assets, string selectedFolderPath)
        {
            return assets.Where(a => a.Type == AssetType.Scene && a.Path.StartsWith(selectedFolderPath)).ToList();
        }

        public static bool IsWarnTexture(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return WarnTextureExtensions.Contains(ext);
        }

        static bool ContainsEditorSegment(string path)
        {
            // Check for /Editor/ segment in path
            return path.Contains("/Editor/") || path.EndsWith("/Editor");
        }

        static void ClassifyOther(string path, string ext, SkipReport skipReport)
        {
            string category;
            switch (ext)
            {
                case ".anim":
                case ".controller":
                case ".overridecontroller":
                    category = "Animator Controllers";
                    break;
                case ".shader":
                case ".shadergraph":
                case ".shadersubgraph":
                    category = "Custom Shaders";
                    break;
                case ".mp3":
                case ".wav":
                case ".ogg":
                case ".aif":
                case ".aiff":
                    category = "Audio Clips";
                    break;
                case ".particle":
                case ".vfx":
                    category = "Particle Systems";
                    break;
                case ".asset":
                    category = "ScriptableObjects/Assets";
                    break;
                case ".renderTexture":
                case ".cubemap":
                    category = "Render Textures";
                    break;
                case ".physicmaterial":
                case ".physicsMaterial2D":
                    category = "Physics Materials";
                    break;
                case ".mp4":
                case ".mov":
                case ".webm":
                    category = "Video Clips";
                    break;
                default:
                    category = "Other";
                    break;
            }
            skipReport.Add(category, path);
        }
    }
}
