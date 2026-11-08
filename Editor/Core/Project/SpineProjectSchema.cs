using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>4.3 타임라인 종류별 고정 배치.</summary>
    public sealed class TimelineLayout
    {
        public int Vals;
        public int Curves;
        public string Extra;
        public string Extra2;
        public string Payload;
    }

    /// <summary>
    /// 포맷 규칙 한 벌(poc/schema/*.json 과 같은 내용). 클래스 번호 → 이름·종류, (클래스, 키) → 값 타입,
    /// 참조 표, 4.3 타임라인 배치와 빌드별 옵션.
    /// </summary>
    public sealed class SpineProjectSchema
    {
        public string Name;
        public List<string> Formats = new List<string>();
        public string SpineVersion;
        public Dictionary<string, string> Names = new Dictionary<string, string>();
        public Dictionary<string, string> Kinds = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> Fields = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, string>> Slots = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, int> Tail = new Dictionary<string, int>();
        /// <summary>포맷 접두사 → 견본 이름. 한 스키마가 여러 시대를 맡을 때 시대에 맞는 견본을 고른다(없으면 스키마 이름).</summary>
        public Dictionary<string, string> Templates = new Dictionary<string, string>();
        public Dictionary<string, string> Tables = new Dictionary<string, string>();
        public Dictionary<string, List<string>> Plain = new Dictionary<string, List<string>>();
        public Dictionary<string, TimelineLayout> Timelines = new Dictionary<string, TimelineLayout>();
        public Dictionary<string, string> AnyA = new Dictionary<string, string>();
        public Dictionary<string, List<List<int>>> Groups = new Dictionary<string, List<List<int>>>();
        public bool Ascending;
        public bool TimelineRefs = true;
        /// <summary>null: 곡선 항상(4.3.00~03) · "own": 이 키가 베지어일 때만(4.3.04) · "prev": 앞 키가 베지어여도(4.3.05~).</summary>
        public string CurveCondition;
        public string KeyEventField = "7";
        public bool RootVarint = true;
        public int RootPad;

        public static SpineProjectSchema Parse(string name, string json)
        {
            JObject o = JObject.Parse(json);
            var s = new SpineProjectSchema { Name = name };
            s.Formats = o["formats"] is JArray formats ? formats.Select(t => (string)t).ToList() : new List<string> { name };
            s.SpineVersion = (string)o["spine_version"];
            s.Names = StringMap(o["names"]);
            s.Kinds = StringMap(o["kinds"]);
            s.Fields = NestedMap(o["fields"]);
            s.Slots = NestedMap(o["slots"]);
            if (o["tail"] is JObject tail)
            {
                foreach (JProperty p in tail.Properties())
                {
                    s.Tail[p.Name] = (int)p.Value;
                }
            }

            s.Templates = StringMap(o["templates"]);
            s.Tables = StringMap(o["tables"]);
            if (o["plain"] is JObject plain)
            {
                foreach (JProperty p in plain.Properties())
                {
                    s.Plain[p.Name] = ((JArray)p.Value).Select(t => (string)t).ToList();
                }
            }

            if (o["timelines"] is JObject timelines)
            {
                foreach (JProperty p in timelines.Properties())
                {
                    var lay = (JObject)p.Value;
                    s.Timelines[p.Name] = new TimelineLayout
                    {
                        Vals = (int?)lay["vals"] ?? 0,
                        Curves = (int?)lay["curves"] ?? 0,
                        Extra = (string)lay["extra"],
                        Extra2 = (string)lay["extra2"],
                        Payload = (string)lay["payload"],
                    };
                }
            }

            s.AnyA = StringMap(o["anyA"]);
            if (o["groups"] is JObject groups)
            {
                foreach (JProperty p in groups.Properties())
                {
                    s.Groups[p.Name] = ((JArray)p.Value).Select(g => ((JArray)g).Select(t => (int)t).ToList()).ToList();
                }
            }

            s.Ascending = (bool?)o["ascending"] ?? false;
            s.TimelineRefs = (bool?)o["tl_refs"] ?? true;
            JToken curve = o["curve_cond"];
            if (curve != null && curve.Type == JTokenType.String)
            {
                s.CurveCondition = (string)curve;
            }
            else if (curve != null && curve.Type == JTokenType.Boolean && (bool)curve)
            {
                s.CurveCondition = "prev";
            }

            s.KeyEventField = (string)o["key_event_field"] ?? "7";
            s.RootVarint = (bool?)o["root_varint"] ?? true;
            s.RootPad = (int?)o["root_pad"] ?? 0;
            return s;
        }

        static Dictionary<string, string> StringMap(JToken token)
        {
            var map = new Dictionary<string, string>();
            if (token is JObject o)
            {
                foreach (JProperty p in o.Properties())
                {
                    map[p.Name] = (string)p.Value;
                }
            }

            return map;
        }

        static Dictionary<string, Dictionary<string, string>> NestedMap(JToken token)
        {
            var map = new Dictionary<string, Dictionary<string, string>>();
            if (token is JObject o)
            {
                foreach (JProperty p in o.Properties())
                {
                    map[p.Name] = StringMap(p.Value);
                }
            }

            return map;
        }
    }
}
