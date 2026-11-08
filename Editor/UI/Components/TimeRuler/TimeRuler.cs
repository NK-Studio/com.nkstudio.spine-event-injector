using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>프레임 눈금자. 눈금·재생 헤드·애니메이션 끝 이후 음영을 그리고, 누르거나 끌면 재생 헤드를 옮긴다.</summary>
    [UxmlElement]
    public partial class TimeRuler : VisualElement
    {
        const string AssetPath = "Components/TimeRuler/TimeRuler";
        const string ClassRoot = "time-ruler";
        const string ClassLabel = "time-ruler__label";
        static readonly CustomStyleProperty<Color> TickColor = new CustomStyleProperty<Color>("--sei-tick");
        static readonly CustomStyleProperty<Color> TickMinorColor = new CustomStyleProperty<Color>("--sei-tick-minor");
        static readonly CustomStyleProperty<Color> PlayheadColor = new CustomStyleProperty<Color>("--sei-playhead-color");
        static readonly CustomStyleProperty<Color> RangeColor = new CustomStyleProperty<Color>("--sei-range-color");
        static readonly CustomStyleProperty<Color> BandColor = new CustomStyleProperty<Color>("--sei-band-color");
        static readonly CustomStyleProperty<Color> EndColor = new CustomStyleProperty<Color>("--sei-end-color");

        readonly VisualElement _labels;
        readonly List<Label> _pool = new List<Label>();
        TimeAxis _axis;
        double _playheadFrame;
        Color _tick = Color.gray, _tickMinor = Color.gray, _playhead = Color.blue, _range = Color.clear, _band = Color.blue, _end = Color.gray;

        /// <summary>눌러서·끌어서 고른 프레임.</summary>
        internal event Action<double, bool> Scrubbed;
        internal event Action<WheelEvent, float> Wheel;

        public TimeRuler()
        {
            AddToClassList(ClassRoot);
            UiAssets.Build(this, AssetPath);
            _labels = this.Q("labels");
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyle);
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<WheelEvent>(e => Wheel?.Invoke(e, this.WorldToLocal(e.mousePosition).x));
        }

        internal void SetAxis(TimeAxis axis)
        {
            _axis = axis;
            Refresh();
        }

        internal void SetPlayhead(double frame)
        {
            _playheadFrame = frame;
            MarkDirtyRepaint();
        }

        internal void Refresh()
        {
            LayoutLabels();
            MarkDirtyRepaint();
        }

        void OnStyle(CustomStyleResolvedEvent e)
        {
            e.customStyle.TryGetValue(TickColor, out _tick);
            e.customStyle.TryGetValue(TickMinorColor, out _tickMinor);
            e.customStyle.TryGetValue(PlayheadColor, out _playhead);
            e.customStyle.TryGetValue(RangeColor, out _range);
            e.customStyle.TryGetValue(BandColor, out _band);
            e.customStyle.TryGetValue(EndColor, out _end);
            MarkDirtyRepaint();
        }

        void LayoutLabels()
        {
            if (_axis == null)
            {
                return;
            }

            int step = _axis.Step(46f);
            float width = layout.width;
            int first = Mathf.Max(0, (int)Math.Floor(_axis.ScrollFrame / step) * step);
            int used = 0;
            for (int frame = first; _axis.X(frame) < width && used < 64; frame += step)
            {
                if (used == _pool.Count)
                {
                    var label = new Label { pickingMode = PickingMode.Ignore };
                    label.AddToClassList(ClassLabel);
                    _labels.Add(label);
                    _pool.Add(label);
                }

                Label l = _pool[used++];
                l.text = frame.ToString();
                l.style.left = _axis.X(frame);
                l.style.display = DisplayStyle.Flex;
            }

            for (int i = used; i < _pool.Count; i++)
            {
                _pool[i].style.display = DisplayStyle.None;
            }
        }

        void Draw(MeshGenerationContext mgc)
        {
            if (_axis == null)
            {
                return;
            }

            Rect r = contentRect;
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

            // 재생 범위(0 ~ 끝) 띠와 끝 프레임 선
            float start = Mathf.Max(0f, _axis.X(0));
            float stop = Mathf.Min(r.width, end);
            if (stop > start)
            {
                p.fillColor = _band;
                p.BeginPath();
                p.MoveTo(new Vector2(start, r.height - 3f));
                p.LineTo(new Vector2(stop, r.height - 3f));
                p.LineTo(new Vector2(stop, r.height));
                p.LineTo(new Vector2(start, r.height));
                p.ClosePath();
                p.Fill();
            }

            if (end >= 0f && end <= r.width)
            {
                p.strokeColor = _end;
                p.lineWidth = 2f;
                p.BeginPath();
                p.MoveTo(new Vector2(end, 0));
                p.LineTo(new Vector2(end, r.height));
                p.Stroke();
            }

            int major = _axis.Step(46f);
            int minor = _axis.Step(6f);
            p.lineWidth = 1f;
            int first = Mathf.Max(0, (int)Math.Floor(_axis.ScrollFrame / minor) * minor);
            for (int frame = first; _axis.X(frame) < r.width; frame += minor)
            {
                bool isMajor = frame % major == 0;
                float x = Mathf.Round(_axis.X(frame)) + 0.5f;
                p.strokeColor = isMajor ? _tick : _tickMinor;
                p.BeginPath();
                p.MoveTo(new Vector2(x, isMajor ? 0 : r.height * 0.6f));
                p.LineTo(new Vector2(x, r.height));
                p.Stroke();
            }

            float px = Mathf.Round(_axis.X(_playheadFrame)) + 0.5f; // 도프시트 재생 헤드 선(1px)의 가운데
            p.fillColor = _playhead;
            p.BeginPath();
            p.MoveTo(new Vector2(px - 5f, r.height - 8f));
            p.LineTo(new Vector2(px + 5f, r.height - 8f));
            p.LineTo(new Vector2(px, r.height));
            p.ClosePath();
            p.Fill();
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (e.button != 0 || _axis == null)
            {
                return;
            }

            this.CapturePointer(e.pointerId);
            Scrubbed?.Invoke(_axis.Frame(e.localPosition.x), e.altKey);
            e.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            if (this.HasPointerCapture(e.pointerId))
            {
                Scrubbed?.Invoke(_axis.Frame(e.localPosition.x), e.altKey);
            }
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (this.HasPointerCapture(e.pointerId))
            {
                this.ReleasePointer(e.pointerId);
            }
        }
    }
}
