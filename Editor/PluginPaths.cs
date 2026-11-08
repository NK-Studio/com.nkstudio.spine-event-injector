using System;
using System.IO;
using UnityEditor;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 플러그인 폴더 위치. Assets/Plugins 아래든 Packages 아래든 동작하도록 경로를 하드코딩하지 않고
    /// 이 플러그인의 asmdef 가 있는 폴더(= Editor 폴더)를 찾는다.
    /// </summary>
    static class PluginPaths
    {
        const string AsmdefName = "NKStudio.SpineEventInjector.Editor";
        static string _editorRoot;

        /// <summary>Editor 폴더의 에셋 경로(예: Assets/Plugins/SpineEventInjector/Editor/).</summary>
        public static string EditorRoot
        {
            get
            {
                if (_editorRoot != null)
                {
                    return _editorRoot;
                }

                foreach (string guid in AssetDatabase.FindAssets(AsmdefName + " t:AssemblyDefinitionAsset"))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == AsmdefName)
                    {
                        _editorRoot = Path.GetDirectoryName(path).Replace('\\', '/') + "/";
                        return _editorRoot;
                    }
                }

                throw new InvalidOperationException(Loc.T("{0}.asmdef 를 찾을 수 없습니다", AsmdefName));
            }
        }

        /// <summary>Editor 폴더의 실제 파일 경로(Packages 아래에서도 동작).</summary>
        public static string EditorRootFullPath => Path.GetFullPath(EditorRoot);

        /// <summary>프로젝트 루트(Assets 의 부모) 절대 경로.</summary>
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        /// <summary>도구가 쓰는 Library 아래 작업 폴더(git 에 들어가지 않는다).</summary>
        public static string LibraryDir
        {
            get
            {
                string dir = Path.Combine(ProjectRoot, "Library", "NKStudio.SpineEventInjector");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}
