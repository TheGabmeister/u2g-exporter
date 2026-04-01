using System.IO;
using System.Text;

namespace U2GExporter
{
    public static class ProjectWriter
    {
        /// <summary>
        /// Generates a minimal project.godot file in the output directory.
        /// </summary>
        public static void WriteProjectFile(string outputDir, string projectName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; Engine configuration file.");
            sb.AppendLine("; It's best edited using the editor, so don't edit it unless you know what you're doing.");
            sb.AppendLine();
            sb.AppendLine("config_version=5");
            sb.AppendLine();
            sb.AppendLine("[application]");
            sb.AppendLine(string.Format("config/name=\"{0}\"", EscapeString(projectName)));
            sb.AppendLine("config/features=PackedStringArray(\"4.6\")");
            sb.AppendLine();
            sb.AppendLine("[rendering]");
            sb.AppendLine("renderer/rendering_method=\"forward_plus\"");

            string path = Path.Combine(outputDir, "project.godot");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
