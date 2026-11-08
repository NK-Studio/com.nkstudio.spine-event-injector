using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>이벤트 정의 목록(+ 새로 만들기). 고르면 속성 칸에서 이름·기본값을 고친다.</summary>
    [UxmlElement]
    public partial class DefinitionList : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/DefinitionList/DefinitionList";
        const string ItemPath = "Windows/SpineEventInjectorWindow/Panels/DefinitionList/DefinitionListItem";
        const string ClassRoot = "definition-list";
        const string ClassDirty = "definition-list__row--dirty";
        const string ClassUnsynced = "definition-list__row--unsynced";
        const string ClassEmpty = "definition-list--empty";

        readonly ListView _list;
        readonly List<EventDefinition> _defs = new List<EventDefinition>();
        readonly VisualTreeAsset _item;
        HashSet<EventDefinition> _dirty = new HashSet<EventDefinition>();
        HashSet<string> _unsynced = new HashSet<string>();
        bool _silent;
        /// <summary>목록에서 고른 정의들(Shift·⌘/Ctrl 다중 선택). 속성 칸에는 그중 하나만 보인다.</summary>
        readonly List<EventDefinition> _picked = new List<EventDefinition>();

        internal event Action AddClicked;
        internal event Action<EventDefinition> Selected;
        internal event Action<List<EventDefinition>> DeleteRequested;

        public DefinitionList()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _item = UiAssets.Uxml(ItemPath);
            this.Q<Button>("add").clicked += () => AddClicked?.Invoke();
            _list = this.Q<ListView>("list");
            _list.itemsSource = _defs;
            _list.makeItem = () => _item.Instantiate();
            _list.bindItem = Bind;
            _list.selectionChanged += items =>
            {
                if (_silent)
                {
                    return;
                }

                _picked.Clear();
                _picked.AddRange(items.OfType<EventDefinition>());
                if (_picked.Count > 0)
                {
                    Selected?.Invoke(_picked[0]);
                }
            };
            _list.AddManipulator(new ContextualMenuManipulator(PopulateMenu));
            // 이 패널 안(목록·줄)에 포커스가 있을 때 Delete/Backspace: 고른 정의 삭제(쓰는 키가 있으면 창에서 묻는다).
            // ListView 가 먼저 키를 먹지 않도록 내려가는 단계(TrickleDown)에서 받는다
            RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && _picked.Count > 0)
                {
                    DeleteRequested?.Invoke(new List<EventDefinition>(_picked));
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
        }

        internal void Show(EventSet set, EventSet original, EventDefinition selected, ICollection<string> unsynced)
        {
            _defs.Clear();
            _dirty = new HashSet<EventDefinition>();
            _unsynced = new HashSet<string>(unsynced ?? Array.Empty<string>());
            if (set != null)
            {
                _defs.AddRange(set.Definitions);
                EventSetDiff diff = EventSetDiff.Compute(original, set);
                _dirty.UnionWith(diff.AddedDefinitions);
                _dirty.UnionWith(diff.ChangedDefinitions.Select(c => c.After));
            }

            EnableInClassList(ClassEmpty, set != null && _defs.Count == 0);
            _silent = true;
            _list.RefreshItems();
            // 고른 것 안에 있는 정의를 보는 중이면 다중 선택을 그대로 둔다(다른 것을 고르면 그 하나만)
            _picked.RemoveAll(d => _defs.Contains(d) == false);
            if (selected == null || _picked.Contains(selected) == false)
            {
                _picked.Clear();
                if (selected != null && _defs.Contains(selected))
                {
                    _picked.Add(selected);
                }
            }

            _list.SetSelectionWithoutNotify(_picked.Select(d => _defs.IndexOf(d)).ToArray());
            _silent = false;
        }

        void PopulateMenu(ContextualMenuPopulateEvent e)
        {
            // 고르지 않은 줄을 우클릭하면 그 줄 하나를 대상으로
            EventDefinition under = RowDefinition(e.target as VisualElement);
            if (under != null && _picked.Contains(under) == false)
            {
                _list.SetSelection(_defs.IndexOf(under));
            }

            if (_picked.Count == 0)
            {
                return;
            }

            var targets = new List<EventDefinition>(_picked);
            e.menu.AppendAction(Loc.T("선택 항목 삭제 ({0}개)", targets.Count), _ => DeleteRequested?.Invoke(targets));
            e.StopPropagation();
        }

        static EventDefinition RowDefinition(VisualElement element)
        {
            for (VisualElement v = element; v != null; v = v.parent)
            {
                if (v.userData is EventDefinition def)
                {
                    return def;
                }
            }

            return null;
        }

        void Bind(VisualElement element, int index)
        {
            EventDefinition def = _defs[index];
            element.userData = def;
            element.Q<Label>("name").text = def.Name;
            VisualElement row = element.Q("row");
            // 안쪽 글자는 포인터를 받지 않으므로(커서가 줄에서 정해지게) 툴팁은 줄에 둔다
            row.tooltip = def.Name;
            row.EnableInClassList(ClassDirty, _dirty.Contains(def));
            row.EnableInClassList(ClassUnsynced, _dirty.Contains(def) == false && _unsynced.Contains(def.Name));
        }
    }
}
