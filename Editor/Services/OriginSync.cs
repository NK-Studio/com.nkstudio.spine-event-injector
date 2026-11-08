using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 익스포트 데이터(bytes·json)와 연결된 Spine 원본(.spine)의 이벤트 비교, 그리고 «원본에 반영».
    /// 비교 자체가 원장 역할을 한다: 데이터에는 있는데 원본에 없는 이벤트가 «원본에 아직 없는 이벤트»다.
    /// </summary>
    sealed class OriginSync
    {
        public string SpinePath;
        public EventSet Origin;
        public List<EventDefinition> MissingDefinitions = new List<EventDefinition>();
        public List<EventKey> MissingKeys = new List<EventKey>();
        public List<string> MissingAnimations = new List<string>();
        public string Error;
        string _skeleton;

        public int MissingCount => MissingKeys.Count + MissingDefinitions.Count;

        /// <summary>data(익스포트 데이터의 «파일에 쓴» 이벤트)와 원본을 비교한다. 원본 키 시간은 data 의 FPS 로 초로 바꾼다.</summary>
        /// <param name="dataFileName">익스포트 파일 이름. 스켈레톤이 여럿인 .spine 에서 같은 이름의 스켈레톤과 비교한다.</param>
        public static OriginSync Compare(EventSet data, string spinePath, string dataFileName)
        {
            var sync = new OriginSync { SpinePath = spinePath, _skeleton = dataFileName?.Split('.')[0] };
            try
            {
                if (File.Exists(spinePath) == false)
                {
                    throw new SpineEventUsageException(Loc.T("연결된 .spine 파일이 없습니다"));
                }

                var codec = new SpineProjectCodec(SchemaLibrary.All) { Fps = data.Fps, Skeleton = sync._skeleton };
                sync.Origin = codec.Read(File.ReadAllBytes(spinePath));
            }
            catch (Exception e)
            {
                sync.Error = e.Message;
                return sync;
            }

            sync.MissingDefinitions = data.Definitions.Where(d => sync.Origin.Definition(d.Name) == null).ToList();
            sync.MissingKeys = data.Keys.Where(k => sync.Origin.Keys.Any(o =>
                o.Animation == k.Animation && o.Name == k.Name && Math.Abs(o.Seconds - k.Seconds) * data.Fps < 0.01 && o.SameValues(k)) == false).ToList();
            sync.MissingAnimations = sync.MissingKeys.Select(k => k.Animation).Distinct()
                .Where(a => sync.Origin.Animations.Contains(a) == false).ToList();
            return sync;
        }

        /// <summary>반영할 수 없는 상태면 이유를 담은 예외를 던진다.</summary>
        public void Validate()
        {
            if (Error != null)
            {
                throw new SpineEventUsageException(Error);
            }

            if (MissingAnimations.Count > 0)
            {
                throw new SpineEventUsageException(Loc.T("원본에 애니메이션이 없습니다: {0}", string.Join(", ", MissingAnimations)));
            }
        }

        /// <summary>
        /// 빠진 정의·키를 원본에 넣는다. 같은 애니메이션·같은 정의에서 프레임만 다른 키도 «빠진 것»으로 보고 더한다
        /// (원본의 기존 키는 지우지 않는다 — 원본에서 지운 결정은 사람이 판단할 일이다).
        /// </summary>
        /// <param name="confirmed">창 안 확인 창에서 이미 확인받았으면 true.</param>
        public List<string> Push(bool confirmed = false)
        {
            Validate();
            if (confirmed == false && EventFileWriter.Confirm(SpinePath) == false)
            {
                return null;
            }

            var codec = new SpineProjectCodec(SchemaLibrary.All) { Fps = Origin.Fps, Skeleton = _skeleton };
            byte[] bytes = File.ReadAllBytes(SpinePath);
            EventSet original = codec.Read(bytes);
            EventSet edited = original.Clone();
            foreach (EventDefinition d in MissingDefinitions)
            {
                EventDefinition copy = d.Clone();
                copy.Id = -1;
                copy.AudioPath = null;
                edited.Definitions.Add(copy);
            }

            foreach (EventKey k in MissingKeys)
            {
                EventKey copy = k.Clone();
                copy.Id = -1;
                // 같은 자리(애니메이션·정의·프레임)에 값만 다른 원본 키가 있으면 값만 고친다
                EventKey same = edited.Keys.FirstOrDefault(o => o.Animation == k.Animation && o.Name == k.Name &&
                                                                Math.Abs(o.Seconds - k.Seconds) * Origin.Fps < 0.01);
                if (same != null)
                {
                    same.Int = k.Int;
                    same.Float = k.Float;
                    same.String = k.String;
                    continue;
                }

                edited.Keys.Add(copy);
            }

            var log = new List<string>();
            byte[] result = codec.Write(bytes, original, edited, log);
            EventFileWriter.WriteVerified(SpinePath, null, result, written =>
            {
                EventSet check = codec.Read(written);
                OriginSync again = new OriginSync { Origin = check };
                again.MissingKeys = MissingKeys.Where(k => check.Keys.Any(o => o.Animation == k.Animation && o.Name == k.Name &&
                    Math.Abs(o.Seconds - k.Seconds) * Origin.Fps < 0.01 && o.SameValues(k)) == false).ToList();
                if (again.MissingKeys.Count > 0)
                {
                    throw new SpineFormatException(Loc.T("원본에 쓴 뒤 다시 읽었더니 키 {0}개가 없습니다. 백업으로 되돌렸습니다", again.MissingKeys.Count));
                }
            });
            return log;
        }
    }
}
