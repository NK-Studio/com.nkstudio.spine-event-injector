using UnityEditor;

namespace NKStudio.SpineEventInjector
{
    /// <summary>화면 테마 설정(EditorPrefs). 기본은 Unity 에디터 스킨을 따른다.</summary>
    static class ThemePref
    {
        const string Key = "NKStudio.SpineEventInjector.Theme";

        public enum Mode
        {
            System,
            Dark,
            Light,
        }

        public static Mode Current
        {
            get
            {
                switch (EditorPrefs.GetString(Key, "system"))
                {
                    case "dark": return Mode.Dark;
                    case "light": return Mode.Light;
                    default: return Mode.System;
                }
            }
            set => EditorPrefs.SetString(Key, value == Mode.Dark ? "dark" : value == Mode.Light ? "light" : "system");
        }

        /// <summary>지금 다크 토큰을 쓸지.</summary>
        public static bool Dark => Current == Mode.System ? EditorGUIUtility.isProSkin : Current == Mode.Dark;
    }
}
