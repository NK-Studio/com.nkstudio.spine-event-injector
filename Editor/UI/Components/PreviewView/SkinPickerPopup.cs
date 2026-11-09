using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 미리보기 스킨을 여러 개 켜고 끄는 팝업. 체크해도 닫히지 않고, 바깥을 누르면 닫힌다.
    /// 스킨 이름의 «폴더/» 부분으로 묶어 보여 준다.
    /// </summary>
    sealed class SkinPickerPopup : PopupWindowContent
    {
        const float RowHeight = 20f;
        const float MaxHeight = 420f;

        readonly SkeletonPreview _preview;
        readonly Action _changed;
        readonly float _width;

        public SkinPickerPopup(SkeletonPreview preview, float width, Action changed)
        {
            _preview = preview;
            _width = Mathf.Max(width, 240f);
            _changed = changed;
        }

        public override Vector2 GetWindowSize()
        {
            int rows = 0;
            var folders = new HashSet<string>();
            foreach (string name in _preview.SkinNames)
            {
                rows++;
                int slash = name.LastIndexOf('/');
                if (slash > 0 && folders.Add(name.Substring(0, slash)))
                {
                    rows++;
                }
            }

            return new Vector2(_width, Mathf.Min(rows * RowHeight + 12f, MaxHeight));
        }

        public override void OnGUI(Rect rect)
        {
        }

        public override VisualElement CreateGUI()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.contentContainer.style.paddingTop = 4;
            scroll.contentContainer.style.paddingBottom = 4;
            scroll.contentContainer.style.paddingLeft = 6;
            scroll.contentContainer.style.paddingRight = 6;

            // 폴더 없는 스킨(default 등)을 먼저, 그 뒤 폴더별로(처음 나온 순서)
            var folders = new Dictionary<string, Foldout>();
            var roots = new List<VisualElement>();
            var grouped = new List<VisualElement>();
            foreach (string name in _preview.SkinNames)
            {
                int slash = name.LastIndexOf('/');
                if (slash <= 0)
                {
                    roots.Add(MakeToggle(name, name));
                    continue;
                }

                string folder = name.Substring(0, slash);
                if (!folders.TryGetValue(folder, out Foldout foldout))
                {
                    foldout = new Foldout { text = folder, value = true };
                    folders[folder] = foldout;
                    grouped.Add(foldout);
                }

                foldout.Add(MakeToggle(name, name.Substring(slash + 1)));
            }

            foreach (VisualElement e in roots)
            {
                scroll.Add(e);
            }

            foreach (VisualElement e in grouped)
            {
                scroll.Add(e);
            }

            return scroll;
        }

        Toggle MakeToggle(string skin, string label)
        {
            var toggle = new Toggle(label) { value = _preview.IsSkinSelected(skin), tooltip = skin };
            toggle.style.height = RowHeight;
            toggle.style.marginTop = 0;
            toggle.style.marginBottom = 0;
            // 이름 칸 폭을 고정하지 않고 체크박스를 왼쪽에 둔다
            toggle.style.flexDirection = FlexDirection.RowReverse;
            toggle.style.justifyContent = Justify.FlexEnd;
            toggle.labelElement.style.minWidth = 0;
            toggle.labelElement.style.flexGrow = 1;
            toggle.labelElement.style.marginLeft = 4;
            toggle.labelElement.style.unityTextAlign = TextAnchor.MiddleLeft;
            toggle.RegisterValueChangedCallback(e =>
            {
                _preview.SetSkinSelected(skin, e.newValue);
                _changed?.Invoke();
            });
            return toggle;
        }
    }
}
