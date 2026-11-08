using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>도프시트 한 줄 = 이벤트 정의 하나. 왼쪽 이름 칸, 오른쪽 트랙(격자 + 키 마름모).</summary>
    sealed class DopesheetRow : VisualElement
    {
        const string AssetPath = "Components/DopesheetRow/DopesheetRow";
        const string ClassRoot = "dopesheet-row";
        public const string ClassOdd = "dopesheet-row--odd";
        public const string ClassSelected = "dopesheet-row--selected";
        const string ClassKey = "dopesheet-row__key";
        const string ClassEmpty = "dopesheet-row--empty";
        const string ClassKeyDirty = "dopesheet-row__key--dirty";
        const string ClassKeyUnsynced = "dopesheet-row__key--unsynced";
        const string ClassKeySelected = "dopesheet-row__key--selected";
        const string ClassKeyFlash = "dopesheet-row__key--flash";
        static readonly CustomStyleProperty<Color> Grid = new CustomStyleProperty<Color>("--sei-grid");
        static readonly CustomStyleProperty<Color> GridStrong = new CustomStyleProperty<Color>("--sei-grid-strong");
        static readonly CustomStyleProperty<Color> Range = new CustomStyleProperty<Color>("--sei-range");

        readonly Label _name;
        readonly VisualElement _track;
        readonly List<VisualElement> _keyPool = new List<VisualElement>();
        readonly List<EventKey> _keys = new List<EventKey>();
        readonly TimeAxis _axis;
        Color _grid = Color.gray, _gridStrong = Color.gray, _range = Color.clear;

        public EventDefinition Definition { get; private set; }
        public VisualElement Track => _track;

        /// <summary>키를 눌렀다(키, 이벤트).</summary>
        public event Action<DopesheetRow, EventKey, PointerDownEvent> KeyPointerDown;
        /// <summary>트랙 빈 곳을 눌렀다.</summary>
        public event Action<DopesheetRow, PointerDownEvent> TrackPointerDown;
        public event Action<DopesheetRow, ContextualMenuPopulateEvent> TrackMenu;
        public event Action<DopesheetRow> LabelClicked;

        public DopesheetRow(TimeAxis axis)
        {
            _axis = axis;
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _name = this.Q<Label>("name");
            _track = this.Q("track");
            _track.generateVisualContent += DrawGrid;
            _track.RegisterCallback<CustomStyleResolvedEvent>(OnStyle);
            _track.RegisterCallback<PointerDownEvent>(e => TrackPointerDown?.Invoke(this, e));
            _track.AddManipulator(new ContextualMenuManipulator(e => TrackMenu?.Invoke(this, e)));
            this.Q("label").RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0)
                {
                    LabelClicked?.Invoke(this);
                }
            });
        }

        public void Bind(EventDefinition def, IReadOnlyList<EventKey> keys)
        {
            Definition = def;
            _keys.Clear();
            _keys.AddRange(keys);
            bool empty = def.Name.Length == 0;
            _name.text = empty ? Loc.T("(비어 있음)") : def.Name;
            EnableInClassList(ClassEmpty, empty);
            // 잘린 이름도 볼 수 있게(안쪽 글자는 포인터를 받지 않는다)
            this.Q("label").tooltip = _name.text;
            for (int i = 0; i < _keys.Count; i++)
            {
                if (i == _keyPool.Count)
                {
                    var key = new VisualElement { pickingMode = PickingMode.Position };
                    key.AddToClassList(ClassKey);
                    key.RegisterCallback<PointerDownEvent>(OnKeyPointerDown);
                    _track.Add(key);
                    _keyPool.Add(key);
                }

                VisualElement el = _keyPool[i];
                el.userData = _keys[i];
                el.style.display = DisplayStyle.Flex;
            }

            for (int i = _keys.Count; i < _keyPool.Count; i++)
            {
                _keyPool[i].style.display = DisplayStyle.None;
                _keyPool[i].userData = null;
            }

            Relayout();
        }

        void OnKeyPointerDown(PointerDownEvent e)
        {
            if ((e.currentTarget as VisualElement)?.userData is EventKey key)
            {
                KeyPointerDown?.Invoke(this, key, e);
                e.StopPropagation();
            }
        }

        /// <summary>키 위치를 시간 축에 맞춘다. dragOffsetPx 는 끌고 있는 키(dragging)에만 더한다.</summary>
        public void Relayout(ISet<EventKey> dragging = null, float dragOffsetPx = 0f)
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                EventKey k = _keys[i];
                float x = _axis.X(k.Seconds * _axis.Fps);
                if (dragging != null && dragging.Contains(k))
                {
                    x += dragOffsetPx;
                }

                _keyPool[i].style.left = Mathf.Round(x); // 9px 키의 가운데 = 픽셀 가운데(재생 헤드·눈금과 같은 자리)
                _keyPool[i].tooltip = Loc.T("{0} · {1:0.##}프레임 ({2:0.###}초)", k.Name, k.Seconds * _axis.Fps, k.Seconds) +
                                      (k.Int != 0 ? $" · int {k.Int}" : "") + (k.Float != 0 ? $" · float {k.Float:0.###}" : "") +
                                      (string.IsNullOrEmpty(k.String) ? "" : $" · \"{k.String}\"");
            }

            _track.MarkDirtyRepaint();
        }

        public void SetStates(ISet<EventKey> selected, ISet<EventKey> dirty, ISet<EventKey> unsynced, ISet<EventKey> flashing)
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                EventKey k = _keys[i];
                VisualElement el = _keyPool[i];
                el.EnableInClassList(ClassKeySelected, selected.Contains(k));
                el.EnableInClassList(ClassKeyDirty, dirty.Contains(k));
                el.EnableInClassList(ClassKeyUnsynced, unsynced.Contains(k));
                el.EnableInClassList(ClassKeyFlash, flashing.Contains(k));
            }
        }

        void OnStyle(CustomStyleResolvedEvent e)
        {
            e.customStyle.TryGetValue(Grid, out _grid);
            e.customStyle.TryGetValue(GridStrong, out _gridStrong);
            e.customStyle.TryGetValue(Range, out _range);
            _track.MarkDirtyRepaint();
        }

        void DrawGrid(MeshGenerationContext mgc)
        {
            Rect r = _track.contentRect;
            Painter2D p = mgc.painter2D;
            float end = _axis.X(_axis.DurationFrames);
            if (end < r.width)
            {
                p.fillColor = _range;
                p.BeginPath();
                p.MoveTo(new Vector2(Mathf.Max(0, end), 0));
                p.LineTo(new Vector2(r.width, 0));
                p.LineTo(new Vector2(r.width, r.height));
                p.LineTo(new Vector2(Mathf.Max(0, end), r.height));
                p.ClosePath();
                p.Fill();
            }

            int minor = _axis.Step(6f);
            int major = _axis.Step(46f);
            p.lineWidth = 1f;
            int first = Mathf.Max(0, (int)Math.Floor(_axis.ScrollFrame / minor) * minor);
            for (int frame = first; _axis.X(frame) < r.width; frame += minor)
            {
                float x = Mathf.Round(_axis.X(frame)) + 0.5f;
                p.strokeColor = frame % major == 0 ? _gridStrong : _grid;
                p.BeginPath();
                p.MoveTo(new Vector2(x, 0));
                p.LineTo(new Vector2(x, r.height));
                p.Stroke();
            }
        }
    }
}
