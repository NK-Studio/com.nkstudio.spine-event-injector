using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace NKStudio.SpineEventInjector
{
    /// <summary>미리보기 재생 시계. 재생 중 시각을 진행시키고, 지나간 이벤트 키를 알린다(작업본 기준 — 적용 전 키도 보인다).</summary>
    sealed class PlaybackClock
    {
        readonly InjectorSession _session;
        readonly IVisualElementScheduledItem _tick;
        double _last;

        public event Action<List<EventKey>> Passed;

        public PlaybackClock(InjectorSession session, VisualElement host)
        {
            _session = session;
            _tick = host.schedule.Execute(Tick).Every(16);
            _tick.Pause();
            session.PlayingChanged += () =>
            {
                if (session.Playing)
                {
                    _last = EditorApplication.timeSinceStartup;
                    _tick.Resume();
                }
                else
                {
                    _tick.Pause();
                }
            };
        }

        public void Toggle()
        {
            if (_session.Source == null || _session.Animation == null)
            {
                return;
            }

            if (_session.Playing == false && _session.Time >= _session.Duration - 1e-6)
            {
                _session.SetTime(0);
            }

            _session.SetPlaying(!_session.Playing);
        }

        void Tick()
        {
            if (_session.Playing == false || _session.Source == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            double dt = Math.Min(now - _last, 0.1) * SnapPref.PlaybackSpeed;
            _last = now;
            double duration = Math.Max(_session.Duration, 1e-3);
            double t0 = _session.Time;
            double t1 = t0 + dt;
            List<EventKey> keys = _session.Source.Working.KeysOf(_session.Animation).ToList();
            var passed = keys.Where(k => k.Seconds > t0 && k.Seconds <= t1 || t0 == 0 && k.Seconds == 0).ToList();
            if (t1 >= duration)
            {
                if (_session.Loop)
                {
                    t1 -= duration;
                    passed.AddRange(keys.Where(k => k.Seconds <= t1));
                }
                else
                {
                    t1 = duration;
                    _session.SetPlaying(false);
                }
            }

            _session.SetTime(t1);
            if (passed.Count > 0)
            {
                Passed?.Invoke(passed);
            }
        }
    }
}
