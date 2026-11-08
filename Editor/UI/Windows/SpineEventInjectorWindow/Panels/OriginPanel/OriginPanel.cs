using System;
using UnityEngine.UIElements;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 익스포트 데이터(bytes·json)의 원본 연결: Spine 원본 경로, «원본에 아직 없는 이벤트» 수, «원본에 반영».
    /// .spine 을 직접 연 경우에는 숨긴다.
    /// </summary>
    [UxmlElement]
    public partial class OriginPanel : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/OriginPanel/OriginPanel";
        const string ClassRoot = "origin-panel";
        const string ClassHidden = "origin-panel--hidden";
        const string ClassLinked = "origin-panel--linked";
        const string ClassSynced = "origin-panel--synced";
        const string ClassMissing = "origin-panel--missing";
        const string ClassError = "origin-panel--error";

        readonly Label _path;
        readonly Label _state;

        internal event Action LinkClicked;
        internal event Action UnlinkClicked;
        internal event Action RefreshClicked;
        internal event Action PushClicked;

        public OriginPanel()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _path = this.Q<Label>("path");
            _state = this.Q<Label>("state");
            this.Q<Button>("link").clicked += () => LinkClicked?.Invoke();
            this.Q<Button>("unlink").clicked += () => UnlinkClicked?.Invoke();
            this.Q<Button>("refresh").clicked += () => RefreshClicked?.Invoke();
            this.Q<Button>("push").clicked += () => PushClicked?.Invoke();
        }

        internal void Show(EventSource source, string originPath, OriginSync sync)
        {
            bool visible = source != null && source.IsSpineProject == false && source.AssetPath != null;
            EnableInClassList(ClassHidden, visible == false);
            // .spine 원본을 직접 연 경우 등 연결이 뜻 없는 때는 숨길 뿐 아니라 단추도 잠근다(숨김이 풀려도 안전하게)
            this.Q<Button>("link").SetEnabled(visible);
            this.Q<Button>("push").SetEnabled(visible && originPath != null);
            bool linked = originPath != null;
            EnableInClassList(ClassLinked, linked);
            EnableInClassList(ClassSynced, linked && sync != null && sync.Error == null && sync.MissingCount == 0);
            EnableInClassList(ClassMissing, linked && sync != null && sync.Error == null && sync.MissingCount > 0);
            EnableInClassList(ClassError, linked && sync?.Error != null);
            _path.text = linked ? originPath : Loc.T("연결 안 됨 — 이 데이터 파일에만 씁니다");
            _path.tooltip = linked ? originPath : Loc.T("원본 .spine 을 연결하면 «원본에 아직 없는 이벤트»를 알려 주고 원본에 반영할 수 있습니다");
            if (linked == false || sync == null)
            {
                _state.text = string.Empty;
            }
            else if (sync.Error != null)
            {
                _state.text = sync.Error;
            }
            else if (sync.MissingCount == 0)
            {
                _state.text = Loc.T("원본과 같음");
            }
            else
            {
                _state.text = Loc.T("원본에 없는 이벤트 키 {0}개", sync.MissingKeys.Count) +
                              (sync.MissingDefinitions.Count > 0 ? Loc.T(" · 정의 {0}개", sync.MissingDefinitions.Count) : "");
            }
        }
    }
}
