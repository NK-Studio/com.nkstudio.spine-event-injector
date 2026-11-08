using System;
using System.Collections.Generic;
using System.Linq;

namespace NKStudio.SpineEventInjector
{
    public enum EventSourceKind
    {
        /// <summary>Spine 에디터 프로젝트(.spine).</summary>
        SpineProject,
        /// <summary>익스포트한 바이너리(.skel / .skel.bytes).</summary>
        SkeletonBinary,
        /// <summary>익스포트한 JSON(.json).</summary>
        SkeletonJson,
    }

    /// <summary>이벤트 정의(Spine «Events» 트리의 항목).</summary>
    public sealed class EventDefinition
    {
        /// <summary>읽었을 때 붙인 번호. 새로 만든 정의는 -1.</summary>
        public int Id = -1;
        public string Name = string.Empty;
        public int Int;
        public float Float;
        public string String = string.Empty;
        /// <summary>오디오 경로(익스포트 데이터만). null 이면 오디오 없음.</summary>
        public string AudioPath;
        public float Volume = 1f;
        public float Balance;

        public EventDefinition Clone() => (EventDefinition)MemberwiseClone();

        public bool SameValues(EventDefinition o) =>
            Name == o.Name && Int == o.Int && BitEquals(Float, o.Float) && (String ?? "") == (o.String ?? "") &&
            AudioPath == o.AudioPath && BitEquals(Volume, o.Volume) && BitEquals(Balance, o.Balance);

        internal static bool BitEquals(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }

    /// <summary>애니메이션 안의 이벤트 키 하나.</summary>
    public sealed class EventKey
    {
        /// <summary>읽었을 때 붙인 번호. 새로 만든 키는 -1.</summary>
        public int Id = -1;
        public string Animation = string.Empty;
        /// <summary>초. 화면은 초 × FPS 로 프레임을 보인다.</summary>
        public double Seconds;
        /// <summary>파일에 적힌 원래 시간(.spine 은 프레임, 익스포트는 초). 원본 키를 다시 찾을 때만 쓴다.</summary>
        public float NativeTime;
        public string Name = string.Empty;
        public int Int;
        public float Float;
        public string String = string.Empty;
        public float Volume = 1f;
        public float Balance;

        public EventKey Clone() => (EventKey)MemberwiseClone();

        public bool SameValues(EventKey o) =>
            Int == o.Int && EventDefinition.BitEquals(Float, o.Float) && (String ?? "") == (o.String ?? "") &&
            EventDefinition.BitEquals(Volume, o.Volume) && EventDefinition.BitEquals(Balance, o.Balance);

        public bool SamePlace(EventKey o) => Animation == o.Animation && Name == o.Name && Math.Abs(Seconds - o.Seconds) < 1e-6;
    }

    /// <summary>파일 하나의 이벤트 전부: 애니메이션 이름, 정의, 키.</summary>
    public sealed class EventSet
    {
        public float Fps = 30f;
        public List<string> Animations = new List<string>();
        public List<EventDefinition> Definitions = new List<EventDefinition>();
        public List<EventKey> Keys = new List<EventKey>();

        public EventSet Clone() => new EventSet
        {
            Fps = Fps,
            Animations = new List<string>(Animations),
            Definitions = Definitions.Select(d => d.Clone()).ToList(),
            Keys = Keys.Select(k => k.Clone()).ToList(),
        };

        public EventDefinition Definition(string name) => Definitions.FirstOrDefault(d => d.Name == name);

        public IEnumerable<EventKey> KeysOf(string animation) =>
            Keys.Where(k => k.Animation == animation).OrderBy(k => k.Seconds);

        public int Frame(double seconds) => (int)Math.Round(seconds * Fps);
    }

    /// <summary>읽은 집합(원본)과 고친 집합의 차이.</summary>
    public sealed class EventSetDiff
    {
        public readonly List<EventDefinition> AddedDefinitions = new List<EventDefinition>();
        public readonly List<(EventDefinition Before, EventDefinition After)> ChangedDefinitions = new List<(EventDefinition, EventDefinition)>();
        public readonly List<EventDefinition> RemovedDefinitions = new List<EventDefinition>();
        public readonly List<EventKey> AddedKeys = new List<EventKey>();
        /// <summary>자리(애니메이션·시간·정의)는 그대로이고 값만 바뀐 키.</summary>
        public readonly List<(EventKey Before, EventKey After)> RetunedKeys = new List<(EventKey, EventKey)>();
        /// <summary>자리가 바뀐 키(옮김·정의 바꿈): 원본을 지우고 새로 넣는다.</summary>
        public readonly List<(EventKey Before, EventKey After)> MovedKeys = new List<(EventKey, EventKey)>();
        public readonly List<EventKey> RemovedKeys = new List<EventKey>();

        public bool IsEmpty =>
            AddedDefinitions.Count + ChangedDefinitions.Count + RemovedDefinitions.Count +
            AddedKeys.Count + RetunedKeys.Count + MovedKeys.Count + RemovedKeys.Count == 0;

        public static EventSetDiff Compute(EventSet original, EventSet edited)
        {
            var diff = new EventSetDiff();
            Dictionary<int, EventDefinition> defs = original.Definitions.ToDictionary(d => d.Id);
            var renamed = new Dictionary<string, string>();
            foreach (EventDefinition after in edited.Definitions)
            {
                if (after.Id < 0 || defs.TryGetValue(after.Id, out EventDefinition before) == false)
                {
                    diff.AddedDefinitions.Add(after);
                }
                else if (before.SameValues(after) == false)
                {
                    diff.ChangedDefinitions.Add((before, after));
                    if (before.Name != after.Name)
                    {
                        renamed[before.Name] = after.Name;
                    }
                }
            }

            var keptDefs = new HashSet<int>(edited.Definitions.Select(d => d.Id));
            diff.RemovedDefinitions.AddRange(original.Definitions.Where(d => keptDefs.Contains(d.Id) == false));

            Dictionary<int, EventKey> keys = original.Keys.ToDictionary(k => k.Id);
            foreach (EventKey after in edited.Keys)
            {
                if (after.Id < 0 || keys.TryGetValue(after.Id, out EventKey before) == false)
                {
                    diff.AddedKeys.Add(after);
                    continue;
                }

                // 정의 이름이 바뀌었으면 원본 키의 이름도 새 이름으로 본다(정의만 바뀐 것은 키 변경이 아니다)
                string beforeName = renamed.TryGetValue(before.Name, out string n) ? n : before.Name;
                bool samePlace = before.Animation == after.Animation && beforeName == after.Name &&
                                 Math.Abs(before.Seconds - after.Seconds) < 1e-6;
                if (samePlace == false)
                {
                    diff.MovedKeys.Add((before, after));
                }
                else if (before.SameValues(after) == false)
                {
                    diff.RetunedKeys.Add((before, after));
                }
            }

            var keptKeys = new HashSet<int>(edited.Keys.Select(k => k.Id));
            diff.RemovedKeys.AddRange(original.Keys.Where(k => keptKeys.Contains(k.Id) == false));
            return diff;
        }
    }

    /// <summary>이벤트를 읽고 쓰는 파일 형식 하나.</summary>
    public interface IEventCodec
    {
        EventSourceKind Kind { get; }

        /// <summary>파일 바이트에서 이벤트를 읽는다. 지원하지 않거나 손상이면 예외.</summary>
        EventSet Read(byte[] file);

        /// <summary>원본 바이트에 «원본 집합 → 고친 집합» 차이를 적용한 새 바이트. 파일에는 쓰지 않는다.</summary>
        byte[] Write(byte[] file, EventSet original, EventSet edited, List<string> log);
    }
}
