using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>애니메이션 목록. 고르면 도프시트·미리보기가 그 애니메이션으로 바뀐다.</summary>
    [UxmlElement]
    public partial class AnimationList : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/AnimationList/AnimationList";
        const string ItemPath = "Windows/SpineEventInjectorWindow/Panels/AnimationList/AnimationListItem";
        const string ClassRoot = "animation-list";
        const string ClassSkeletons = "animation-list--skeletons";

        readonly ListView _list;
        readonly List<string> _names = new List<string>();
        readonly VisualTreeAsset _item;
        readonly Button _skeleton;
        readonly Label _skeletonName;
        IReadOnlyList<string> _skeletons = Array.Empty<string>();
        bool _silent;

        internal event Action<string> Selected;
        internal event Action<string> SkeletonSelected;

        public AnimationList()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _item = UiAssets.Uxml(ItemPath);
            _list = this.Q<ListView>("list");
            _skeleton = this.Q<Button>("skeleton");
            _skeletonName = this.Q<Label>("skeleton-name");
            _skeleton.clicked += OpenSkeletonMenu;
            _list.itemsSource = _names;
            _list.makeItem = () => _item.Instantiate();
            _list.bindItem = Bind;
            _list.selectionChanged += items =>
            {
                if (_silent == false && items.FirstOrDefault() is string name)
                {
                    Selected?.Invoke(name);
                }
            };
        }

        internal void Show(EventSet set, string current)
        {
            _names.Clear();
            if (set != null)
            {
                _names.AddRange(set.Animations);
            }

            _silent = true;
            _list.RefreshItems();
            int index = current == null ? -1 : _names.IndexOf(current);
            _list.SetSelectionWithoutNotify(index >= 0 ? new[] { index } : Array.Empty<int>());
            _silent = false;
        }

        /// <summary>프로젝트의 스켈레톤 목록에 맞춘다. 하나뿐이면 숨긴다.</summary>
        internal void ShowSkeletons(IReadOnlyList<string> names, string current)
        {
            _skeletons = names ?? Array.Empty<string>();
            bool choosable = _skeletons.Count > 1;
            EnableInClassList(ClassSkeletons, choosable);
            _skeletonName.text = choosable ? current ?? string.Empty : string.Empty;
            if (choosable)
            {
                if (Spine.Unity.Editor.SpineEditorUtilities.Icons.skeleton == null)
                {
                    Spine.Unity.Editor.SpineEditorUtilities.Icons.Initialize();
                }

                this.Q("skeleton-icon").style.backgroundImage = new StyleBackground(Spine.Unity.Editor.SpineEditorUtilities.Icons.skeleton);
            }
        }

        void OpenSkeletonMenu()
        {
            var menu = new GenericMenu();
            foreach (string name in _skeletons)
            {
                string picked = name;
                menu.AddItem(new GUIContent(picked), picked == _skeletonName.text, () => SkeletonSelected?.Invoke(picked));
            }

            menu.DropDown(_skeleton.worldBound);
        }

        void Bind(VisualElement element, int index)
        {
            string name = _names[index];
            element.Q<Label>("name").text = name;
            element.Q("row").tooltip = name;
        }
    }
}
