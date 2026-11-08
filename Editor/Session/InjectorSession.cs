using System;
using System.Collections.Generic;
using System.Linq;

namespace NKStudio.SpineEventInjector
{
    public enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>
    /// 창 하나의 상태: 불러온 대상, 고른 애니메이션, 재생 헤드 시각, 선택한 키·정의.
    /// UI 를 모른다. 컴포넌트는 이 객체의 이벤트를 듣고 메서드를 부른다.
    /// </summary>
    sealed class InjectorSession
    {
        public EventSource Source { get; private set; }
        public string Animation { get; private set; }
        public double Time { get; private set; }
        public bool Playing { get; private set; }
        public bool Loop { get; set; } = true;
        public readonly List<EventKey> SelectedKeys = new List<EventKey>();
        public EventDefinition SelectedDefinition { get; private set; }
        public OriginSync Origin { get; private set; }

        /// <summary>대상이 바뀌었다(열기·다시 읽기).</summary>
        public event Action SourceChanged;
        /// <summary>작업본이 바뀌었다(편집·되돌리기).</summary>
        public event Action DataChanged;
        public event Action SelectionChanged;
        public event Action AnimationChanged;
        public event Action TimeChanged;
        public event Action PlayingChanged;
        public event Action OriginChanged;
        public event Action<string, StatusKind> Status;

        public bool HasSource => Source != null;
        public float Fps => Source?.Working.Fps ?? 30f;
        public float Duration => Source == null || Animation == null ? 1f : Source.Duration(Animation);

        public void Open(EventSource source)
        {
            if (Source != null)
            {
                Source.Changed -= OnSourceChanged;
            }

            Source = source;
            SelectedKeys.Clear();
            SelectedDefinition = null;
            Playing = false;
            Time = 0;
            Animation = source?.Working.Animations.FirstOrDefault(a => source.Working.KeysOf(a).Any()) ?? source?.Working.Animations.FirstOrDefault();
            if (source != null)
            {
                source.Changed += OnSourceChanged;
            }

            RefreshOrigin();
            SourceChanged?.Invoke();
            AnimationChanged?.Invoke();
            TimeChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        void OnSourceChanged()
        {
            // 되돌리기·다시 읽기로 작업본 객체가 바뀌면 선택이 가리키던 키가 사라질 수 있다
            SelectedKeys.RemoveAll(k => Source.Working.Keys.Contains(k) == false);
            if (SelectedDefinition != null && Source.Working.Definitions.Contains(SelectedDefinition) == false)
            {
                SelectedDefinition = null;
            }

            DataChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        /// <summary>미리보기 등으로 대상 정보(길이·FPS)가 늘었을 때.</summary>
        public void NotifySourceInfoChanged() => SourceChanged?.Invoke();

        public void Report(string message, StatusKind kind = StatusKind.Info) => Status?.Invoke(message, kind);

        // ------------------------------------------------------------ 탐색

        public void SetAnimation(string animation)
        {
            if (animation == Animation || Source == null || Source.Working.Animations.Contains(animation) == false)
            {
                return;
            }

            Animation = animation;
            SelectedKeys.Clear();
            Time = 0;
            AnimationChanged?.Invoke();
            SelectionChanged?.Invoke();
            TimeChanged?.Invoke();
        }

        public void SetTime(double seconds)
        {
            double t = Math.Min(Math.Max(0, seconds), Duration); // 재생 범위(0 ~ 애니메이션 끝) 밖으로 나가지 않는다
            if (Math.Abs(t - Time) < 1e-9)
            {
                return;
            }

            Time = t;
            TimeChanged?.Invoke();
        }

        /// <summary>재생 헤드를 바로 앞(dir &lt; 0) 또는 뒤의 이벤트 키로 옮긴다. 없으면 그대로.</summary>
        public void JumpToKey(int dir)
        {
            if (Source == null || Animation == null)
            {
                return;
            }

            List<double> times = Source.Working.KeysOf(Animation).Select(k => k.Seconds).OrderBy(s => s).ToList();
            double now = Time;
            int found = dir < 0 ? times.FindLastIndex(s => s < now - 1e-6) : times.FindIndex(s => s > now + 1e-6);
            if (found >= 0)
            {
                SetPlaying(false);
                SetTime(times[found]);
            }
        }

        public void StepFrame(int frames) => SetTime(Math.Round(Time * Fps + frames) / Fps);

        public void SetPlaying(bool playing)
        {
            if (Playing == playing)
            {
                return;
            }

            Playing = playing;
            PlayingChanged?.Invoke();
        }

        // ------------------------------------------------------------ 선택

        public void Select(IEnumerable<EventKey> keys)
        {
            SelectedKeys.Clear();
            SelectedKeys.AddRange(keys.Distinct());
            if (SelectedKeys.Count > 0)
            {
                SelectedDefinition = null;
            }

            SelectionChanged?.Invoke();
        }

        public void SelectDefinition(EventDefinition def)
        {
            SelectedDefinition = def;
            SelectedKeys.Clear();
            SelectionChanged?.Invoke();
        }

        // ------------------------------------------------------------ 키 편집

        public EventKey AddKey(string definition, double seconds)
        {
            if (string.IsNullOrEmpty(definition))
            {
                return AddEmptyKey(seconds);
            }

            EventDefinition def = Source.Working.Definition(definition);
            if (def == null || Animation == null)
            {
                return null;
            }

            double t = Math.Max(0, seconds);
            if (Source.Working.KeysOf(Animation).Any(k => k.Name == definition && Math.Abs(k.Seconds - t) < 1e-6))
            {
                Report(Loc.T("{0} {1}프레임에 이미 '{2}' 키가 있습니다", Animation, Frame(t), definition), StatusKind.Warning);
                return null;
            }

            var key = new EventKey { Animation = Animation, Seconds = t, Name = def.Name, Int = def.Int, Float = def.Float, String = def.String };
            Source.Edit(set => set.Keys.Add(key));
            Select(new[] { key });
            return key;
        }

        /// <summary>
        /// 빈 키: 정의 없이 재생 헤드에 바로 넣는다(Unity 의 «No Function» 이벤트처럼). 이름이 "" 인 키.
        /// 오른쪽 속성에서 이벤트를 고르기 전에는 적용하지 않는다(Spine 키는 반드시 정의를 가리켜야 한다).
        /// </summary>
        public EventKey AddEmptyKey(double seconds)
        {
            if (Source == null || Animation == null)
            {
                return null;
            }

            double t = Math.Max(0, seconds);
            if (Source.Working.KeysOf(Animation).Any(k => k.Name.Length == 0 && Math.Abs(k.Seconds - t) < 1e-6))
            {
                Report(Loc.T("{0} {1}프레임에 이미 빈 키가 있습니다", Animation, Frame(t)), StatusKind.Warning);
                return null;
            }

            var key = new EventKey { Animation = Animation, Seconds = t, Name = string.Empty };
            Source.Edit(set => set.Keys.Add(key));
            Select(new[] { key });
            return key;
        }

        public int EmptyKeyCount => Source == null ? 0 : Source.Working.Keys.Count(k => k.Name.Length == 0);

        /// <summary>키들을 delta 초만큼 옮긴다(0 아래로는 가지 않는다). 같은 정의끼리 같은 시각에 겹치면 거부한다.</summary>
        public void MoveKeys(IReadOnlyCollection<EventKey> keys, double delta)
        {
            if (keys.Count == 0 || Math.Abs(delta) < 1e-9)
            {
                return;
            }

            double shift = Math.Max(delta, -keys.Min(k => k.Seconds));
            var moving = new HashSet<EventKey>(keys);
            foreach (EventKey k in keys)
            {
                double t = k.Seconds + shift;
                if (Source.Working.Keys.Any(o => moving.Contains(o) == false && o.Animation == k.Animation && o.Name == k.Name && Math.Abs(o.Seconds - t) < 1e-6))
                {
                    Report(Loc.T("{0}프레임에 이미 '{1}' 키가 있어 옮기지 않았습니다", Frame(t), k.Name), StatusKind.Warning);
                    return;
                }
            }

            Source.Edit(_ =>
            {
                foreach (EventKey k in keys)
                {
                    k.Seconds = Math.Round((k.Seconds + shift) * 1e6) / 1e6;
                }
            });
        }

        public void SetKeyTime(EventKey key, double seconds) => MoveKeys(new[] { key }, Math.Max(0, seconds) - key.Seconds);

        public void SetKeyValues(EventKey key, int intValue, float floatValue, string stringValue) => Source.Edit(_ =>
        {
            key.Int = intValue;
            key.Float = floatValue;
            key.String = stringValue ?? string.Empty;
        });

        /// <summary>키 값을 그 정의의 기본값으로.</summary>
        public void SetKeyDefinition(EventKey key, string definition)
        {
            if (string.IsNullOrEmpty(definition))
            {
                // 다시 빈 키로
                if (key.Name.Length > 0)
                {
                    Source.Edit(_ => key.Name = string.Empty);
                }

                return;
            }

            EventDefinition def = Source.Working.Definition(definition);
            if (def == null || key.Name == definition)
            {
                return;
            }

            if (Source.Working.Keys.Any(o => o != key && o.Animation == key.Animation && o.Name == definition && Math.Abs(o.Seconds - key.Seconds) < 1e-6))
            {
                Report(Loc.T("같은 자리에 이미 '{0}' 키가 있습니다", definition), StatusKind.Warning);
                return;
            }

            Source.Edit(set =>
            {
                key.Name = def.Name;
                key.Int = def.Int;
                key.Float = def.Float;
                key.String = def.String;
            });
        }

        public void DeleteKeys(IReadOnlyCollection<EventKey> keys)
        {
            if (keys.Count == 0)
            {
                return;
            }

            var gone = new HashSet<EventKey>(keys);
            Source.Edit(set => set.Keys.RemoveAll(gone.Contains));
            Select(Array.Empty<EventKey>());
        }

        // ------------------------------------------------------------ 정의 편집

        public EventDefinition AddDefinition()
        {
            string name = "event";
            for (int i = 1; Source.Working.Definition(name) != null; i++)
            {
                name = "event" + i;
            }

            var def = new EventDefinition { Name = name };
            Source.Edit(set => set.Definitions.Add(def));
            SelectDefinition(def);
            return def;
        }

        public bool UpdateDefinition(EventDefinition def, string name, int intValue, float floatValue, string stringValue)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                Report(Loc.T("정의 이름이 비었습니다"), StatusKind.Warning);
                return false;
            }

            if (name != def.Name && Source.Working.Definition(name) != null)
            {
                Report(Loc.T("이벤트 정의 '{0}'가 이미 있습니다", name), StatusKind.Warning);
                return false;
            }

            Source.Edit(set =>
            {
                foreach (EventKey k in set.Keys.Where(k => k.Name == def.Name))
                {
                    k.Name = name;
                }

                def.Name = name;
                def.Int = intValue;
                def.Float = floatValue;
                def.String = stringValue ?? string.Empty;
            });
            return true;
        }

        /// <summary>정의들과 그 정의를 쓰는 키를 함께 지운다(되돌리기 한 번으로 돌아온다).</summary>
        public void DeleteDefinitions(IReadOnlyCollection<EventDefinition> defs)
        {
            if (Source == null || defs.Count == 0)
            {
                return;
            }

            var names = new HashSet<string>(defs.Select(d => d.Name));
            Source.Edit(set =>
            {
                set.Keys.RemoveAll(k => names.Contains(k.Name));
                set.Definitions.RemoveAll(d => names.Contains(d.Name));
            });
            SelectDefinition(null);
        }

        // ------------------------------------------------------------ 원본(.spine) 연결

        public string OriginPath => Source?.AssetPath != null && Source.IsSpineProject == false ? SourceLinkStore.GetOrigin(Source.AssetPath) : null;

        public void LinkOrigin(string spinePath)
        {
            if (Source?.AssetPath == null)
            {
                return;
            }

            SourceLinkStore.SetOrigin(Source.AssetPath, spinePath);
            RefreshOrigin();
        }

        public void RefreshOrigin()
        {
            string path = OriginPath;
            Origin = path == null ? null : OriginSync.Compare(Source.Original, path, Source.DisplayName);
            OriginChanged?.Invoke();
        }

        /// <summary>프레임 표시용 글자. 프레임 사이 키(6.5f 등)도 반올림하지 않고 그대로 보인다.</summary>
        public string Frame(double seconds) => (seconds * Fps).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
