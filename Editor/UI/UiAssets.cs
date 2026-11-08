using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>UXML·USS 로딩을 한 곳에서. 루트는 이 플러그인의 Editor/UI 폴더(위치를 하드코딩하지 않는다).</summary>
    static class UiAssets
    {
        static string Root => PluginPaths.EditorRoot + "UI/";

        public static VisualTreeAsset Uxml(string relativePath) => Load<VisualTreeAsset>(relativePath + ".uxml");

        public static StyleSheet Uss(string relativePath) => Load<StyleSheet>(relativePath + ".uss");

        static T Load<T>(string relativePath) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + relativePath);
            if (asset == null)
            {
                throw new InvalidOperationException($"UI asset not found: {Root}{relativePath}");
            }

            return asset;
        }

        /// <summary>컴포넌트 하나: 자기 UXML 을 복제해 넣고 자기 USS 를 붙인다.</summary>
        public static void Build(VisualElement element, string relativePath)
        {
            Uxml(relativePath).CloneTree(element);
            element.styleSheets.Add(Uss(relativePath));
            Translate(element);
        }

        /// <summary>UXML 에 적힌 글(라벨·버튼·필드 이름·툴팁)을 현재 언어로 바꾼다. 원문은 한국어다.</summary>
        public static void Translate(VisualElement root)
        {
            root.Query<VisualElement>().ForEach(e =>
            {
                if (e is TextElement text && string.IsNullOrEmpty(text.text) == false)
                {
                    text.text = Loc.TryT(text.text) ?? text.text;
                }

                if (string.IsNullOrEmpty(e.tooltip) == false)
                {
                    e.tooltip = Loc.TryT(e.tooltip) ?? e.tooltip;
                }
            });
        }

        public static void ApplyTheme(VisualElement root)
        {
            root.styleSheets.Add(Uss(ThemePref.Dark ? "Theme/Theme.Dark" : "Theme/Theme.Light"));
            root.styleSheets.Add(Uss("Theme/Common"));
        }
    }
}
