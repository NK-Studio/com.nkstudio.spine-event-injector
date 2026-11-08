using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>
    /// 이벤트 심기에 맞춘 도프시트: 줄 = 이벤트 정의, 마름모 = 키. 눈금자를 누르거나 끌면 재생 헤드가 움직이고,
    /// 트랙을 더블클릭하면 키를 넣고, 키를 끌면 옮긴다. 데이터는 바꾸지 않고 요청 이벤트만 낸다(세션이 처리).
    /// </summary>
    [UxmlElement]
    public partial class EventDopesheet : VisualElement
    {
        const string AssetPath = "Components/EventDopesheet/EventDopesheet";
        const string ClassRoot = "event-dopesheet";
        const string ClassEmpty = "event-dopesheet--empty";

        internal readonly TimeAxis Axis = new TimeAxis();
        /// <summary>빈 키(이름 "")를 모아 보이는 맨 위 줄. 정의가 아니라 화면용이다.</summary>
        internal static readonly EventDefinition EmptyRow = new EventDefinition { Name = string.Empty };
        readonly ListView _rows;
        readonly TimeRuler _ruler;
        readonly VisualElement _corner;
        readonly VisualElement _playhead;
        readonly VisualElement _outside;
        readonly Label _time;
        readonly Label _frame;
        readonly List<DopesheetRow> _live = new List<DopesheetRow>();
        readonly List<EventDefinition> _defs = new List<EventDefinition>();
        readonly HashSet<EventKey> _selected = new HashSet<EventKey>();
        readonly HashSet<EventKey> _dirty = new HashSet<EventKey>();
        readonly HashSet<EventKey> _unsynced = new HashSet<EventKey>();
        readonly HashSet<EventKey> _flashing = new HashSet<EventKey>();
        EventSet _set;
        string _animation;
        double _seconds;
        bool _fitPending;
        Scroller _hscroll;
        bool _syncingScroll;

        enum DragMode
        {
            None,
            Keys,
            Scrub,
        }

        DragMode _drag;
        float _dragStartX;
        double _dragFrames;
        /// <summary>끄는 키(손으로 잡은 키)의 원래 프레임. 기본 끌기는 이 키가 정수 프레임에 놓이게 한다.</summary>
        double _dragAnchorFrame;
        EventKey _dragAnchorKey;
        HashSet<EventKey> _dragKeys;

        internal event Action<double> Scrubbed;
        internal event Action<List<EventKey>> SelectionRequested;
        internal event Action<List<EventKey>, double> MoveRequested;
        internal event Action<string, double> AddRequested;
        internal event Action DeleteRequested;
        /// <summary>«이벤트 키 추가» 버튼. 인자는 메뉴를 펼칠 버튼 영역(월드 좌표).</summary>
        internal event Action<Rect> AddAtPlayheadClicked;
        internal event Action<EventDefinition> DefinitionClicked;

        public EventDopesheet()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _rows = this.Q<ListView>("rows");
            _ruler = this.Q<TimeRuler>("ruler");
            _corner = this.Q("corner");
            _playhead = this.Q("playhead");
            _outside = this.Q("outside");
            _time = this.Q<Label>("time");
            _frame = this.Q<Label>("frame");
            Button addEvent = this.Q<Button>("add-event");
            addEvent.clicked += () => AddAtPlayheadClicked?.Invoke(addEvent.worldBound);
            _ruler.SetAxis(Axis);
            // 눈금자는 정수 프레임에 붙인다(Alt: 프레임 사이)
            _ruler.Scrubbed += (frame, free) => Scrub(frame, free);
            _ruler.Wheel += OnWheel;
            _hscroll = this.Q<Scroller>("hscroll");
            _hscroll.valueChanged += v =>
            {
                if (_syncingScroll == false && Math.Abs(v - Axis.ScrollFrame) > 0.0001f)
                {
                    Axis.ScrollFrame = Mathf.Max(0f, v);
                    OnAxisChanged();
                }
            };

            _rows.itemsSource = _defs;
            _rows.makeItem = MakeRow;
            _rows.bindItem = BindRow;
            _rows.unbindItem = (e, i) => _live.Remove((DopesheetRow)e);

            Axis.Changed += OnAxisChanged;
            this.Q("body").RegisterCallback<WheelEvent>(e => OnWheel(e, _ruler.WorldToLocal(e.mousePosition).x), TrickleDown.TrickleDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                EndDrag(false);
                SetSnapKey(null);
            });
            RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_fitPending && _ruler.layout.width > 0)
                {
                    _fitPending = false;
                    FitAxis();
                }

                PlacePlayhead();
            });
        }

        // ------------------------------------------------------------ 데이터

        internal void Bind(EventSet set, string animation, float durationSeconds, ISet<EventKey> dirty, ISet<EventKey> unsynced)
        {
            float fps = set?.Fps ?? 30f;
            float frames = durationSeconds * fps;
            // 애니메이션이 바뀌었거나 길이가 나중에 알려졌으면(미리보기 준비 완료) 다시 맞춘다
            bool newAnimation = animation != _animation || set != _set && _set == null || Math.Abs(frames - Axis.DurationFrames) > 0.01f;
            _set = set;
            _animation = animation;
            Axis.Fps = fps;
            Axis.DurationFrames = frames;
            _dirty.Clear();
            _unsynced.Clear();
            if (dirty != null) _dirty.UnionWith(dirty);
            if (unsynced != null) _unsynced.UnionWith(unsynced);
            _defs.Clear();
            if (set != null)
            {
                if (animation != null && set.Keys.Any(k => k.Animation == animation && k.Name.Length == 0))
                {
                    _defs.Add(EmptyRow);
                }

                _defs.AddRange(set.Definitions);
            }

            EnableInClassList(ClassEmpty, set != null && _defs.Count == 0);
            _rows.RefreshItems();
            if (newAnimation)
            {
                FitToView();
            }

            _ruler.Refresh();
            UpdateTimeLabel();
        }

        internal void SetSelection(IEnumerable<EventKey> keys)
        {
            _selected.Clear();
            _selected.UnionWith(keys);
            RefreshStates();
        }

        internal void SetTime(double seconds)
        {
            _seconds = seconds;
            _ruler.SetPlayhead(seconds * Axis.Fps);
            PlacePlayhead();
            UpdateTimeLabel();
        }

        /// <summary>재생 중 지나간 키를 잠깐 키운다.</summary>
        internal void Flash(IEnumerable<EventKey> keys)
        {
            _flashing.Clear();
            _flashing.UnionWith(keys);
            RefreshStates();
            if (_flashing.Count > 0)
            {
                schedule.Execute(() =>
                {
                    _flashing.Clear();
                    RefreshStates();
                }).StartingIn(180);
            }
        }

        void FitAxis()
        {
            Axis.Fit(_ruler.layout.width);
            SyncFooter();
        }

        /// <summary>그 정의의 줄이 보이게 스크롤한다.</summary>
        internal void RevealRow(string definition)
        {
            int i = _defs.FindIndex(d => d.Name == definition);
            if (i >= 0)
            {
                schedule.Execute(() => _rows.ScrollToItem(i));
            }
        }

        internal void FitToView()
        {
            // 레이아웃이 아직이면 다음 프레임에 다시(레이아웃 변화를 기다리면 길이만 바뀐 경우 영영 안 맞춰진다)
            schedule.Execute(() =>
            {
                if (_ruler.layout.width > 0 && float.IsNaN(_ruler.layout.width) == false)
                {
                    FitAxis();
                }
                else
                {
                    _fitPending = true;
                }
            });
        }

        IReadOnlyList<EventKey> KeysOf(EventDefinition def) =>
            _set == null || _animation == null
                ? Array.Empty<EventKey>()
                : (IReadOnlyList<EventKey>)_set.Keys.Where(k => k.Animation == _animation && k.Name == def.Name).OrderBy(k => k.Seconds).ToList();

        VisualElement MakeRow()
        {
            var row = new DopesheetRow(Axis);
            row.KeyPointerDown += OnKeyPointerDown;
            row.TrackPointerDown += OnTrackPointerDown;
            row.TrackMenu += OnTrackMenu;
            row.LabelClicked += r =>
            {
                if (r.Definition != EmptyRow)
                {
                    DefinitionClicked?.Invoke(r.Definition);
                }
            };
            return row;
        }

        void BindRow(VisualElement element, int index)
        {
            var row = (DopesheetRow)element;
            EventDefinition def = _defs[index];
            row.Bind(def, KeysOf(def));
            row.EnableInClassList(DopesheetRow.ClassOdd, index % 2 == 1);
            row.SetStates(_selected, _dirty, _unsynced, _flashing);
            if (_live.Contains(row) == false)
            {
                _live.Add(row);
            }
        }

        void RefreshStates()
        {
            foreach (DopesheetRow row in _live)
            {
                row.SetStates(_selected, _dirty, _unsynced, _flashing);
            }
        }

        /// <summary>가로 스크롤 막대를 축에 맞춘다.</summary>
        void SyncFooter()
        {
            float w = _ruler.layout.width;
            if (float.IsNaN(w) || w <= 0)
            {
                return;
            }

            float visible = (w - TimeAxis.Inset) / Axis.PxPerFrame;
            // «전체 보기» 때 남기는 오른쪽 여백(36px, TimeAxis.Fit)까지가 끝이다 — 전체 보기면 스크롤할 것이 없다
            float max = Mathf.Max(0f, Axis.DurationFrames + 36f / Axis.PxPerFrame - visible);
            if (max < 0.01f)
            {
                max = 0f;
            }

            max = Mathf.Max(max, Axis.ScrollFrame);
            _syncingScroll = true;
            _hscroll.lowValue = 0f;
            _hscroll.highValue = max;
            _hscroll.Adjust(Mathf.Clamp01(visible / Mathf.Max(visible + max, 0.0001f)));
            _hscroll.value = Axis.ScrollFrame;
            _hscroll.SetEnabled(max > 0.01f);
            _syncingScroll = false;
        }

        void OnAxisChanged()
        {
            SyncFooter();
            foreach (DopesheetRow row in _live)
            {
                row.Relayout(_dragKeys, (float)(_dragFrames * Axis.PxPerFrame));
            }

            _ruler.Refresh();
            PlacePlayhead();
        }

        void PlacePlayhead()
        {
            float endX = _corner.layout.width + Axis.X(Axis.DurationFrames);
            _outside.style.left = Mathf.Max(_corner.layout.width, endX);
            _outside.style.display = endX < layout.width ? DisplayStyle.Flex : DisplayStyle.None;

            float x = Axis.X(_seconds * Axis.Fps);
            float left = _corner.layout.width + x;
            bool visible = x >= 0 && left < layout.width;
            _playhead.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _playhead.style.left = Mathf.Round(left);
        }

        void UpdateTimeLabel()
        {
            double frame = _seconds * Axis.Fps;
            _frame.text = $"{frame:0.#}";
            _time.text = $"/ {Axis.DurationFrames:0.#}f · {_seconds:0.000}s";
        }

        // ------------------------------------------------------------ 입력

        const float MagnetPx = 8f;
        const string ClassSnapped = "event-dopesheet--snapped";
        EventKey _snapKey;

        /// <summary>재생 헤드 스냅 모드(도구 막대). Option/Alt 를 누르는 동안은 자유.</summary>
        internal ScrubSnap ScrubMode = ScrubSnap.Frame;
        /// <summary>키 끌기 스냅 모드(도구 막대). Option/Alt 를 누르는 동안은 자유.</summary>
        internal KeySnap KeyMode = KeySnap.Frame;

        /// <summary>
        /// 재생 헤드 옮기기.
        /// 자유: 그대로 · 프레임: 가까운(MagnetPx 안) 키에 붙고 없으면 정수 프레임 · 키: 가장 가까운 키(또는 처음·끝)에만.
        /// </summary>
        void Scrub(double frame, bool free)
        {
            ScrubSnap mode = free ? ScrubSnap.Free : ScrubMode;
            EventKey snap = mode == ScrubSnap.Free
                ? null
                : NearestKey(frame, mode == ScrubSnap.Key ? double.MaxValue : MagnetPx / Math.Max(Axis.PxPerFrame, 0.0001f));
            double target = frame;
            if (mode == ScrubSnap.Key && snap != null)
            {
                // 처음·끝이 키보다 가까우면 거기로
                double keyFrame = snap.Seconds * Axis.Fps;
                double nearestEnd = frame < Axis.DurationFrames - frame ? 0 : Axis.DurationFrames;
                if (Math.Abs(nearestEnd - frame) < Math.Abs(keyFrame - frame))
                {
                    snap = null;
                    target = nearestEnd;
                }
                else
                {
                    target = keyFrame;
                }
            }
            else if (snap != null)
            {
                target = snap.Seconds * Axis.Fps;
            }
            else if (mode != ScrubSnap.Free)
            {
                target = Math.Round(frame);
            }

            target = Math.Min(Math.Max(0, target), Axis.DurationFrames);
            SetSnapKey(snap);
            Scrubbed?.Invoke(target / Axis.Fps);
        }

        EventKey NearestKey(double frame, double range)
        {
            if (_set == null || _animation == null)
            {
                return null;
            }

            EventKey nearest = null;
            double best = range;
            foreach (EventKey k in _set.Keys)
            {
                if (k.Animation != _animation)
                {
                    continue;
                }

                double d = Math.Abs(k.Seconds * Axis.Fps - frame);
                if (d <= best)
                {
                    best = d;
                    nearest = k;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 끄는 거리(프레임)를 모드에 맞춘다. 잡은 키의 원래 프레임이 anchor.
        /// 자유: 그대로 · 프레임: 잡은 키가 정수 프레임에 · 재생 헤드: 헤드 가까이면 헤드에, 아니면 정수 프레임에.
        /// </summary>
        internal static double KeyDragDelta(double raw, double anchor, KeySnap mode, bool alt, double playheadFrame, double magnetFrames)
        {
            if (alt || mode == KeySnap.Free)
            {
                return raw;
            }

            if (mode == KeySnap.Playhead && Math.Abs(anchor + raw - playheadFrame) <= magnetFrames)
            {
                return playheadFrame - anchor;
            }

            return Math.Max(0, Math.Round(anchor + raw)) - anchor;
        }

        void SetSnapKey(EventKey key)
        {
            if (ReferenceEquals(key, _snapKey))
            {
                return;
            }

            _snapKey = key;
            EnableInClassList(ClassSnapped, key != null);
            if (key != null)
            {
                Flash(new[] { key }); // 붙는 순간 키가 «톡» 커진다
            }
        }

        double SecondsAt(VisualElement track, Vector2 worldPosition, bool free)
        {
            double frame = Axis.Frame(track.WorldToLocal(worldPosition).x);
            if (free == false)
            {
                frame = Math.Round(frame);
            }

            return Math.Max(0, frame) / Axis.Fps;
        }

        void OnKeyPointerDown(DopesheetRow row, EventKey key, PointerDownEvent e)
        {
            if (e.button != 0)
            {
                return;
            }

            bool additive = e.shiftKey || e.actionKey;
            if (additive)
            {
                var next = new List<EventKey>(_selected);
                if (next.Remove(key) == false)
                {
                    next.Add(key);
                }

                SelectionRequested?.Invoke(next);
            }
            else if (_selected.Contains(key) == false)
            {
                SelectionRequested?.Invoke(new List<EventKey> { key });
                Scrubbed?.Invoke(key.Seconds);
            }

            if (_selected.Contains(key))
            {
                _drag = DragMode.Keys;
                _dragStartX = e.position.x;
                _dragAnchorFrame = key.Seconds * Axis.Fps;
                _dragAnchorKey = key;
                _dragFrames = 0;
                _dragKeys = new HashSet<EventKey>(_selected);
                this.CapturePointer(e.pointerId);
            }
        }

        void OnTrackPointerDown(DopesheetRow row, PointerDownEvent e)
        {
            if (e.button != 0)
            {
                return;
            }

            double seconds = SecondsAt(row.Track, e.position, e.altKey || KeyMode == KeySnap.Free);
            if (e.clickCount == 2)
            {
                AddRequested?.Invoke(row.Definition.Name, seconds);
                return;
            }

            if (e.shiftKey == false && e.actionKey == false)
            {
                SelectionRequested?.Invoke(new List<EventKey>());
            }

            Scrub(Axis.Frame(_ruler.WorldToLocal(e.position).x), e.altKey);
            _drag = DragMode.Scrub;
            this.CapturePointer(e.pointerId);
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            if (_drag == DragMode.Scrub && this.HasPointerCapture(e.pointerId))
            {
                Scrub(Axis.Frame(_ruler.WorldToLocal(e.position).x), e.altKey);
            }
            else if (_drag == DragMode.Keys && this.HasPointerCapture(e.pointerId))
            {
                double frames = (e.position.x - _dragStartX) / Axis.PxPerFrame;
                double playheadFrame = _seconds * Axis.Fps;
                double magnet = MagnetPx / Math.Max(Axis.PxPerFrame, 0.0001f);
                _dragFrames = KeyDragDelta(frames, _dragAnchorFrame, KeyMode, e.altKey, playheadFrame, magnet);
                bool onPlayhead = KeyMode == KeySnap.Playhead && e.altKey == false && Math.Abs(_dragAnchorFrame + _dragFrames - playheadFrame) < 1e-6;
                SetSnapKey(onPlayhead ? _dragAnchorKey : null);
                foreach (DopesheetRow row in _live)
                {
                    row.Relayout(_dragKeys, (float)(_dragFrames * Axis.PxPerFrame));
                }
            }
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (this.HasPointerCapture(e.pointerId))
            {
                this.ReleasePointer(e.pointerId);
            }

            EndDrag(true);
            SetSnapKey(null);
        }

        void EndDrag(bool commit)
        {
            if (_drag == DragMode.Keys && commit && Math.Abs(_dragFrames) > 1e-6 && _dragKeys != null)
            {
                MoveRequested?.Invoke(_dragKeys.ToList(), _dragFrames / Axis.Fps);
            }

            bool wasKeys = _drag == DragMode.Keys;
            _drag = DragMode.None;
            _dragKeys = null;
            _dragFrames = 0;
            if (wasKeys)
            {
                foreach (DopesheetRow row in _live)
                {
                    row.Relayout();
                }
            }
        }

        void OnTrackMenu(DopesheetRow row, ContextualMenuPopulateEvent e)
        {
            double at = SecondsAt(row.Track, e.mousePosition, false);
            string def = row.Definition.Name;
            string shown = def.Length == 0 ? Loc.T("(비어 있음)") : def;
            e.menu.AppendAction(Loc.T("여기에 '{0}' 키 추가 ({1:0}프레임)", shown, at * Axis.Fps), _ => AddRequested?.Invoke(def, at));
            e.menu.AppendAction(Loc.T("재생 헤드에 '{0}' 키 추가 ({1:0.#}프레임)", shown, _seconds * Axis.Fps), _ => AddRequested?.Invoke(def, _seconds));
            e.menu.AppendSeparator();
            e.menu.AppendAction(Loc.T("선택한 키 {0}개 삭제", _selected.Count), _ => DeleteRequested?.Invoke(),
                _selected.Count > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            e.menu.AppendAction(Loc.T("전체 보기(F)"), _ => FitToView());
            e.StopPropagation();
        }

        void OnWheel(WheelEvent e, float anchorX)
        {
            if (e.actionKey || e.ctrlKey)
            {
                Axis.Zoom(e.delta.y > 0 ? 1f / 1.15f : 1.15f, anchorX);
                e.StopPropagation();
            }
            else if (e.shiftKey || Math.Abs(e.delta.x) > Math.Abs(e.delta.y))
            {
                float d = Math.Abs(e.delta.x) > Math.Abs(e.delta.y) ? e.delta.x : e.delta.y;
                Axis.ScrollBy(d * 12f);
                e.StopPropagation();
            }
        }
    }
}
