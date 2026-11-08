using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NKStudio.SpineEventInjector.SpineVersionConverter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NKStudio.SpineEventInjector.Project;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Skeleton
{
    /// <summary>
    /// 익스포트한 스켈레톤 데이터(.skel.bytes / .json, Spine 4.2·4.3)의 이벤트 읽기·쓰기.
    /// 바이너리는 SpineVersionConverter 의 공통 모델로 읽고 다시 쓴다. 고치기 전에 «읽고 다시 쓰기 = 원본»을 확인하고,
    /// 같지 않으면 손대지 않는다(변환기가 그 파일을 온전히 다루지 못한다는 뜻이므로).
    /// JSON 은 문서 트리에서 정의(events)와 애니메이션의 events 배열만 바꾼다. 나머지 값은 그대로지만
    /// 줄바꿈·들여쓰기는 Newtonsoft 형식으로 다시 쓰인다(한 줄짜리 원본은 한 줄로).
    /// </summary>
    public sealed class SkeletonDataCodec : IEventCodec
    {
        public SkeletonDataCodec(SpineFileFormat format) => Format = format;

        public SpineFileFormat Format { get; }

        public EventSourceKind Kind => Format == SpineFileFormat.Json ? EventSourceKind.SkeletonJson : EventSourceKind.SkeletonBinary;

        public static SkeletonDataCodec For(byte[] file) => new SkeletonDataCodec(SpineVersionInfo.DetectFormat(file));

        public static SpineVersion VersionOf(byte[] file)
        {
            string version = SpineVersionInfo.ReadVersionString(file)
                ?? throw new SpineFormatException(Loc.T("Spine 스켈레톤 데이터가 아니거나 버전을 읽을 수 없습니다"));
            SpineVersion v = SpineVersionInfo.Parse(version);
            if (v == SpineVersion.Version38)
            {
                throw new SpineEventUsageException(Loc.T("Spine {0} 데이터입니다. 4.2·4.3 익스포트만 지원합니다", version));
            }

            return v;
        }

        public EventSet Read(byte[] file) => Format == SpineFileFormat.Json ? ReadJson(file) : ReadBinary(file);

        public byte[] Write(byte[] file, EventSet original, EventSet edited, List<string> log)
        {
            if (EventSetDiff.Compute(original, edited).IsEmpty)
            {
                return file; // 바뀐 것이 없으면 파일을 그대로 둔다
            }

            byte[] result = Format == SpineFileFormat.Json ? WriteJson(file, edited, log) : WriteBinary(file, edited, log);
            EventSet check = Read(result);
            string problem = Compare(edited, check);
            if (problem != null)
            {
                throw new SpineFormatException(Loc.T("쓴 결과를 다시 읽었더니 이벤트가 다릅니다: {0}", problem));
            }

            return result;
        }

        // ------------------------------------------------------------ 바이너리

        static EventSet ReadBinary(byte[] file)
        {
            SpineSkeleton skeleton = SpineSkeletonConverter.Read(file, SpineFileFormat.Skel, VersionOf(file));
            var set = new EventSet { Fps = skeleton.Fps > 0 ? skeleton.Fps : 30f };
            for (int i = 0; i < skeleton.Events.Count; i++)
            {
                SpineEvent e = skeleton.Events[i];
                set.Definitions.Add(new EventDefinition
                {
                    Id = i,
                    Name = e.Name,
                    Int = e.IntValue,
                    Float = e.FloatValue,
                    String = e.StringValue ?? string.Empty,
                    AudioPath = e.AudioPath,
                    Volume = e.Volume,
                    Balance = e.Balance,
                });
            }

            int id = 0;
            foreach (SpineAnimation anim in skeleton.Animations)
            {
                set.Animations.Add(anim.Name);
                foreach (SpineTimelineFrame f in anim.Events)
                {
                    SpineEvent def = skeleton.Events.First(e => e.Name == f.Str1);
                    set.Keys.Add(new EventKey
                    {
                        Id = id++,
                        Animation = anim.Name,
                        Seconds = f.Time,
                        NativeTime = f.Time,
                        Name = f.Str1,
                        Int = f.Int1,
                        Float = f.Value1,
                        String = f.Str2 ?? string.Empty,
                        Volume = def.AudioPath != null ? f.Value2 : 1f,
                        Balance = def.AudioPath != null ? f.Value3 : 0f,
                    });
                }
            }

            return set;
        }

        static byte[] WriteBinary(byte[] file, EventSet edited, List<string> log)
        {
            SpineVersion version = VersionOf(file);
            SpineSkeleton skeleton = SpineSkeletonConverter.Read(file, SpineFileFormat.Skel, version);
            byte[] again = SpineSkeletonConverter.Write(skeleton, SpineFileFormat.Skel, version);
            if (again.AsSpan().SequenceEqual(file) == false)
            {
                throw new SpineEventUsageException(Loc.T("이 .skel 파일은 변환기로 읽고 다시 썼을 때 원본과 같지 않아 고치지 않습니다"));
            }

            EventSet original = ReadBinary(file);
            skeleton = SpineSkeletonConverter.Read(file, SpineFileFormat.Skel, version);

            // 정의: 바뀌지 않은 정의는 원래 객체를 그대로 써서 바이트가 같게 둔다
            List<SpineEvent> originalDefs = skeleton.Events;
            skeleton.Events = edited.Definitions.Select(d =>
            {
                if (d.Id >= 0 && d.Id < originalDefs.Count)
                {
                    SpineEvent e = originalDefs[d.Id];
                    if (original.Definitions[d.Id].SameValues(d))
                    {
                        return e;
                    }

                    e.Name = d.Name;
                    e.IntValue = d.Int;
                    e.FloatValue = d.Float;
                    if ((e.StringValue ?? "") != (d.String ?? ""))
                    {
                        e.StringValue = string.IsNullOrEmpty(d.String) ? null : d.String;
                    }

                    e.AudioPath = d.AudioPath;
                    e.Volume = d.Volume;
                    e.Balance = d.Balance;
                    return e;
                }

                return new SpineEvent
                {
                    Name = d.Name,
                    IntValue = d.Int,
                    FloatValue = d.Float,
                    StringValue = string.IsNullOrEmpty(d.String) ? null : d.String,
                    AudioPath = d.AudioPath,
                    Volume = d.Volume,
                    Balance = d.Balance,
                };
            }).ToList();

            // 키: 원래 프레임 객체를 번호로 모은다(ReadBinary 와 같은 순서)
            var frames = new Dictionary<int, SpineTimelineFrame>();
            int id = 0;
            foreach (SpineAnimation anim in skeleton.Animations)
            {
                foreach (SpineTimelineFrame f in anim.Events)
                {
                    frames[id++] = f;
                }
            }

            Dictionary<int, EventKey> before = original.Keys.ToDictionary(k => k.Id);
            foreach (SpineAnimation anim in skeleton.Animations)
            {
                anim.Events = edited.KeysOf(anim.Name).Select(k =>
                {
                    EventDefinition def = edited.Definition(k.Name) ?? throw new SpineEventUsageException(Loc.T("이벤트 정의 '{0}'가 없습니다", k.Name));
                    if (k.Id >= 0 && before.TryGetValue(k.Id, out EventKey old) && old.Animation == k.Animation &&
                        Math.Abs(old.Seconds - k.Seconds) < 1e-7 && old.SameValues(k))
                    {
                        SpineTimelineFrame kept = frames[k.Id];
                        kept.Str1 = k.Name; // 정의 이름이 바뀌었을 수 있다
                        return kept;
                    }

                    // 키 문자열이 정의 기본값과 같으면 기본값을 따르게 둔다(쓸 때 null)
                    bool isDefault = (k.String ?? "") == (def.String ?? "");
                    return new SpineTimelineFrame
                    {
                        Time = (float)k.Seconds,
                        Str1 = k.Name,
                        Int1 = k.Int,
                        Value1 = k.Float,
                        Str2 = isDefault ? (string.IsNullOrEmpty(def.String) ? null : def.String) : k.String,
                        Value2 = k.Volume,
                        Value3 = k.Balance,
                    };
                }).ToList();
            }

            log?.Add(Loc.T("이벤트 정의 {0}개, 키 {1}개로 다시 썼습니다", edited.Definitions.Count, edited.Keys.Count));
            return SpineSkeletonConverter.Write(skeleton, SpineFileFormat.Skel, version);
        }

        // ------------------------------------------------------------ JSON

        static JObject ParseJson(byte[] file) =>
            JObject.Parse(Encoding.UTF8.GetString(file).TrimStart('\uFEFF'));

        static EventSet ReadJson(byte[] file)
        {
            VersionOf(file);
            JObject root = ParseJson(file);
            var set = new EventSet { Fps = (float?)root["skeleton"]?["fps"] ?? 30f };
            int id = 0;
            var defs = new Dictionary<string, EventDefinition>();
            if (root["events"] is JObject events)
            {
                foreach (JProperty p in events.Properties())
                {
                    var d = (JObject)p.Value;
                    var def = new EventDefinition
                    {
                        Id = id++,
                        Name = p.Name,
                        Int = (int?)d["int"] ?? 0,
                        Float = (float?)d["float"] ?? 0f,
                        String = (string)d["string"] ?? string.Empty,
                        AudioPath = (string)d["audio"],
                        Volume = (float?)d["volume"] ?? 1f,
                        Balance = (float?)d["balance"] ?? 0f,
                    };
                    defs[p.Name] = def;
                    set.Definitions.Add(def);
                }
            }

            id = 0;
            if (root["animations"] is JObject animations)
            {
                foreach (JProperty p in animations.Properties())
                {
                    set.Animations.Add(p.Name);
                    if (p.Value["events"] is JArray keys)
                    {
                        foreach (JToken k in keys)
                        {
                            string name = (string)k["name"];
                            EventDefinition def = defs.TryGetValue(name, out EventDefinition d) ? d
                                : throw new SpineFormatException(Loc.T("애니메이션 '{0}'이 정의되지 않은 이벤트 '{1}'를 씁니다", p.Name, name));
                            float time = (float?)k["time"] ?? 0f;
                            set.Keys.Add(new EventKey
                            {
                                Id = id++,
                                Animation = p.Name,
                                Seconds = time,
                                NativeTime = time,
                                Name = name,
                                Int = (int?)k["int"] ?? def.Int,
                                Float = (float?)k["float"] ?? def.Float,
                                String = (string)k["string"] ?? def.String,
                                Volume = (float?)k["volume"] ?? 1f,
                                Balance = (float?)k["balance"] ?? 0f,
                            });
                        }
                    }
                }
            }

            return set;
        }

        static byte[] WriteJson(byte[] file, EventSet edited, List<string> log)
        {
            string text = Encoding.UTF8.GetString(file);
            bool bom = text.Length > 0 && text[0] == '\uFEFF';
            bool indented = text.TrimEnd().Contains('\n');
            JObject root = ParseJson(file);
            EventSet original = ReadJson(file);
            var defTokens = new Dictionary<int, JToken>();
            if (root["events"] is JObject oldEvents)
            {
                int n = 0;
                foreach (JProperty p in oldEvents.Properties())
                {
                    defTokens[n++] = p.Value.DeepClone();
                }
            }

            var keyTokens = new Dictionary<int, JToken>();
            if (root["animations"] is JObject oldAnims)
            {
                int n = 0;
                foreach (JProperty p in oldAnims.Properties())
                {
                    if (p.Value["events"] is JArray arr)
                    {
                        foreach (JToken t in arr)
                        {
                            keyTokens[n++] = t.DeepClone();
                        }
                    }
                }
            }

            Dictionary<int, EventKey> beforeKeys = original.Keys.ToDictionary(k => k.Id);
            // 기본값이 바뀐 정의를 쓰는 키는 원래 토큰을 쓰지 않는다(값을 생략한 키는 기본값을 따라가 버리므로)
            var changedDefs = new HashSet<string>(edited.Definitions
                .Where(d => d.Id < 0 || original.Definitions[d.Id].SameValues(d) == false)
                .Select(d => d.Name));
            var events = new JObject();
            foreach (EventDefinition d in edited.Definitions)
            {
                if (d.Id >= 0 && defTokens.TryGetValue(d.Id, out JToken keptDef) && original.Definitions[d.Id].SameValues(d))
                {
                    events[d.Name] = keptDef;
                    continue;
                }

                var json = new JObject();
                if (d.Int != 0) json["int"] = d.Int;
                if (d.Float != 0f) json["float"] = Number(d.Float, 6);
                if (string.IsNullOrEmpty(d.String) == false) json["string"] = d.String;
                if (d.AudioPath != null)
                {
                    json["audio"] = d.AudioPath;
                    if (d.Volume != 1f) json["volume"] = Number(d.Volume, 6);
                    if (d.Balance != 0f) json["balance"] = Number(d.Balance, 6);
                }

                events[d.Name] = json;
            }

            root.Remove("events");
            if (events.Count > 0)
            {
                // Spine 순서: … skins, events, animations
                if (root.Property("animations") is JProperty anims)
                {
                    anims.AddBeforeSelf(new JProperty("events", events));
                }
                else
                {
                    root["events"] = events;
                }
            }

            if (root["animations"] is JObject animations)
            {
                foreach (JProperty p in animations.Properties())
                {
                    var anim = (JObject)p.Value;
                    var keys = new JArray();
                    foreach (EventKey k in edited.KeysOf(p.Name))
                    {
                        EventDefinition def = edited.Definition(k.Name) ?? throw new SpineEventUsageException(Loc.T("이벤트 정의 '{0}'가 없습니다", k.Name));
                        if (k.Id >= 0 && beforeKeys.TryGetValue(k.Id, out EventKey old) && old.Animation == k.Animation &&
                            Math.Abs(old.Seconds - k.Seconds) < 1e-7 && old.SameValues(k) && changedDefs.Contains(k.Name) == false &&
                            keyTokens.TryGetValue(k.Id, out JToken kept))
                        {
                            kept["name"] = k.Name; // 정의 이름이 바뀌었을 수 있다
                            keys.Add(kept);
                            continue;
                        }

                        var json = new JObject();
                        if (k.Seconds != 0) json["time"] = Number(k.Seconds, 4);
                        json["name"] = k.Name;
                        if (k.Int != def.Int) json["int"] = k.Int;
                        if (k.Float != def.Float) json["float"] = Number(k.Float, 6);
                        if ((k.String ?? "") != (def.String ?? "")) json["string"] = k.String ?? string.Empty;
                        if (def.AudioPath != null)
                        {
                            if (k.Volume != 1f) json["volume"] = Number(k.Volume, 6);
                            if (k.Balance != 0f) json["balance"] = Number(k.Balance, 6);
                        }

                        keys.Add(json);
                    }

                    anim.Remove("events");
                    if (keys.Count > 0)
                    {
                        anim["events"] = keys;
                    }
                }
            }

            log?.Add(Loc.T("이벤트 정의 {0}개, 키 {1}개로 다시 썼습니다", edited.Definitions.Count, edited.Keys.Count));
            string output = root.ToString(indented ? Formatting.Indented : Formatting.None);
            return Encoding.UTF8.GetBytes(bom ? "\uFEFF" + output : output);
        }

        static JToken Number(double value, int digits)
        {
            double rounded = Math.Round(value, digits);
            return rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e9 ? new JValue((long)rounded) : new JValue(rounded);
        }

        // ------------------------------------------------------------ 확인

        /// <summary>두 집합의 이벤트가 같은지(정의 순서·값, 애니메이션별 키). 다르면 첫 차이를 설명한다.</summary>
        public static string Compare(EventSet want, EventSet got)
        {
            if (want.Definitions.Count != got.Definitions.Count)
            {
                return Loc.T("정의 수 {0} ≠ {1}", want.Definitions.Count, got.Definitions.Count);
            }

            for (int i = 0; i < want.Definitions.Count; i++)
            {
                EventDefinition a = want.Definitions[i];
                EventDefinition b = got.Definitions[i];
                if (a.Name != b.Name || a.Int != b.Int || Math.Abs(a.Float - b.Float) > 1e-5 || (a.String ?? "") != (b.String ?? ""))
                {
                    return Loc.T("정의 '{0}'", a.Name);
                }
            }

            foreach (string anim in want.Animations)
            {
                List<EventKey> a = want.KeysOf(anim).ToList();
                List<EventKey> b = got.KeysOf(anim).ToList();
                if (a.Count != b.Count)
                {
                    return Loc.T("{0} 키 수 {1} ≠ {2}", anim, a.Count, b.Count);
                }

                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i].Name != b[i].Name || Math.Abs(a[i].Seconds - b[i].Seconds) > 1e-4 || a[i].Int != b[i].Int ||
                        Math.Abs(a[i].Float - b[i].Float) > 1e-5 || (a[i].String ?? "") != (b[i].String ?? ""))
                    {
                        return Loc.T("{0} {1}번째 키", anim, i);
                    }
                }
            }

            return null;
        }
    }
}
