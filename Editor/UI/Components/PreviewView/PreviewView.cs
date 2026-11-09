using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>스켈레톤 미리보기 화면과 재생 단추. 그림은 <see cref="SkeletonPreview"/> 가 만든다.</summary>
    [UxmlElement]
    public partial class PreviewView : VisualElement
    {
        const string AssetPath = "Components/PreviewView/PreviewView";
        const string ClassRoot = "preview-view";
        const string ClassMessage = "preview-view--message";
        const string ClassBusy = "preview-view--busy";
        const string ClassPlaying = "preview-view--playing";
        const string ClassFlashItem = "preview-view__flash-item";
        const string ClassFlashFade = "preview-view__flash-item--fade";
        const string ClassOn = "sei-icon-btn--on";
        static readonly CustomStyleProperty<Color> ClearColor = new CustomStyleProperty<Color>("--sei-preview-clear");
        static readonly CustomStyleProperty<Color> CheckerA = new CustomStyleProperty<Color>("--sei-preview-checker-a");
        static readonly CustomStyleProperty<Color> CheckerB = new CustomStyleProperty<Color>("--sei-preview-checker-b");

        readonly VisualElement _stage;
        readonly ActivityIndicator _spinner = new ActivityIndicator();
        readonly Image _image;
        readonly Label _message;
        readonly VisualElement _flash;
        readonly Button _loop;
        readonly Label _zoom;
        const float ZoomStep = 1.25f;
        const string ClassSegOn = "sei-seg__btn--on";
        const string ClassNarrow = "preview-view--narrow";
        const string ClassSkins = "preview-view--skins";
        static readonly Dictionary<ScrubSnap, string> ScrubButtons = new Dictionary<ScrubSnap, string>
        {
            [ScrubSnap.Free] = "scrub-free",
            [ScrubSnap.Frame] = "scrub-frame",
            [ScrubSnap.Key] = "scrub-key",
        };
        static readonly Dictionary<KeySnap, string> KeyButtons = new Dictionary<KeySnap, string>
        {
            [KeySnap.Free] = "key-free",
            [KeySnap.Frame] = "key-frame",
            [KeySnap.Playhead] = "key-head",
        };
        readonly Button _skin;
        readonly Label _skinName;
        readonly Button _speed;
        float _currentSpeed = 1f;
        SkeletonPreview _preview;
        Color _clear = Color.gray;
        Color _checkerA = Color.gray;
        Color _checkerB = Color.gray;
        string _target;
        float _seconds;
        bool _dirty;

        internal event Action PlayClicked;
        internal event Action<int> StepClicked;
        internal event Action LoopClicked;
        internal event Action<int> KeyJumpClicked;
        internal event Action<ScrubSnap> ScrubModeChanged;
        internal event Action<KeySnap> KeyModeChanged;
        internal event Action<float> SpeedChanged;

        public PreviewView()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _image = this.Q<Image>("image");
            _message = this.Q<Label>("message");
            _flash = this.Q("flash");
            _loop = this.Q<Button>("loop");
            this.Q<Button>("play").clicked += () => PlayClicked?.Invoke();
            this.Q<Button>("prev").clicked += () => StepClicked?.Invoke(-1);
            this.Q<Button>("next").clicked += () => StepClicked?.Invoke(1);
            _loop.clicked += () => LoopClicked?.Invoke();
            this.Q<Button>("key-prev").clicked += () => KeyJumpClicked?.Invoke(-1);
            this.Q<Button>("key-next").clicked += () => KeyJumpClicked?.Invoke(1);
            foreach (ScrubSnap m in ScrubButtons.Keys)
            {
                ScrubSnap mode = m;
                this.Q<Button>(ScrubButtons[m]).clicked += () =>
                {
                    ShowScrub(mode);
                    ScrubModeChanged?.Invoke(mode);
                };
            }

            foreach (KeySnap m in KeyButtons.Keys)
            {
                KeySnap mode = m;
                this.Q<Button>(KeyButtons[m]).clicked += () =>
                {
                    ShowKey(mode);
                    KeyModeChanged?.Invoke(mode);
                };
            }

            _skin = this.Q<Button>("skin");
            _skinName = this.Q<Label>("skin-name");
            _skin.clicked += OpenSkinMenu;
            _speed = this.Q<Button>("speed");
            _speed.clicked += OpenSpeedMenu;
            this.Q("transport").RegisterCallback<GeometryChangedEvent>(e =>
            {
                EnableInClassList(ClassNarrow, e.newRect.width < 780f);
            });
            _zoom = this.Q<Label>("zoom");
            this.Q<Button>("zoom-in").clicked += () => SetZoom(CurrentZoom * ZoomStep);
            this.Q<Button>("zoom-out").clicked += () => SetZoom(CurrentZoom / ZoomStep);
            this.Q<Button>("zoom-reset").clicked += () => SetZoom(1f);
            this.Q("stage").RegisterCallback<WheelEvent>(e =>
            {
                SetZoom(e.delta.y > 0 ? CurrentZoom / 1.1f : CurrentZoom * 1.1f);
                e.StopPropagation();
            });
            _stage = this.Q("stage");
            this.Q("busy-card").Insert(0, _spinner);
            _stage.RegisterCallback<GeometryChangedEvent>(_ => _dirty = true);
            RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                e.customStyle.TryGetValue(ClearColor, out _clear);
                e.customStyle.TryGetValue(CheckerA, out _checkerA);
                e.customStyle.TryGetValue(CheckerB, out _checkerB);
                _preview?.SetBackground(_clear);
                _preview?.SetChecker(_checkerA, _checkerB);
                _dirty = true;
            });
            // 렌더링은 UI 이벤트 밖(EditorApplication.update)에서만 한다
            RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.update += RenderIfDirty);
            RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.update -= RenderIfDirty);
        }

        float CurrentZoom => _preview?.Zoom ?? 1f;

        internal void SetModes(ScrubSnap scrub, KeySnap key, float speed)
        {
            ShowScrub(scrub);
            ShowKey(key);
            ShowSpeed(speed);
        }

        void ShowScrub(ScrubSnap mode)
        {
            foreach (KeyValuePair<ScrubSnap, string> b in ScrubButtons)
            {
                this.Q<Button>(b.Value).EnableInClassList(ClassSegOn, b.Key == mode);
            }
        }

        void ShowKey(KeySnap mode)
        {
            foreach (KeyValuePair<KeySnap, string> b in KeyButtons)
            {
                this.Q<Button>(b.Value).EnableInClassList(ClassSegOn, b.Key == mode);
            }
        }

        void ShowSpeed(float speed)
        {
            _currentSpeed = speed;
            _speed.text = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
        }

        void OpenSpeedMenu()
        {
            var menu = new GenericMenu();
            foreach (float value in new[] { 0.25f, 0.5f, 1f })
            {
                float v = value;
                menu.AddItem(new GUIContent(v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×"), Mathf.Approximately(v, _currentSpeed), () =>
                {
                    ShowSpeed(v);
                    SpeedChanged?.Invoke(v);
                });
            }

            menu.DropDown(_speed.worldBound);
        }

        /// <summary>열린 스켈레톤의 스킨 목록에 맞춘다. 스킨이 하나뿐이면 숨긴다.</summary>
        internal void RefreshSkins()
        {
            bool choosable = _preview != null && _preview.HasData && _preview.SkinNames.Count > 1;
            EnableInClassList(ClassSkins, choosable);
            if (choosable)
            {
                if (Spine.Unity.Editor.SpineEditorUtilities.Icons.skin == null)
                {
                    Spine.Unity.Editor.SpineEditorUtilities.Icons.Initialize();
                }

                this.Q("skin-icon").style.backgroundImage = new StyleBackground(Spine.Unity.Editor.SpineEditorUtilities.Icons.skin);
            }

            ShowSkinLabel(choosable);
        }

        /// <summary>버튼 글: 하나면 그 이름, 여럿이면 «첫 이름 +N». 툴팁에 전부.</summary>
        void ShowSkinLabel(bool choosable)
        {
            if (!choosable)
            {
                _skinName.text = string.Empty;
                _skin.tooltip = string.Empty;
                return;
            }

            IReadOnlyList<string> skins = _preview.SelectedSkins;
            _skinName.text = skins.Count switch
            {
                0 => "(스킨 없음)",
                1 => skins[0],
                _ => $"{skins[0]} +{skins.Count - 1}",
            };
            _skin.tooltip = skins.Count > 1 ? string.Join("\n", skins) : string.Empty;
        }

        void OpenSkinMenu()
        {
            if (_preview == null)
            {
                return;
            }

            UnityEditor.PopupWindow.Show(_skin.worldBound, new SkinPickerPopup(_preview, _skin.worldBound.width, () =>
            {
                ShowSkinLabel(true);
                _dirty = true;
            }));
        }

        void SetZoom(float zoom)
        {
            if (_preview == null)
            {
                return;
            }

            _preview.Zoom = Mathf.Clamp(zoom, 0.1f, 20f);
            _zoom.text = $"{_preview.Zoom * 100f:0}%";
            _dirty = true;
        }

        internal void SetRenderer(SkeletonPreview preview)
        {
            _preview = preview;
            _preview?.SetBackground(_clear);
            _preview?.SetChecker(_checkerA, _checkerB);
            _zoom.text = "100%";
        }

        /// <summary>외부 .spine 내보내기처럼 미리보기를 준비하는 동안 표시기를 돌린다.</summary>
        internal void SetBusy(bool busy)
        {
            EnableInClassList(ClassBusy, busy);
            _spinner.Running = busy;
        }

        internal void SetMessage(string message)
        {
            _message.text = message ?? string.Empty;
            EnableInClassList(ClassMessage, string.IsNullOrEmpty(message) == false);
        }

        internal void SetState(bool playing, bool loop)
        {
            EnableInClassList(ClassPlaying, playing);
            _loop.EnableInClassList(ClassOn, loop);
        }

        /// <summary>그릴 애니메이션·시각을 정한다. 실제로 그리는 것은 다음 에디터 갱신 때다.</summary>
        internal void RequestRender(string animation, float seconds)
        {
            _target = animation;
            _seconds = seconds;
            _dirty = true;
        }

        void RenderIfDirty()
        {
            if (_dirty == false || panel == null)
            {
                return;
            }

            _dirty = false;
            if (_preview == null || _preview.HasData == false || _target == null)
            {
                _image.image = null;
                return;
            }

            int width = (int)_stage.layout.width, height = (int)_stage.layout.height;
            Texture texture = _preview.RenderNow(_target, _seconds, width, height);
            if (texture == null && (EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                _dirty = true; // 컴파일·임포트가 끝나면 다시
                return;
            }

            _image.image = texture;
            _image.style.width = width;
            _image.style.height = height;
            _image.MarkDirtyRepaint();
        }

        /// <summary>이벤트 이름을 화면 왼쪽 위에 잠깐 띄운다.</summary>
        internal void Flash(string text)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(ClassFlashItem);
            _flash.Add(label);
            schedule.Execute(() => label.AddToClassList(ClassFlashFade)).StartingIn(500);
            schedule.Execute(() => label.RemoveFromHierarchy()).StartingIn(1200);
        }
    }
}
