using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace U2GExporter
{
    public class SkipReport
    {
        readonly Dictionary<string, List<string>> _categories = new Dictionary<string, List<string>>();

        public void Add(string category, string assetPath)
        {
            if (!_categories.TryGetValue(category, out var list))
            {
                list = new List<string>();
                _categories[category] = list;
            }
            list.Add(assetPath);
        }

        public void Log()
        {
            if (_categories.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("=== Skip Report ===");
            sb.AppendLine("Skipped asset types (not supported in V1):");

            foreach (var kvp in _categories.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value.Count} file(s)");
            }

            sb.AppendLine();
            sb.AppendLine("  Details:");

            foreach (var kvp in _categories.OrderBy(k => k.Key))
            {
                foreach (string path in kvp.Value.OrderBy(p => p))
                {
                    sb.AppendLine($"    {path}");
                }
            }

            Debug.LogWarning(sb.ToString());
        }
    }
}
