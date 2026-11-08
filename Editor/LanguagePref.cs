using UnityEditor;

namespace NKStudio.SpineEventInjector
{
    /// <summary>화면 언어 설정(EditorPrefs, 기본 영어). 에디터가 켜질 때·도메인 리로드 때 <see cref="Loc"/> 에 반영한다.</summary>
    static class LanguagePref
    {
        const string Key = "NKStudio.SpineEventInjector.Language";

        [InitializeOnLoadMethod]
        static void Load()
        {
            Loc.Korean = EditorPrefs.GetString(Key, "en") == "ko";
            Loc.Mac = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXEditor;
        }

        public static bool Korean
        {
            get => Loc.Korean;
            set
            {
                EditorPrefs.SetString(Key, value ? "ko" : "en");
                Loc.Korean = value;
            }
        }
    }
}
