using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// Spine Event Injector 창. 컴포넌트를 조립하고 세션·동작과 잇기만 한다.
    /// 메뉴: Tools ▸ Spine2D ▸ Spine Event Injector, Project 창 우클릭 ▸ Spine Event Injector 로 열기.
    /// </summary>
    public sealed class SpineEventInjectorWindow : EditorWindow, IHasCustomMenu
    {
        const string AssetPath = "Windows/SpineEventInjectorWindow/SpineEventInjectorWindow";
        const string ClassEmpty = "sei-window--empty";
        const string Title = "Spine Event Injector";

        InjectorSession _session;
        InjectorActions _actions;
        SkeletonPreview _preview;
        PlaybackClock _clock;

        VisualElement _window;
        SourceBar _sourceBar;
        AnimationList _animations;
        ConfirmDialog _confirm;
        DefinitionList _definitions;
        PreviewView _previewView;
        EventDopesheet _dopesheet;
        float _dopesheetHeight;
        bool _centerHidden;
        InspectorPanel _inspector;
        OriginPanel _origin;
        ActionBar _actionBar;

        [MenuItem("Tools/Spine2D/Spine Event Injector")]
        public static SpineEventInjectorWindow Open() => GetWindow<SpineEventInjectorWindow>(Title);

        [MenuItem("Assets/Open in Spine Event Injector", false, 2000)]
        static void OpenSelected() => Open()._actions?.OpenAsset(Selection.activeObject);

        [MenuItem("Assets/Open in Spine Event Injector", true)]
        static bool CanOpenSelected() => Selection.activeObject != null && EventSourceLoader.CanOpen(Selection.activeObject);

        /// <summary>창 ⋮ 메뉴.</summary>
        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent(Loc.T("도움말")), false, () => rootVisualElement.Q<HelpOverlay>("help")?.Toggle());
            menu.AddItem(new GUIContent("Language/English"), LanguagePref.Korean == false, () => SetLanguage(false));
            menu.AddItem(new GUIContent("Language/한국어"), LanguagePref.Korean, () => SetLanguage(true));
            ThemePref.Mode theme = ThemePref.Current;
            menu.AddItem(new GUIContent(Loc.T("테마/Unity 따라가기")), theme == ThemePref.Mode.System, () => SetTheme(ThemePref.Mode.System));
            menu.AddItem(new GUIContent(Loc.T("테마/다크")), theme == ThemePref.Mode.Dark, () => SetTheme(ThemePref.Mode.Dark));
            menu.AddItem(new GUIContent(Loc.T("테마/라이트")), theme == ThemePref.Mode.Light, () => SetTheme(ThemePref.Mode.Light));
        }

        void SetTheme(ThemePref.Mode mode)
        {
            if (ThemePref.Current == mode)
            {
                return;
            }

            ThemePref.Current = mode;
            Rebuild();
        }

        /// <summary>언어를 바꾸고 화면을 다시 만든다(적용하지 않은 작업본은 그대로 이어진다).</summary>
        void SetLanguage(bool korean)
        {
            if (LanguagePref.Korean == korean)
            {
                return;
            }

            LanguagePref.Korean = korean;
            Rebuild();
        }

        /// <summary>화면을 처음부터 다시 만든다(언어·테마 바꿀 때). 적용하지 않은 작업본은 그대로 이어진다.</summary>
        void Rebuild()
        {
            OnDisable();
            rootVisualElement.Clear();
            // 루트에는 Unity 기본 에디터 스타일도 붙어 있으므로 이 도구가 붙인 것만 뗀다
            foreach (StyleSheet sheet in new[]
                     {
                         UiAssets.Uss("Theme/Theme.Dark"), UiAssets.Uss("Theme/Theme.Light"), UiAssets.Uss("Theme/Common"), UiAssets.Uss(AssetPath),
                     })
            {
                rootVisualElement.styleSheets.Remove(sheet);
            }

            CreateGUI();
        }

        /// <summary>«이벤트 키 추가» 버튼: 묻지 않고 재생 헤드에 빈 키를 넣고 고른다. 이벤트는 오른쪽 속성에서 고른다.</summary>
        void AddEventAtPlayhead()
        {
            if (_session == null || _session.HasSource == false || _session.Animation == null)
            {
                return;
            }

            _session.SetPlaying(false);
            EventKey key = _session.AddEmptyKey(_session.Time);
            if (key != null)
            {
                _dopesheet.RevealRow(key.Name);
            }
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            UiAssets.ApplyTheme(root);
            UiAssets.Build(root, AssetPath);
            _window = root.Q("window");
            _sourceBar = root.Q<SourceBar>("source-bar");
            _animations = root.Q<AnimationList>("animations");
            _confirm = root.Q<ConfirmDialog>("confirm");
            _definitions = root.Q<DefinitionList>("definitions");
            _previewView = root.Q<PreviewView>("preview");
            _dopesheet = root.Q<EventDopesheet>("dopesheet");
            _inspector = root.Q<InspectorPanel>("inspector");
            _origin = root.Q<OriginPanel>("origin");
            _actionBar = root.Q<ActionBar>("action-bar");

            _session = new InjectorSession();
            _preview = new SkeletonPreview();
            _actions = new InjectorActions(_session, _preview);
            _clock = new PlaybackClock(_session, root);
            _previewView.SetRenderer(_preview);
            Wire();
            Selection.selectionChanged += OnProjectSelection;
            EventGuard.Imported += OnImported;
            RefreshAll();
            // 도메인 리로드 직후에는 에셋 데이터베이스가 아직 준비 중일 수 있어, 열릴 때까지 잠깐씩 다시 시도한다
            int tries = 0;
            IVisualElementScheduledItem retry = null;
            retry = root.schedule.Execute(() =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    return;
                }

                _actions?.RestoreLast();
                if (_session.Source != null || ++tries >= 10)
                {
                    retry.Pause();
                }
            }).Every(300);
        }

        /// <summary>창을 닫을 때 «저장»을 고르면 적용한다.</summary>
        public override void SaveChanges()
        {
            if (_actions != null && _actions.Apply())
            {
                base.SaveChanges();
            }
        }

        void OnDisable()
        {
            _actions?.SaveWorkingState();
            _actions?.ReleasePreview();
            Selection.selectionChanged -= OnProjectSelection;
            EventGuard.Imported -= OnImported;
            _preview?.Dispose();
            _preview = null;
        }

        void Wire()
        {
            _sourceBar.AssetPicked += a => _actions.OpenAsset(a);
            _sourceBar.AssetCleared += _actions.Close;
            _sourceBar.AssetRejected += message => _session.Report(message, StatusKind.Warning);
            _sourceBar.OpenSpineClicked += _actions.BrowseSpine;
            _sourceBar.RecentPicked += path => _actions.OpenPath(path);
            _sourceBar.ImportUnityClicked += _actions.ImportUnityEvents;
            _animations.Selected += _session.SetAnimation;
            _animations.SkeletonSelected += _actions.SelectSkeleton;
            _definitions.AddClicked += () => _session.AddDefinition();
            _definitions.Selected += _session.SelectDefinition;
            _definitions.DeleteRequested += _actions.DeleteDefinitions;
            _previewView.PlayClicked += () => _clock.Toggle();
            _previewView.StepClicked += n =>
            {
                _session.SetPlaying(false);
                _session.StepFrame(n);
            };
            _previewView.KeyJumpClicked += d => _session.JumpToKey(d);
            _previewView.SetModes(SnapPref.Scrub, SnapPref.Key, SnapPref.PlaybackSpeed);
            _dopesheet.ScrubMode = SnapPref.Scrub;
            _dopesheet.KeyMode = SnapPref.Key;
            _previewView.ScrubModeChanged += m =>
            {
                SnapPref.Scrub = m;
                _dopesheet.ScrubMode = m;
            };
            _previewView.KeyModeChanged += m =>
            {
                SnapPref.Key = m;
                _dopesheet.KeyMode = m;
            };
            _previewView.SpeedChanged += v => SnapPref.PlaybackSpeed = v;
            _previewView.LoopClicked += () =>
            {
                _session.Loop = !_session.Loop;
                RefreshTransport();
            };
            _dopesheet.Scrubbed += s =>
            {
                _session.SetPlaying(false);
                _session.SetTime(s);
            };
            _dopesheet.SelectionRequested += keys => _session.Select(keys);
            _dopesheet.MoveRequested += (keys, delta) => _session.MoveKeys(keys, delta);
            _dopesheet.AddRequested += (def, t) => _session.AddKey(def, t);
            _dopesheet.AddAtPlayheadClicked += _ => AddEventAtPlayhead();
            _dopesheet.DeleteRequested += () => _session.DeleteKeys(_session.SelectedKeys.ToList());
            _dopesheet.DefinitionClicked += _session.SelectDefinition;
            _inspector.KeyDefinitionChanged += _session.SetKeyDefinition;
            _inspector.KeyTimeChanged += _session.SetKeyTime;
            _inspector.KeyValuesChanged += _session.SetKeyValues;
            _inspector.DeleteKeysClicked += () => _session.DeleteKeys(_session.SelectedKeys.ToList());
            _inspector.DefinitionChanged += (def, name, i, f, s) => _session.UpdateDefinition(def, name, i, f, s);
            _origin.LinkClicked += _actions.LinkOrigin;
            _origin.UnlinkClicked += _actions.UnlinkOrigin;
            _origin.RefreshClicked += _actions.RefreshOrigin;
            _origin.PushClicked += _actions.PushOrigin;
            _actionBar.RevertClicked += _actions.Revert;
            _actionBar.ApplyClicked += _actions.ApplyFromWindow;
            _actions.SpineWriteConfirm = (path, done) => _confirm.Show(Loc.T("Spine 원본을 덮어씁니다"), EventFileWriter.SpineBusyMessage,
                Loc.T("적용하기"), Loc.T("취소"), done);

            _session.SourceChanged += RefreshAll;
            _session.DataChanged += RefreshData;
            _session.SelectionChanged += RefreshSelection;
            _session.AnimationChanged += RefreshAll;
            _session.TimeChanged += RefreshTime;
            _session.PlayingChanged += RefreshTransport;
            _session.OriginChanged += RefreshData;
            _session.Status += _actionBar.SetStatus;
            _actions.PreviewChanged += message =>
            {
                _previewView.SetMessage(message);
                _previewView.SetBusy(_actions.PreviewBusy);
                _previewView.RefreshSkins();
                RefreshAll();
            };
            _clock.Passed += keys =>
            {
                _dopesheet.Flash(keys);
                foreach (EventKey k in keys)
                {
                    _previewView.Flash(k.Name);
                }
            };
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown);
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => FitPanes());
            TrackDopesheetHeight();
        }

        // ------------------------------------------------------------ 갱신

        void RefreshAll()
        {
            EventSource source = _session.Source;
            _window.EnableInClassList(ClassEmpty, source == null);
            RefreshData();
            RefreshTime();
            RefreshTransport();
        }

        void RefreshData()
        {
            EventSource source = _session.Source;
            _sourceBar.Show(source, _actions.UnityEventCount);
            if (source == null)
            {
                _origin.Show(null, null, null);
                _actionBar.SetChanges(false, Loc.T("적용하기"));
                return;
            }

            EventSetDiff diff = EventSetDiff.Compute(source.Original, source.Working);
            var dirty = new HashSet<EventKey>(diff.AddedKeys.Concat(diff.MovedKeys.Select(m => m.After)).Concat(diff.RetunedKeys.Select(m => m.After)));
            var unsynced = new HashSet<EventKey>();
            var unsyncedDefs = new List<string>();
            if (_session.Origin?.Error == null && _session.Origin != null)
            {
                foreach (EventKey missing in _session.Origin.MissingKeys)
                {
                    unsynced.UnionWith(source.Working.Keys.Where(k => k.Animation == missing.Animation && k.Name == missing.Name &&
                                                                      Math.Abs(k.Seconds - missing.Seconds) < 1e-6 && k.SameValues(missing)));
                }

                unsyncedDefs.AddRange(_session.Origin.MissingDefinitions.Select(d => d.Name));
            }

            _animations.Show(source.Working, _session.Animation);
            _animations.ShowSkeletons(source.Skeletons, source.Skeleton);
            _definitions.Show(source.Working, source.Original, _session.SelectedDefinition, unsyncedDefs);
            _dopesheet.Bind(source.Working, _session.Animation, _session.Duration, dirty, unsynced);
            _dopesheet.SetSelection(_session.SelectedKeys);
            _inspector.Show(source.Working, _session.SelectedKeys, _session.SelectedDefinition);
            _origin.Show(source, _session.OriginPath, _session.Origin);
            bool isDirty = diff.IsEmpty == false;
            _actionBar.SetChanges(isDirty, source.IsSpineProject ? Loc.T("원본에 적용") : Loc.T("적용하기"));
            // 탭의 «저장 안 됨» 표시와 창을 닫을 때의 확인은 Unity 가 hasUnsavedChanges 로 처리한다
            hasUnsavedChanges = isDirty;
            saveChangesMessage = Loc.T("{0}에 적용하지 않은 이벤트 변경이 있습니다.\n{1}", source.DisplayName, EventFileWriter.Describe(diff));
        }

        void RefreshSelection()
        {
            if (_session.Source == null)
            {
                return;
            }

            _dopesheet.SetSelection(_session.SelectedKeys);
            _inspector.Show(_session.Source.Working, _session.SelectedKeys, _session.SelectedDefinition);
            _definitions.Show(_session.Source.Working, _session.Source.Original, _session.SelectedDefinition,
                _session.Origin?.MissingDefinitions.Select(d => d.Name).ToList());
        }

        void RefreshTime()
        {
            _dopesheet.SetTime(_session.Time);
            _previewView.RequestRender(_session.Animation, (float)_session.Time);

            RefreshTransport();
        }

        void RefreshTransport()
        {
            _previewView.SetState(_session.Playing, _session.Loop);
        }

        /// <summary>
        /// 창이 좁으면 양옆 칸을 줄이고, 그래도 좁으면 속성 칸을 접는다(가운데 미리보기·도프시트가 0 이 되지 않게).
        /// 칸 너비는 계산한 값이라 style 로 넣는다.
        /// </summary>
        void FitPanes()
        {
            float width = rootVisualElement.layout.width;
            if (float.IsNaN(width) || width <= 0f)
            {
                return;
            }

            var main = rootVisualElement.Q<TwoPaneSplitView>("main");
            var right = rootVisualElement.Q<TwoPaneSplitView>("right");
            VisualElement sidebar = rootVisualElement.Q("sidebar");
            const float minCenter = 320f;
            float side = sidebar.layout.width;
            float inspector = _inspector.layout.width;
            if (width < 760f)
            {
                right.CollapseChild(1);
                inspector = 0f;
            }
            else if (right.fixedPane != null && right.fixedPane.resolvedStyle.display == DisplayStyle.None)
            {
                right.UnCollapse();
            }

            if (width - side - inspector < minCenter)
            {
                float target = Mathf.Max(150f, (width - minCenter) * 0.45f);
                main.fixedPane.style.width = target;
                if (inspector > 0f)
                {
                    right.fixedPane.style.width = Mathf.Max(200f, width - target - minCenter);
                }
            }
        }

        /// <summary>
        /// 창이 비어 있는 동안(main 이 display:none) 세로 분할 뷰가 도프시트를 최소 높이로 눌러 둔다.
        /// 보이는 동안의 높이를 기억해 두었다가 다시 보일 때 되돌린다.
        /// </summary>
        void TrackDopesheetHeight()
        {
            var center = rootVisualElement.Q<TwoPaneSplitView>("center");
            _dopesheetHeight = center.fixedPaneInitialDimension;
            center.RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (e.newRect.height <= 0f)
                {
                    _centerHidden = true;
                }
                else if (_centerHidden)
                {
                    _centerHidden = false;
                    _dopesheet.style.height = _dopesheetHeight;
                    // 높이만 바꾸면 분할선 손잡이는 눌렸던 자리에 남는다 — 분할 뷰가 자기 배치를 끝낸 다음 틱에 함께 옮긴다
                    center.schedule.Execute(() =>
                    {
                        VisualElement anchor = center.Q(className: "unity-two-pane-split-view__dragline-anchor");
                        if (anchor != null)
                        {
                            anchor.style.top = center.layout.height - _dopesheet.layout.height;
                        }
                    });
                }
            });
            _dopesheet.RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (_centerHidden == false && e.newRect.height >= _dopesheet.resolvedStyle.minHeight.value)
                {
                    _dopesheetHeight = e.newRect.height;
                }
            });
        }

        // ------------------------------------------------------------ 입력

        void OnProjectSelection()
        {
            Object picked = Selection.activeObject;
            if (picked == null || hasFocus || EventSourceLoader.CanOpen(picked) == false)
            {
                return;
            }

            if (_session.Source?.Preview == picked || (_session.Source?.AssetPath != null && AssetDatabase.GetAssetPath(picked) == _session.Source.AssetPath))
            {
                return;
            }

            _actions.OpenAsset(picked);
        }

        void OnImported(string assetPath) => _actions.OnImported(assetPath);

        void OnKeyDown(KeyDownEvent e)
        {
            if (_confirm.IsOpen)
            {
                return;
            }

            var help = rootVisualElement.Q<HelpOverlay>("help");
            if (e.keyCode == KeyCode.Escape && help.IsOpen)
            {
                help.Close();
                e.StopPropagation();
                return;
            }

            if (_session.Source == null || rootVisualElement.focusController?.focusedElement is TextElement ||
                e.target is TextElement || (e.target as VisualElement)?.GetFirstAncestorOfType<TextField>() != null)
            {
                return;
            }

            bool action = e.actionKey;
            switch (e.keyCode)
            {
                case KeyCode.Z when action && e.shiftKey:
                case KeyCode.Y when action:
                    _session.Source.Redo();
                    break;
                case KeyCode.Z when action:
                    _session.Source.Undo();
                    break;
                case KeyCode.S when action:
                    _actions.ApplyFromWindow();
                    break;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    _session.DeleteKeys(_session.SelectedKeys.ToList());
                    break;
                case KeyCode.Space:
                    _clock.Toggle();
                    break;
                case KeyCode.LeftArrow:
                    _session.SetPlaying(false);
                    _session.StepFrame(-1);
                    break;
                case KeyCode.RightArrow:
                    _session.SetPlaying(false);
                    _session.StepFrame(1);
                    break;
                case KeyCode.Comma:
                    _session.JumpToKey(-1);
                    break;
                case KeyCode.Period:
                    _session.JumpToKey(1);
                    break;
                case KeyCode.F:
                    _dopesheet.FitToView();
                    break;
                default:
                    return;
            }

            e.StopPropagation();
        }
    }
}
