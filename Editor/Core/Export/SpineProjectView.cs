using System.Collections.Generic;
using System.Linq;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>
    /// 객체 그래프를 읽기 쉽게 감싼다(파이썬 PoC project_reader.py).
    /// 참조를 따라가 실제 객체를 돌려주고, 버전마다 다르게 적히는 필드 키(12, t12, d12, 1:12…)를
    /// 태그 번호 하나로 찾고, 해석기가 건너뛰는 값(배열·색·열거형)을 바이트에서 꺼낸다.
    /// 값이 들어 있는 자리의 선언 타입(O·T 등)은 스키마마다 달라도 참조를 따라가면 같은 결과가 된다.
    /// </summary>
    internal sealed class ProjectView
    {
        public const string KindString = "str";
        public const string KindNumber = "num";
        public const string KindNode = "node";

        readonly Dictionary<Node, Dictionary<int, List<string>>> _tags = new Dictionary<Node, Dictionary<int, List<string>>>();

        public readonly SpineProjectFile File;
        public readonly SpineProjectDocument Doc;
        public readonly byte[] Body;
        public readonly string Format;

        public ProjectView(SpineProjectFile file)
        {
            File = file;
            Doc = file.Doc;
            Body = file.Body;
            Format = Doc.Version;
        }

        public Node Root => Doc.Of("Project").FirstOrDefault() ?? Doc.PartialRoot;

        public bool CurveConditional => Doc.Schema.CurveCondition != null;

        public bool NewStyleTimelines => Doc.Schema.Kinds.Values.Contains("timeline");

        public string KindOf(Node node) => Doc.Schema.Kinds.TryGetValue(node.Cls, out string kind) ? kind : null;

        public object Deref(object value) => value is NodeRef r ? Doc.ById[(r.Table, r.Id)] : value;

        static int? KeyTag(string key)
        {
            string k = key.TrimStart('t', 'd');
            int colon = k.IndexOf(':');
            if (colon >= 0)
            {
                k = k.Substring(colon + 1);
            }

            if (k.Length == 0 || k.All(c => c >= '0' && c <= '9') == false)
            {
                return null;
            }

            return int.Parse(k);
        }

        Dictionary<int, List<string>> Tags(Node node)
        {
            if (_tags.TryGetValue(node, out Dictionary<int, List<string>> tags))
            {
                return tags;
            }

            tags = new Dictionary<int, List<string>>();
            foreach (string key in node.F.Keys)
            {
                int? tag = KeyTag(key);
                if (tag != null)
                {
                    if (tags.TryGetValue(tag.Value, out List<string> list) == false)
                    {
                        tags[tag.Value] = list = new List<string>();
                    }

                    list.Add(key);
                }
            }

            _tags[node] = tags;
            return tags;
        }

        /// <summary>태그 번호로 값(같은 태그가 여럿이면 kind 로 고른다). kind: str·num·node 또는 클래스 이름.</summary>
        public object Get(Node node, int tag, string kind)
        {
            if (node == null || Tags(node).TryGetValue(tag, out List<string> keys) == false)
            {
                return null;
            }

            foreach (string key in keys)
            {
                object value = Deref(node.F[key].Value);
                if (Is(value, kind))
                {
                    return value;
                }
            }

            return null;
        }

        public Node GetNode(Node node, int tag, string cls = KindNode) => Get(node, tag, cls) as Node;

        public string GetString(Node node, int tag) => Get(node, tag, KindString) as string;

        static bool Is(object value, string kind)
        {
            switch (kind)
            {
                case KindString:
                    return value is string;
                case KindNumber:
                    return value is long || value is float;
                case KindNode:
                    return value is Node;
                default:
                    return value is Node node && node.Cls == kind;
            }
        }

        /// <summary>리스트·맵 노드의 원소(참조를 따라간 것). 빈 자리는 null 로 남는다.</summary>
        public List<Node> Items(Node node)
        {
            var items = new List<Node>();
            if (node != null)
            {
                foreach (ListItem item in node.Items)
                {
                    items.Add(Deref(item.Value) as Node);
                }
            }

            return items;
        }

        /// <summary>새 객체 본문의 시작 위치(표지 뒤). 슬롯이 없는 잎 객체에만 쓴다.</summary>
        public int BodyStart(Node node) => node.Parts.Count == 0 ? node.End : node.Parts[0].Start;

        /// <summary>배열·색·Vec3·열거형·문자열 객체의 값. 결과: long, string, int[](색), List&lt;double&gt; 또는 List&lt;long&gt;.</summary>
        public object Leaf(Node node)
        {
            if (node == null)
            {
                return null;
            }

            string kind = KindOf(node);
            if (kind == "varint")
            {
                return node.F["v"].Value;
            }

            if (kind == "string")
            {
                return node.F["s"].Value;
            }

            int p = BodyStart(node);
            switch (kind)
            {
                case "fixed4":
                    return new int[] { Body[p], Body[p + 1], Body[p + 2], Body[p + 3] };
                case "fixed12":
                    return Floats(p, 3);
                case "rfarray":
                {
                    long n = Varint(ref p);
                    return Floats(p, (int)n);
                }
                case "farray":
                {
                    long n = Varint(ref p);
                    return n == 0 ? null : Floats(p, (int)n - 1);
                }
                case "rsarray":
                {
                    long n = Varint(ref p);
                    var shorts = new List<long>((int)n);
                    for (int i = 0; i < n; i++)
                    {
                        shorts.Add((Body[p] << 8) | Body[p + 1]);
                        p += 2;
                    }

                    return shorts;
                }
                case "rvarray":
                {
                    long n = Varint(ref p);
                    var values = new List<long>((int)n);
                    for (int i = 0; i < n; i++)
                    {
                        values.Add(Varint(ref p));
                    }

                    return values;
                }
                case "boxfarray":
                {
                    long n = Varint(ref p);
                    var boxed = new List<double?>();
                    for (long i = 0; i < n - 1; i++)
                    {
                        long present = Varint(ref p);
                        if (present != 0)
                        {
                            boxed.Add(FloatBits.Read(Body, p));
                            p += 4;
                        }
                        else
                        {
                            boxed.Add(null);
                        }
                    }

                    return boxed;
                }
                case "defvtx":
                case "defvtx0":
                {
                    int total = (int)Varint(ref p);
                    int chunks = (int)Varint(ref p);
                    var output = new List<double>(new double[total]);
                    for (int i = 0; i < chunks; i++)
                    {
                        byte head = Body[p++];
                        if (head != 0 || kind == "defvtx")
                        {
                            int n = System.Math.Min(128, total - 128 * i);
                            for (int j = 0; j < n; j++)
                            {
                                output[128 * i + j] = FloatBits.Read(Body, p);
                                p += 4;
                            }
                        }
                    }

                    return output;
                }
                default:
                    throw new SpineFormatException(Loc.T("{0}({1})는 잎 값이 아닙니다", node.Cls, kind));
            }
        }

        public List<double> Floats(int at, int count)
        {
            var values = new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                values.Add(FloatBits.Read(Body, at + 4 * i));
            }

            return values;
        }

        long Varint(ref int p)
        {
            long value = 0;
            int shift = 0;
            while (true)
            {
                byte x = Body[p++];
                if (shift < 63)
                {
                    value |= (long)(x & 0x7F) << shift;
                }

                shift += 7;
                if ((x & 0x80) == 0)
                {
                    return value;
                }
            }
        }
    }
}
