using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace U2GExporter
{
    /// <summary>
    /// Writes Godot .tres (resource) files for StandardMaterial3D resources.
    /// </summary>
    public class TresWriter
    {
        readonly List<ExtResource> _extResources = new List<ExtResource>();
        readonly List<string> _properties = new List<string>();
        int _nextId = 1;

        public string AddExtResource(string type, string resPath)
        {
            string id = _nextId.ToString(CultureInfo.InvariantCulture);
            _nextId++;
            _extResources.Add(new ExtResource { Type = type, Path = resPath, Id = id });
            return id;
        }

        public void AddProperty(string name, string value)
        {
            _properties.Add($"{name} = {value}");
        }

        public void AddPropertyColor(string name, float r, float g, float b, float a)
        {
            _properties.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} = Color({1}, {2}, {3}, {4})", name, r, g, b, a));
        }

        public void AddPropertyFloat(string name, float value)
        {
            _properties.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} = {1}", name, value));
        }

        public void AddPropertyBool(string name, bool value)
        {
            _properties.Add($"{name} = {(value ? "true" : "false")}");
        }

        public void AddPropertyInt(string name, int value)
        {
            _properties.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} = {1}", name, value));
        }

        public void AddPropertyExtResource(string name, string id)
        {
            _properties.Add($"{name} = ExtResource(\"{id}\")");
        }

        public void AddPropertyVector3(string name, float x, float y, float z)
        {
            _properties.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} = Vector3({1}, {2}, {3})", name, x, y, z));
        }

        public void Write(string outputPath)
        {
            int loadSteps = _extResources.Count;
            var sb = new StringBuilder();

            if (loadSteps > 0)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[gd_resource type=\"StandardMaterial3D\" load_steps={0} format=3]", loadSteps + 1));
            else
                sb.AppendLine("[gd_resource type=\"StandardMaterial3D\" format=3]");

            sb.AppendLine();

            foreach (var ext in _extResources)
            {
                sb.AppendLine(string.Format("[ext_resource type=\"{0}\" path=\"{1}\" id=\"{2}\"]",
                    ext.Type, ext.Path, ext.Id));
            }

            if (_extResources.Count > 0)
                sb.AppendLine();

            sb.AppendLine("[resource]");
            foreach (string prop in _properties)
            {
                sb.AppendLine(prop);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        struct ExtResource
        {
            public string Type;
            public string Path;
            public string Id;
        }
    }
}
