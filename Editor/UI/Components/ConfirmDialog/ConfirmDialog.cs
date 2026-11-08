using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 창 안에 맨 앞으로 띄우는 경고 창. 뒤를 검게 덮어 입력을 막고, Unity 6.3 이상이면 뒤 화면을 살짝 흐린다.
    /// 처음 포커스는 취소라 Enter 만 눌러서는 쓰지 않는다. Esc 는 취소, 바깥을 눌러도 닫지 않는다.
    /// </summary>
    [UxmlElement]
    public partial class ConfirmDialog : VisualElement
    {
        const string AssetPath = "Components/ConfirmDialog/ConfirmDialog";
        const string ClassRoot = "confirm-dialog";
        const string ClassOpen = "confirm-dialog--open";

        readonly Label _title;
        readonly Label _message;
        readonly Button _confirm;
        readonly Button _cancel;
        Action<bool> _done;

        public ConfirmDialog()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _title = this.Q<Label>("title");
            _message = this.Q<Label>("message");
            _confirm = this.Q<Button>("confirm");
            _cancel = this.Q<Button>("cancel");
            _confirm.clicked += () => Finish(true);
            _cancel.clicked += () => Finish(false);
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == this)
                {
                    _cancel.Focus();
                    e.StopPropagation();
                }
            });
        }

        public bool IsOpen => ClassListContains(ClassOpen);

        /// <summary>띄운다. 이미 떠 있으면 무시한다. done: 확인이면 true.</summary>
        public void Show(string title, string message, string confirmText, string cancelText, Action<bool> done)
        {
            if (IsOpen)
            {
                return;
            }

            _done = done;
            _title.text = title;
            _message.text = message;
            _confirm.text = confirmText;
            _cancel.text = cancelText;
            AddToClassList(ClassOpen);
            BringToFront();
            SetBackdropBlur(true);
            schedule.Execute(() => _cancel.Focus());
        }

        void Finish(bool confirmed)
        {
            if (IsOpen == false)
            {
                return;
            }

            Action<bool> done = _done;
            _done = null;
            RemoveFromClassList(ClassOpen);
            SetBackdropBlur(false);
            done?.Invoke(confirmed);
        }

        void OnKeyDown(KeyDownEvent e)
        {
            if (IsOpen == false)
            {
                return;
            }

            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    Finish(false);
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Finish(panel?.focusController?.focusedElement == _confirm);
                    break;
                case KeyCode.Tab:
                    (panel?.focusController?.focusedElement == _cancel ? _confirm : _cancel).Focus();
                    break;
            }

            e.StopPropagation(); // 뒤쪽 창의 단축키(되돌리기·재생 등)가 돌지 않게
        }

        void SetBackdropBlur(bool on)
        {
#if UNITY_6000_3_OR_NEWER
            if (parent == null)
            {
                return;
            }

            foreach (VisualElement sibling in parent.Children())
            {
                if (sibling == this)
                {
                    continue;
                }

                if (on)
                {
                    var blur = new FilterFunction(FilterFunctionType.Blur);
                    blur.AddParameter(new FilterParameter(BackdropBlur));
                    sibling.style.filter = new List<FilterFunction> { blur };
                }
                else
                {
                    sibling.style.filter = StyleKeyword.Null;
                }
            }
#endif
        }

#if UNITY_6000_3_OR_NEWER
        const float BackdropBlur = 3f;
#endif
    }
}
