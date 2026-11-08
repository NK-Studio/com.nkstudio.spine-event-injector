using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 고른 것의 속성: 키 하나(정의·프레임·값), 키 여럿(삭제), 이벤트 정의(이름·기본값·삭제).
    /// 입력칸은 Enter·포커스 이동 때 한 번만 알린다(is-delayed) — 되돌리기 한 단계가 한 번의 수정이 되도록.
    /// </summary>
    [UxmlElement]
    public partial class InspectorPanel : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/InspectorPanel/InspectorPanel";
        const string ClassRoot = "inspector-panel";
        const string ClassKey = "inspector-panel--key";
        const string ClassMulti = "inspector-panel--multi";
        const string ClassDef = "inspector-panel--def";

        readonly Label _title;
        readonly DropdownField _keyEvent;
        readonly FloatField _keyFrame;
        readonly Label _keySeconds;
        readonly IntegerField _keyInt;
        readonly FloatField _keyFloat;
        readonly TextField _keyString;
        readonly Label _multiText;
        readonly TextField _defName;
        readonly IntegerField _defInt;
        readonly FloatField _defFloat;
        readonly TextField _defString;
        EventKey _key;
        EventDefinition _def;
        EventSet _set;
        static string EmptyChoice => Loc.T("(비어 있음)");
        float _fps = 30f;

        internal event Action<EventKey, string> KeyDefinitionChanged;
        internal event Action<EventKey, double> KeyTimeChanged;
        internal event Action<EventKey, int, float, string> KeyValuesChanged;
        internal event Action DeleteKeysClicked;
        internal event Action<EventDefinition, string, int, float, string> DefinitionChanged;

        public InspectorPanel()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _title = this.Q<Label>("title");
            _keyEvent = this.Q<DropdownField>("key-event");
            _keyFrame = this.Q<FloatField>("key-frame");
            _keySeconds = this.Q<Label>("key-seconds");
            _keyInt = this.Q<IntegerField>("key-int");
            _keyFloat = this.Q<FloatField>("key-float");
            _keyString = this.Q<TextField>("key-string");
            _multiText = this.Q<Label>("multi-text");
            _defName = this.Q<TextField>("def-name");
            _defInt = this.Q<IntegerField>("def-int");
            _defFloat = this.Q<FloatField>("def-float");
            _defString = this.Q<TextField>("def-string");

            _keyEvent.RegisterValueChangedCallback(e => KeyDefinitionChanged?.Invoke(_key, e.newValue == EmptyChoice ? string.Empty : e.newValue));
            _keyFrame.RegisterValueChangedCallback(e => KeyTimeChanged?.Invoke(_key, Math.Max(0f, e.newValue) / _fps));
            _keyInt.RegisterValueChangedCallback(_ => SendKeyValues());
            _keyFloat.RegisterValueChangedCallback(_ => SendKeyValues());
            RestrictToAlphanumeric(_keyString, SendKeyValues, false);
            ResetOnDoubleClick(_keyInt.labelElement, () => _keyInt.value = KeyDefinition?.Int ?? 0);
            ResetOnDoubleClick(_keyFloat.labelElement, () => _keyFloat.value = KeyDefinition?.Float ?? 0f);
            ResetOnDoubleClick(_keyString.labelElement, () =>
            {
                // 정의 기본값은 입력 제한을 거치지 않고 그대로 되돌린다
                _keyString.SetValueWithoutNotify(KeyDefinition?.String ?? string.Empty);
                SendKeyValues();
            });
            this.Q<Button>("key-delete").clicked += () => DeleteKeysClicked?.Invoke();
            this.Q<Button>("multi-delete").clicked += () => DeleteKeysClicked?.Invoke();
            RestrictToAlphanumeric(_defName, SendDefinition, true);
            _defInt.RegisterValueChangedCallback(_ => SendDefinition());
            _defFloat.RegisterValueChangedCallback(_ => SendDefinition());
            _defString.RegisterValueChangedCallback(_ => SendDefinition());
        }

        internal void Show(EventSet set, IReadOnlyList<EventKey> keys, EventDefinition def)
        {
            _set = set;
            _fps = set?.Fps ?? 30f;
            _key = keys.Count == 1 ? keys[0] : null;
            _def = keys.Count == 0 ? def : null;
            EnableInClassList(ClassKey, _key != null);
            EnableInClassList(ClassMulti, keys.Count > 1);
            EnableInClassList(ClassDef, _def != null);
            if (_key != null)
            {
                _title.text = Loc.T("이벤트 키");
                var choices = new List<string> { EmptyChoice };
                choices.AddRange(set.Definitions.Select(d => d.Name));
                _keyEvent.choices = choices;
                _keyEvent.SetValueWithoutNotify(_key.Name.Length == 0 ? EmptyChoice : _key.Name);
                _keyFrame.SetValueWithoutNotify((float)Math.Round(_key.Seconds * _fps, 4));
                _keySeconds.text = Loc.T("{0:0.####}초 · {1}", _key.Seconds, _key.Animation);
                _keyInt.SetValueWithoutNotify(_key.Int);
                _keyFloat.SetValueWithoutNotify(_key.Float);
                _keyString.SetValueWithoutNotify(_key.String ?? string.Empty);
            }
            else if (keys.Count > 1)
            {
                _title.text = Loc.T("이벤트 키 여럿");
                _multiText.text = Loc.T("키 {0}개를 골랐습니다. 도프시트에서 함께 끌어 옮기거나 지울 수 있습니다.", keys.Count);
            }
            else if (_def != null)
            {
                _title.text = Loc.T("이벤트 정의");
                _defName.SetValueWithoutNotify(_def.Name);
                _defInt.SetValueWithoutNotify(_def.Int);
                _defFloat.SetValueWithoutNotify(_def.Float);
                _defString.SetValueWithoutNotify(_def.String ?? string.Empty);
            }
            else
            {
                _title.text = Loc.T("속성");
            }
        }

        void SendKeyValues()
        {
            if (_key != null)
            {
                KeyValuesChanged?.Invoke(_key, _keyInt.value, _keyFloat.value, _keyString.value);
            }
        }

        static bool IsAlphanumeric(char c) => c < 128 && char.IsLetterOrDigit(c);

        static string Alphanumeric(string text) => new string((text ?? string.Empty).Where(IsAlphanumeric).ToArray());

        /// <summary>
        /// 영문자·숫자만 받는 입력칸(이벤트 키 String, 이벤트 정의 이름). 입력 중에는 다른 글자 키를 막고,
        /// 붙여넣기·조합 입력으로 들어온 것은 값이 확정될 때 걸러 낸 뒤 onCommit 을 부른다.
        /// </summary>
        void RestrictToAlphanumeric(TextField field, Action onCommit, bool required)
        {
            field.RegisterValueChangedCallback(e =>
            {
                string clean = Alphanumeric(e.newValue);
                if (required && clean.Length == 0)
                {
                    field.SetValueWithoutNotify(e.previousValue); // 이름은 비울 수 없다: 걸러서 빈 값이면 이전 이름으로
                    return;
                }

                if (clean != e.newValue)
                {
                    field.SetValueWithoutNotify(clean);
                }

                onCommit();
            });
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.character != 0 && char.IsControl(e.character) == false && e.actionKey == false && IsAlphanumeric(e.character) == false)
                {
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
        }

        /// <summary>지금 고른 키의 정의(빈 키면 null).</summary>
        EventDefinition KeyDefinition => _key == null ? null : _set?.Definition(_key.Name);

        /// <summary>라벨을 더블클릭하면 그 값만 정의 기본값으로 되돌린다.</summary>
        void ResetOnDoubleClick(Label label, Action reset)
        {
            // 숫자 라벨은 Unity 의 끌어서 바꾸기가 라벨에서 먼저 시작되므로, 라벨에 닿기 전에(부모의 TrickleDown) 가로챈다
            label.parent.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == label && e.button == 0 && e.clickCount == 2 && _key != null)
                {
                    reset();
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
        }

        void SendDefinition()
        {
            if (_def != null)
            {
                DefinitionChanged?.Invoke(_def, _defName.value, _defInt.value, _defFloat.value, _defString.value);
            }
        }
    }
}
