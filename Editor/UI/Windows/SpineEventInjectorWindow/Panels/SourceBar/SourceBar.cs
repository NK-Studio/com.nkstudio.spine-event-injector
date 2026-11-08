using System;
using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 대상 고르기 줄: SkeletonDataAsset 칸(고르기 창도 SkeletonDataAsset 만), 외부 .spine 열기, 최근 목록,
    /// 형식·버전 정보, Unity 이벤트 가져오기. 편집하는 파일은 그 에셋이 쓰는 .skel.bytes·.json 이다.
    /// </summary>
    [UxmlElement]
    public partial class SourceBar : VisualElement
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/Panels/SourceBar/SourceBar";
        const string ClassRoot = "source-bar";
        const string ClassHasUnityEvents = "source-bar--has-unity-events";
        const string ClassExternal = "source-bar--external";
        readonly ObjectField _asset;
        readonly Label _fileName;
        readonly Button _openSpine;
        readonly Button _recent;
        readonly Label _info;
        readonly Button _import;

        internal event Action<Object> AssetPicked;
        /// <summary>칸을 비웠다(열린 것을 닫는다).</summary>
        internal event Action AssetCleared;
        /// <summary>고른 에셋이 Spine 스켈레톤 데이터가 아니다(칸은 비운다).</summary>
        internal event Action<string> AssetRejected;
        internal event Action OpenSpineClicked;
        internal event Action<string> RecentPicked;
        internal event Action ImportUnityClicked;

        public SourceBar()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _asset = this.Q<ObjectField>("asset");
            _asset.objectType = typeof(Spine.Unity.SkeletonDataAsset);
            _asset.RegisterValueChangedCallback(e =>
            {
                if (e.newValue == null)
                {
                    if (e.previousValue != null)
                    {
                        AssetCleared?.Invoke();
                    }

                    return;
                }

                if (EventSourceLoader.CanOpen(e.newValue) == false)
                {
                    _asset.SetValueWithoutNotify(e.previousValue);
                    AssetRejected?.Invoke(Loc.T("{0}: Spine 스켈레톤 데이터(.skel.bytes · .json)가 아닙니다", e.newValue.name));
                    return;
                }

                AssetPicked?.Invoke(e.newValue);
            });
            _openSpine = this.Q<Button>("open-spine");
            _openSpine.clicked += () => OpenSpineClicked?.Invoke();
            _fileName = this.Q<Label>("file-name");
            this.Q<Button>("file-close").clicked += () => AssetCleared?.Invoke();
            _recent = this.Q<Button>("recent");
            _recent.clicked += ShowRecent;
            _info = this.Q<Label>("info");
            _import = this.Q<Button>("import-unity");
            _import.clicked += () => ImportUnityClicked?.Invoke();
        }

        internal void Show(EventSource source, int unityEvents)
        {
            Object asset = null;
            if (source?.AssetPath != null)
            {
                asset = source.Preview != null && UnityEditor.AssetDatabase.Contains(source.Preview) ? source.Preview : null;
            }

            _asset.SetValueWithoutNotify(asset);
            // 프로젝트 밖 .spine 은 에셋이 아니므로 빈 에셋 칸 대신 파일 이름을 보인다(× 로 닫는다)
            bool external = source != null && source.AssetPath == null;
            // 프로젝트 에셋을 연 동안은 외부 .spine 으로 넘어갈 수 없다(에셋 칸을 비우면 다시 켜진다)
            bool assetOpen = source != null && source.AssetPath != null;
            _openSpine.SetEnabled(assetOpen == false);
            _recent.SetEnabled(assetOpen == false);
            EnableInClassList(ClassExternal, external);
            _fileName.text = external ? System.IO.Path.GetFileName(source.FilePath) : string.Empty;
            this.Q("file").tooltip = external ? source.FilePath : string.Empty;
            if (source == null)
            {
                _info.text = string.Empty;
            }
            else
            {
                string kind = source.Kind == EventSourceKind.SpineProject ? Loc.T("Spine 프로젝트") : source.Kind == EventSourceKind.SkeletonBinary ? Loc.T("바이너리") : "JSON";
                string where = source.AssetPath == null ? source.FilePath : source.AssetPath;
                _info.text = $"{kind} · Spine {source.Version} · {source.Working.Fps:0.##}fps";
                _info.tooltip = where;
            }

            _import.text = Loc.T("Unity 이벤트 {0}개 가져오기", unityEvents);
            _import.tooltip = Loc.T("Mecanim 클립에 사람이 단 AnimationEvent 를 이 스켈레톤의 Spine 이벤트로 옮깁니다(적용하기 전까지 파일은 그대로)");
            EnableInClassList(ClassHasUnityEvents, unityEvents > 0);
        }

        void ShowRecent()
        {
            IReadOnlyList<string> recent = SourceLinkStore.Recent;
            var menu = new GenericDropdownMenu();
            if (recent.Count == 0)
            {
                menu.AddDisabledItem(Loc.T("최근에 연 .spine 이 없습니다"), false);
            }

            foreach (string path in recent)
            {
                string p = path;
                menu.AddItem(p, false, () => RecentPicked?.Invoke(p));
            }

            menu.DropDown(_recent.worldBound, _recent, false);
        }
    }
}
