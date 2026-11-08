using System.Collections.Generic;
using System.IO;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// 이벤트가 하나도 없는 프로젝트에 새 정의·키를 만들 때 복제할 견본.
    /// 스키마마다 Spine CLI 로 만든 아주 작은 프로젝트(뼈 하나 + 이벤트 하나 + 키 하나)를 함께 배포한다.
    /// 복제하는 객체의 구조는 파일의 버전에 맞아야 하므로 같은 스키마의 견본만 쓴다.
    /// </summary>
    public static class SpineProjectTemplates
    {
        const string Extension = ".spinetpl";

        /// <summary>견본 폴더. 쓰는 쪽(플러그인·검증 도구)이 정한다.</summary>
        public static string Folder;

        /// <summary>이 스키마의 견본을 읽는다. 없으면 null(새로 읽어 돌려주므로 복제해도 다른 작업에 영향이 없다).</summary>
        public static SpineProjectDocument For(SpineProjectSchema schema, string formatVersion)
        {
            if (string.IsNullOrEmpty(Folder))
            {
                return null;
            }

            string name = schema.Name;
            foreach (KeyValuePair<string, string> rule in schema.Templates)
            {
                if (formatVersion != null && formatVersion.StartsWith(rule.Key))
                {
                    name = rule.Value;
                }
            }

            string path = Path.Combine(Folder, name + Extension);
            if (File.Exists(path) == false)
            {
                return null;
            }

            byte[] body = SpineProjectContainer.Decompress(File.ReadAllBytes(path), out _);
            return BigStack.Run(() => new SpineProjectDocument(body, schema));
        }
    }
}
