using UnityEngine;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>12개 살이 시계 방향으로 흐려지며 도는 활동 표시기. 크기·색은 USS(.activity-indicator)에서 정한다.</summary>
    sealed class ActivityIndicator : VisualElement
    {
        const int Spokes = 12;
        const int StepMs = 83; // 한 바퀴 약 1초
        const float TailAlpha = 0.18f;
        static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--sei-activity-color");

        readonly IVisualElementScheduledItem _tick;
        Color _color = Color.gray;
        int _head;

        public ActivityIndicator()
        {
            AddToClassList("activity-indicator");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                e.customStyle.TryGetValue(ColorProperty, out _color);
                MarkDirtyRepaint();
            });
            _tick = schedule.Execute(() =>
            {
                _head = (_head + 1) % Spokes;
                MarkDirtyRepaint();
            }).Every(StepMs);
            _tick.Pause();
        }

        public bool Running
        {
            set
            {
                if (value)
                {
                    _tick.Resume();
                }
                else
                {
                    _tick.Pause();
                }
            }
        }

        void Draw(MeshGenerationContext context)
        {
            Rect r = contentRect;
            float radius = Mathf.Min(r.width, r.height) * 0.5f;
            if (radius <= 0f)
            {
                return;
            }

            Vector2 center = r.center;
            float width = radius * 0.17f;
            float outer = radius - width * 0.5f;
            float inner = radius * 0.5f;
            Painter2D p = context.painter2D;
            p.lineWidth = width;
            p.lineCap = LineCap.Round;
            for (int i = 0; i < Spokes; i++)
            {
                float angle = i * Mathf.PI * 2f / Spokes;
                var dir = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
                int age = (_head - i + Spokes) % Spokes;
                Color c = _color;
                c.a *= Mathf.Lerp(1f, TailAlpha, age / (Spokes - 1f));
                p.strokeColor = c;
                p.BeginPath();
                p.MoveTo(center + dir * inner);
                p.LineTo(center + dir * outer);
                p.Stroke();
            }
        }
    }
}
