using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>3.8 바이너리(.skel)에서 이벤트와 관련된 구간의 바이트 위치.</summary>
    public sealed class Spine38EventLayout
    {
        /// <summary>문자열 표: 개수 varint 의 위치, 첫 문자열의 위치, 표의 끝.</summary>
        public int StringsStart;
        public int StringsBodyStart;
        public int StringsEnd;

        /// <summary>이벤트 정의 목록(개수 varint 부터 끝까지).</summary>
        public int EventsStart;
        public int EventsEnd;

        /// <summary>애니메이션마다 이벤트 타임라인(개수 varint 부터 끝까지). 애니메이션 순서와 같다.</summary>
        public List<(int Start, int End)> AnimationEvents = new List<(int Start, int End)>();

        /// <summary>읽기가 끝난 위치(파일 끝이어야 한다).</summary>
        public int End;
    }

    /// <summary>
    /// 3.8 바이너리의 이벤트 정의와 이벤트 키만 바꿔 쓴다. 그 밖의 바이트는 원본을 그대로 복사한다.
    /// 변환기의 3.8 쓰기(<see cref="Spine38BinaryWriter"/>)는 문자열 표와 타임라인을 새로 만들어 원본과 바이트가 달라지므로,
    /// 익스포트 데이터에 이벤트를 심을 때는 이 방식을 쓴다.
    /// 구조(원본 SkeletonData38BinaryWriter): 문자열 표 … 이벤트 정의 목록 · 애니메이션 목록(애니메이션마다 끝에 이벤트 타임라인).
    /// </summary>
    public static class Spine38BinaryEventPatcher
    {
        /// <summary>
        /// file 을 읽은 skeleton(이벤트 정의·키만 고친 것)으로 다시 쓴다.
        /// 고치지 않은 skeleton 을 주면 file 과 같은 바이트가 나와야 한다(호출하는 쪽이 먼저 확인한다).
        /// </summary>
        public static byte[] Write(byte[] file, SpineSkeleton skeleton)
        {
            Spine38EventLayout layout = skeleton.Layout38
                ?? throw new SpineConversionException("3.8 바이너리에서 읽은 스켈레톤이 아니다.");
            if (layout.End != file.Length || layout.AnimationEvents.Count != skeleton.Animations.Count)
            {
                throw new SpineConversionException("3.8 바이너리의 구조가 읽은 결과와 맞지 않는다.");
            }

            // 이벤트 이름은 문자열 표를 가리킨다. 표에 없는 이름은 표 끝에 붙인다(기존 번호는 그대로다).
            var strings = new List<string>(skeleton.Strings);
            var known = new HashSet<string>(strings);
            var added = new List<string>();
            foreach (SpineEvent e in skeleton.Events)
            {
                if (e.Name == null)
                {
                    throw new SpineConversionException("이름이 없는 이벤트 정의가 있다.");
                }

                if (known.Add(e.Name) == true)
                {
                    strings.Add(e.Name);
                    added.Add(e.Name);
                }
            }

            using (var result = new MemoryStream(file.Length + 256))
            {
                result.Write(file, 0, layout.StringsStart);
                var table = new SpineBinaryOutput();
                table.WriteVarint(strings.Count, true);
                Put(result, table);
                result.Write(file, layout.StringsBodyStart, layout.StringsEnd - layout.StringsBodyStart);
                var tail = new SpineBinaryOutput();
                foreach (string name in added)
                {
                    tail.WriteString(name);
                }

                Put(result, tail);
                result.Write(file, layout.StringsEnd, layout.EventsStart - layout.StringsEnd);

                var defs = new SpineBinaryOutput();
                defs.SetStringTable(strings);
                defs.WriteVarint(skeleton.Events.Count, true);
                foreach (SpineEvent e in skeleton.Events)
                {
                    defs.WriteStringRef(e.Name);
                    defs.WriteVarint(e.IntValue, false);
                    defs.WriteFloat(e.FloatValue);
                    defs.WriteString(e.StringValue);
                    defs.WriteString(e.AudioPath);
                    if (string.IsNullOrEmpty(e.AudioPath) == false)
                    {
                        defs.WriteFloat(e.Volume);
                        defs.WriteFloat(e.Balance);
                    }
                }

                Put(result, defs);

                int cursor = layout.EventsEnd;
                for (int i = 0; i < skeleton.Animations.Count; i++)
                {
                    (int start, int end) = layout.AnimationEvents[i];
                    result.Write(file, cursor, start - cursor);
                    Put(result, Frames(skeleton, skeleton.Animations[i]));
                    cursor = end;
                }

                result.Write(file, cursor, file.Length - cursor);
                return result.ToArray();
            }
        }

        static SpineBinaryOutput Frames(SpineSkeleton skeleton, SpineAnimation animation)
        {
            var o = new SpineBinaryOutput();
            o.WriteVarint(animation.Events.Count, true);
            foreach (SpineTimelineFrame frame in animation.Events)
            {
                int index = skeleton.Events.FindIndex(e => e.Name == frame.Str1);
                if (index < 0)
                {
                    throw new SpineConversionException($"애니메이션 '{animation.Name}' 의 키가 없는 이벤트 '{frame.Str1}' 를 쓴다.");
                }

                SpineEvent data = skeleton.Events[index];
                o.WriteFloat(frame.Time);
                o.WriteVarint(index, true);
                o.WriteVarint(frame.Int1, false);
                o.WriteFloat(frame.Value1);

                // 읽은 키는 원래 표시를 지킨다. 문자열이 정의 기본값과 달라졌으면 자기 문자열을 쓴다.
                bool own = frame.HasOwnString == true || (frame.Str2 ?? string.Empty) != (data.StringValue ?? string.Empty);
                o.WriteBoolean(own);
                if (own == true)
                {
                    o.WriteString(frame.Str2);
                }

                if (string.IsNullOrEmpty(data.AudioPath) == false)
                {
                    o.WriteFloat(frame.Value2);
                    o.WriteFloat(frame.Value3);
                }
            }

            return o;
        }

        static void Put(MemoryStream stream, SpineBinaryOutput output)
        {
            byte[] bytes = output.ToArray();
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
