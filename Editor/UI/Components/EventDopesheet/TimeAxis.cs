using System;
using UnityEngine;

namespace NKStudio.SpineEventInjector.UI
{
    /// <summary>도프시트의 시간 축: 프레임 ↔ 트랙 x 좌표, 확대(픽셀/프레임)와 가로 스크롤. 눈금자·행이 함께 쓴다.</summary>
    sealed class TimeAxis
    {
        /// <summary>0프레임 키가 잘리지 않게 두는 왼쪽 여백(그림 치수).</summary>
        public const float Inset = 10f;

        public float Fps = 30f;
        public float DurationFrames = 30f;
        public float PxPerFrame = 12f;
        public float ScrollFrame;

        public event Action Changed;

        public float X(double frame) => (float)((frame - ScrollFrame) * PxPerFrame + Inset);

        public double Frame(float x) => (x - Inset) / PxPerFrame + ScrollFrame;

        public void Fit(float width)
        {
            if (width <= Inset * 3f)
            {
                return;
            }

            // 오른쪽에 마지막 프레임 숫자가 잘리지 않을 여백(그림 치수)을 남긴다
            PxPerFrame = Mathf.Clamp((width - Inset - 36f) / Mathf.Max(DurationFrames, 1f), 1.5f, 80f);
            ScrollFrame = 0f;
            Changed?.Invoke();
        }

        /// <summary>anchorX(트랙 좌표) 아래 프레임이 그대로 있도록 확대·축소.</summary>
        public void Zoom(float factor, float anchorX)
        {
            double anchor = Frame(anchorX);
            PxPerFrame = Mathf.Clamp(PxPerFrame * factor, 1.5f, 120f);
            ScrollFrame = Mathf.Max(0f, (float)(anchor - (anchorX - Inset) / PxPerFrame));
            Changed?.Invoke();
        }

        public void ScrollBy(float pixels)
        {
            ScrollFrame = Mathf.Max(0f, ScrollFrame + pixels / PxPerFrame);
            Changed?.Invoke();
        }

        /// <summary>눈금 간격(프레임): 화면에서 minPixels 이상 벌어지는 1·2·5 배수.</summary>
        public int Step(float minPixels)
        {
            int[] ladder = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            foreach (int s in ladder)
            {
                if (s * PxPerFrame >= minPixels)
                {
                    return s;
                }
            }

            return 1200;
        }
    }
}
