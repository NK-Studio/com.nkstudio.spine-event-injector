using System;
using System.Collections.Generic;
using NKStudio.SpineEventInjector.Project;
using Spine.Unity;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 창에 불러온 대상 하나: 파일, 형식, 읽은 이벤트(원본)와 고치는 중인 이벤트(작업본), 되돌리기 기록.
    /// 작업본은 «적용하기» 전까지 파일에 쓰지 않는다.
    /// </summary>
    sealed class EventSource
    {
        public EventSourceKind Kind;
        /// <summary>파일 절대 경로.</summary>
        public string FilePath;
        /// <summary>프로젝트 안 파일이면 에셋 경로(Assets/…), 밖이면 null.</summary>
        public string AssetPath;
        public string DisplayName;
        /// <summary>파일에 적힌 Spine 버전(.spine 은 저장한 에디터 버전, 익스포트는 skeleton.spine).</summary>
        public string Version;
        public IEventCodec Codec;
        public byte[] Bytes;
        public EventSet Original;
        public EventSet Working;
        /// <summary>미리보기용 스켈레톤 데이터(프로젝트 에셋 또는 임시로 만든 것). 없으면 null.</summary>
        public SkeletonDataAsset Preview;
        /// <summary>애니메이션 길이(초). 미리보기 데이터에서 채운다.</summary>
        public readonly Dictionary<string, float> Durations = new Dictionary<string, float>();

        readonly List<EventSet> _undo = new List<EventSet>();
        readonly List<EventSet> _redo = new List<EventSet>();

        public event Action Changed;

        /// <summary>.spine 의 스켈레톤 이름(이름순). 하나뿐이면 비어 있다.</summary>
        public IReadOnlyList<string> Skeletons = Array.Empty<string>();

        /// <summary>지금 고치는 스켈레톤(여럿일 때만).</summary>
        public string Skeleton => Skeletons.Count > 1 ? (Codec as SpineProjectCodec)?.Skeleton : null;

        public bool IsSpineProject => Kind == EventSourceKind.SpineProject;
        public bool IsDirty => EventSetDiff.Compute(Original, Working).IsEmpty == false;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>작업본을 고친다(되돌리기 한 단계).</summary>
        public void Edit(Action<EventSet> change)
        {
            _undo.Add(Working.Clone());
            if (_undo.Count > 200)
            {
                _undo.RemoveAt(0);
            }

            _redo.Clear();
            change(Working);
            Changed?.Invoke();
        }

        public void Undo()
        {
            if (_undo.Count == 0)
            {
                return;
            }

            _redo.Add(Working);
            Working = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            _undo.Add(Working);
            Working = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Changed?.Invoke();
        }

        /// <summary>작업본을 버리고 원본으로.</summary>
        public void Revert() => Edit(set =>
        {
            EventSet fresh = Original.Clone();
            set.Definitions = fresh.Definitions;
            set.Keys = fresh.Keys;
        });

        /// <summary>고칠 스켈레톤을 바꾼다. 원본·작업본·미리보기가 그 스켈레톤 것으로 바뀐다.</summary>
        public void SelectSkeleton(string name)
        {
            ((SpineProjectCodec)Codec).Skeleton = name;
            Durations.Clear();
            Preview = null;
            Reload(Bytes);
        }

        /// <summary>파일에 쓴 뒤 새 바이트로 원본·작업본을 다시 만든다.</summary>
        public void Reload(byte[] bytes)
        {
            Bytes = bytes;
            float fps = Working?.Fps ?? 30f;
            Original = Codec.Read(bytes);
            if (IsSpineProject)
            {
                Rescale(Original, fps);
            }

            Working = Original.Clone();
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke();
        }

        /// <summary>.spine 은 키가 프레임으로 적혀 있어 FPS 를 알게 되면 초를 다시 계산한다.</summary>
        public void SetProjectFps(float fps)
        {
            if (IsSpineProject == false || fps <= 0f || Math.Abs(Original.Fps - fps) < 0.001f)
            {
                return;
            }

            ((SpineProjectCodec)Codec).Fps = fps;
            Rescale(Original, fps);
            Rescale(Working, fps);
            Changed?.Invoke();
        }

        static void Rescale(EventSet set, float fps)
        {
            foreach (EventKey k in set.Keys)
            {
                k.Seconds = k.Id >= 0 ? k.NativeTime / fps : k.Seconds * set.Fps / fps;
            }

            set.Fps = fps;
        }

        public float Duration(string animation)
        {
            if (Durations.TryGetValue(animation, out float d) && d > 0f)
            {
                return d;
            }

            double last = 0;
            foreach (EventKey k in Working.KeysOf(animation))
            {
                last = Math.Max(last, k.Seconds);
            }

            return (float)Math.Max(1.0, last + 0.5);
        }
    }
}
