using System.Collections.Generic;
using System.IO;
using System.Linq;
using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector
{
    /// <summary>.spine 포맷 규칙(Editor/Schemas/*.json)을 한 번 읽어 둔다.</summary>
    static class SchemaLibrary
    {
        static List<SpineProjectSchema> _schemas;

        public static IReadOnlyList<SpineProjectSchema> All
        {
            get
            {
                if (_schemas != null)
                {
                    return _schemas;
                }

                string dir = Path.Combine(PluginPaths.EditorRootFullPath, "Schemas");
                SpineProjectTemplates.Folder = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('/', '\\')), "Templates");
                _schemas = Directory.GetFiles(dir, "*.json")
                    .OrderBy(p => p, System.StringComparer.Ordinal)
                    .Select(p => SpineProjectSchema.Parse(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p)))
                    .ToList();
                return _schemas;
            }
        }

        /// <summary>담당하는 Spine 버전 범위(사람이 읽을 요약).</summary>
        public static string SupportedRange =>
            string.Join(", ", All.SelectMany(s => s.Formats).Distinct().OrderBy(v => v).Take(1)
                .Concat(All.SelectMany(s => s.Formats).Distinct().OrderBy(v => v).Reverse().Take(1)));
    }
}
