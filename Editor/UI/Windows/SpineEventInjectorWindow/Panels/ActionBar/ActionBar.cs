using System;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>아래 줄: 상태 메시지, «되돌리기», «적용하기».</summary>
    [UxmlElement]
    public partial class ActionBar : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/ActionBar/ActionBar";
        const string ClassRoot = "action-bar";

        readonly Label _status;
        readonly Button _revert;
        readonly Button _apply;

        internal event Action RevertClicked;
        internal event Action ApplyClicked;

        public ActionBar()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _status = this.Q<Label>("status");
            _revert = this.Q<Button>("revert");
            _apply = this.Q<Button>("apply");
            _revert.clicked += () => RevertClicked?.Invoke();
            _apply.clicked += () => ApplyClicked?.Invoke();
        }

        internal void SetStatus(string message, StatusKind kind)
        {
            _status.text = message ?? string.Empty;
            _status.tooltip = message ?? string.Empty;
            foreach (StatusKind k in (StatusKind[])Enum.GetValues(typeof(StatusKind)))
            {
                EnableInClassList($"{ClassRoot}--{k.ToString().ToLowerInvariant()}", k == kind && k != StatusKind.Info);
            }
        }

        internal void SetChanges(bool dirty, string applyLabel)
        {
            _revert.SetEnabled(dirty);
            _apply.SetEnabled(dirty);
            _apply.text = applyLabel;
        }
    }
}
