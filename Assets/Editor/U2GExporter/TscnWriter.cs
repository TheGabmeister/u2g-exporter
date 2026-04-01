using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace U2GExporter
{
    /// <summary>
    /// Writes Godot .tscn (scene) files.
    /// </summary>
    public class TscnWriter
    {
        readonly List<ExtResource> _extResources = new List<ExtResource>();
        readonly List<string> _nodes = new List<string>();
        int _nextId = 1;

        /// <summary>
        /// Registers an external resource and returns its string ID.
        /// If the same path was already registered, returns the existing ID.
        /// </summary>
        public string AddExtResource(string type, string resPath)
        {
            for (int i = 0; i < _extResources.Count; i++)
            {
                if (_extResources[i].Path == resPath)
                    return _extResources[i].Id;
            }

            string id = _nextId.ToString(CultureInfo.InvariantCulture);
            _nextId++;
            _extResources.Add(new ExtResource { Type = type, Path = resPath, Id = id });
            return id;
        }

        /// <summary>
        /// Adds a root node line (no parent attribute).
        /// </summary>
        public void AddRootNode(string name, string type)
        {
            _nodes.Add(string.Format("[node name=\"{0}\" type=\"{1}\"]",
                EscapeString(name), type));
        }

        /// <summary>
        /// Adds a child node with a type.
        /// </summary>
        public void AddNode(string name, string type, string parentPath)
        {
            _nodes.Add(string.Format("[node name=\"{0}\" type=\"{1}\" parent=\"{2}\"]",
                EscapeString(name), type, parentPath));
        }

        /// <summary>
        /// Adds an instanced node (from ExtResource).
        /// </summary>
        public void AddInstanceNode(string name, string parentPath, string extResourceId)
        {
            _nodes.Add(string.Format("[node name=\"{0}\" parent=\"{1}\" instance=ExtResource(\"{2}\")]",
                EscapeString(name), parentPath, extResourceId));
        }

        /// <summary>
        /// Adds a child-override node (for overriding properties on instanced sub-nodes).
        /// </summary>
        public void AddOverrideNode(string name, string parentPath)
        {
            _nodes.Add(string.Format("[node name=\"{0}\" parent=\"{1}\"]",
                EscapeString(name), parentPath));
        }

        /// <summary>
        /// Appends a raw property line to the last node.
        /// </summary>
        public void AddProperty(string name, string value)
        {
            _nodes.Add($"{name} = {value}");
        }

        public void AddPropertyFloat(string name, float value)
        {
            _nodes.Add(string.Format(CultureInfo.InvariantCulture, "{0} = {1}", name, value));
        }

        public void AddPropertyBool(string name, bool value)
        {
            _nodes.Add($"{name} = {(value ? "true" : "false")}");
        }

        public void AddPropertyColor(string name, float r, float g, float b, float a)
        {
            _nodes.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} = Color({1}, {2}, {3}, {4})", name, r, g, b, a));
        }

        public void AddPropertyTransform(float[] t)
        {
            if (t == null) return;
            _nodes.Add(string.Format(CultureInfo.InvariantCulture,
                "transform = Transform3D({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11})",
                t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8], t[9], t[10], t[11]));
        }

        public void AddPropertyExtResource(string name, string id)
        {
            _nodes.Add($"{name} = ExtResource(\"{id}\")");
        }

        /// <summary>
        /// Adds a blank line between node sections for readability.
        /// </summary>
        public void AddBlankLine()
        {
            _nodes.Add("");
        }

        public void Write(string outputPath)
        {
            int loadSteps = _extResources.Count;
            var sb = new StringBuilder();

            if (loadSteps > 0)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[gd_scene load_steps={0} format=3]", loadSteps + 1));
            else
                sb.AppendLine("[gd_scene format=3]");

            sb.AppendLine();

            foreach (var ext in _extResources)
            {
                sb.AppendLine(string.Format("[ext_resource type=\"{0}\" path=\"{1}\" id=\"{2}\"]",
                    ext.Type, ext.Path, ext.Id));
            }

            if (_extResources.Count > 0)
                sb.AppendLine();

            foreach (string line in _nodes)
            {
                sb.AppendLine(line);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        struct ExtResource
        {
            public string Type;
            public string Path;
            public string Id;
        }
    }
}
