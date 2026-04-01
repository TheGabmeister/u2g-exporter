using System.IO;

namespace U2GExporter
{
    /// <summary>
    /// Shared path conversion utilities.
    /// </summary>
    public static class PathUtil
    {
        /// <summary>
        /// Strips the "Assets/" prefix from a Unity asset path to produce a Godot-relative path.
        /// </summary>
        public static string UnityToGodotRelativePath(string unityAssetPath)
        {
            if (unityAssetPath.StartsWith("Assets/"))
                return unityAssetPath.Substring("Assets/".Length);
            return unityAssetPath;
        }

        /// <summary>
        /// Converts a Unity asset path to a Godot res:// path.
        /// Example: Assets/Textures/brick.png → res://Textures/brick.png
        /// </summary>
        public static string UnityToGodotResPath(string unityAssetPath)
        {
            string relative = UnityToGodotRelativePath(unityAssetPath);
            // Ensure forward slashes
            relative = relative.Replace('\\', '/');
            return "res://" + relative;
        }

        /// <summary>
        /// Converts a Unity .mat path to the corresponding Godot .tres res:// path.
        /// </summary>
        public static string MaterialToGodotResPath(string unityMatPath)
        {
            string relative = UnityToGodotRelativePath(unityMatPath);
            relative = Path.ChangeExtension(relative, ".tres");
            relative = relative.Replace('\\', '/');
            return "res://" + relative;
        }

        /// <summary>
        /// Converts a Unity .prefab path to the corresponding Godot .tscn res:// path.
        /// </summary>
        public static string PrefabToGodotResPath(string unityPrefabPath)
        {
            string relative = UnityToGodotRelativePath(unityPrefabPath);
            relative = Path.ChangeExtension(relative, ".tscn");
            relative = relative.Replace('\\', '/');
            return "res://" + relative;
        }

        /// <summary>
        /// Converts a Unity .unity scene path to the corresponding Godot .tscn res:// path.
        /// </summary>
        public static string SceneToGodotResPath(string unityScenePath)
        {
            string relative = UnityToGodotRelativePath(unityScenePath);
            relative = Path.ChangeExtension(relative, ".tscn");
            relative = relative.Replace('\\', '/');
            return "res://" + relative;
        }

        /// <summary>
        /// Converts a Unity FBX path to a Godot res:// path.
        /// </summary>
        public static string FbxToGodotResPath(string unityFbxPath)
        {
            return UnityToGodotResPath(unityFbxPath);
        }

        /// <summary>
        /// Escapes a string for use in Godot .tscn/.tres serialization.
        /// </summary>
        public static string EscapeGodotString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
