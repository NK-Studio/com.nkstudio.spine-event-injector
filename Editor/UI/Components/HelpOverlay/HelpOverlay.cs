using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>창 안에 맨 앞으로 덮어 띄우는 사용법(창 ⋮ ▸ 도움말). 닫기·Esc·바깥 클릭으로 닫는다.</summary>
    [UxmlElement]
    public partial class HelpOverlay : VisualElement
    {
        const string AssetPath = "Components/HelpOverlay/HelpOverlay";
        const string ClassRoot = "help-overlay";
        const string ClassOpen = "help-overlay--open";

        public HelpOverlay()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            this.Q<Button>("close").clicked += Close;
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == this)
                {
                    Close();
                }
            });
        }

        public bool IsOpen => ClassListContains(ClassOpen);

        public void Toggle() => EnableInClassList(ClassOpen, IsOpen == false);

        public void Close() => RemoveFromClassList(ClassOpen);
    }
}
