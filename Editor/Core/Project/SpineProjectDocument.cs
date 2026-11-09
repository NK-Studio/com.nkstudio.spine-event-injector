using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// .spine 본문(압축을 푼 바이트)을 스키마대로 끝까지 읽어 객체 그래프로 만든다(Kryo 참조 규칙).
    /// 객체 본문은 «원래 바이트 조각»과 «슬롯»의 나열로 기록한다. 슬롯은 처음 나온 곳에서는 객체 본문을 품고(새 객체),
    /// 그 뒤로는 번호로 가리킨다(역참조). 다시 쓰기는 <see cref="SpineProjectWriter"/>.
    /// 파이썬 PoC(poc/spine_event_poc.py)의 Document 를 그대로 옮겼다.
    /// </summary>
    public sealed class SpineProjectDocument
    {
        readonly byte[] _b;
        int _p;
        readonly SpineProjectSchema _schema;
        readonly Dictionary<string, int> _cnum = new Dictionary<string, int>();
        readonly Stack<Node> _stack = new Stack<Node>();
        readonly StringBuilder _text = new StringBuilder(64);
        int? _tlType;
        int _klPrev;

        public readonly Dictionary<string, List<string>> Objs = new Dictionary<string, List<string>>
        {
            ["A"] = new List<string>(),
            ["B"] = new List<string>(),
        };

        public readonly Dictionary<(string, int), Node> ById;
        public readonly List<Node> Nodes;
        public int RefCount { get; private set; }
        public readonly Node Top;
        public readonly string Version;
        public SpineProjectSchema Schema => _schema;
        public IReadOnlyDictionary<string, int> ClassNumbers => _cnum;

        /// <summary>내보내기용 부분 읽기에서 멈춘 이유. 끝까지 읽었으면 null.</summary>
        public string PartialError { get; private set; }

        /// <summary>부분 읽기에서 멈췄을 때의 Project 객체(스켈레톤 목록까지는 읽혀 있다). 끝까지 읽었으면 null.</summary>
        public Node PartialRoot { get; private set; }

        /// <summary>
        /// allowPartial: 내보내기에는 스켈레톤만 있으면 되므로, Project 안 뒤쪽 편집기 상태에서 막혀도
        /// 스켈레톤 목록(Project 필드 0)을 끝까지 읽었다면 예외 대신 그때까지의 그래프를 남긴다.
        /// </summary>
        public SpineProjectDocument(byte[] body, SpineProjectSchema schema, bool allowPartial = false)
        {
            _b = body;
            _schema = schema;

            // 객체 수는 본문 크기에 비례한다(표본에서 20~30바이트에 하나). 미리 잡아 두어 커질 때마다 다시 옮기지 않게 한다.
            int expected = Math.Max(64, body.Length / 24);
            ById = new Dictionary<(string, int), Node>(expected);
            Nodes = new List<Node>(expected);
            foreach (KeyValuePair<string, string> kv in schema.Names.OrderBy(kv => int.Parse(kv.Key)))
            {
                if (_cnum.ContainsKey(kv.Value) == false)
                {
                    _cnum[kv.Value] = int.Parse(kv.Key);
                }
            }

            Top = new Node("<top>", null, 0, null);
            _stack.Push(Top);
            for (int i = 0; i < 3; i++)
            {
                Varint();
            }

            Version = KString();
            try
            {
                if (schema.RootVarint)
                {
                    Varint(); // 루트 앞 값(4.x: 스켈레톤 수)
                }

                if (schema.RootPad > 0)
                {
                    Skip(schema.RootPad); // 4.3.07~: 16바이트 값(프로젝트 ID로 보임)
                }

                TypedSlot("Project"); // 루트는 클래스 없이 표지부터 쓰는 Project

                // 뒤쪽 에디터 상태 구간: 첫 값 1은 새 객체 표지라 참조 번호를 하나 쓴다
                int tailAt = _p;
                if (Varint() == 1)
                {
                    Node tail = NewNode("EditorTail", tailAt);
                    tail.Last = _p;
                    _stack.Push(tail);
                    ReadTail();
                    Flush(tail);
                    _stack.Pop();
                    tail.End = _p;
                    Nodes.Add(tail);
                    AddSlot(tailAt, null, tail);
                }
                else
                {
                    ReadTail();
                }

                Flush(Top);
                Top.End = _p;
            }
            catch (SpineFormatException e) when (allowPartial && KeepPartial(e))
            {
                // 그때까지의 그래프를 남긴다
            }
        }

        bool KeepPartial(SpineFormatException error)
        {
            Node[] stack = _stack.ToArray(); // 맨 위부터
            Node root = stack.Length > 1 ? stack[stack.Length - 2] : null;
            if (root == null || root.Cls != "Project" || root.F.ContainsKey("0") == false)
            {
                return false;
            }

            PartialRoot = root;
            PartialError = error.Message;
            return true;
        }

        void ReadTail()
        {
            Varint();
            while (_p < _b.Length)
            {
                ObjSlot();
            }
        }

        /// <summary>본문 머리의 포맷 문자열만 읽는다(스키마 고르기용).</summary>
        public static string ReadVersion(byte[] body)
        {
            var head = new HeadReader(body);
            for (int i = 0; i < 3; i++)
            {
                head.Varint();
            }

            return head.KString();
        }

        // ------------------------------------------------------------ 바이트 읽기

#if SEI_TRACE
        public static readonly List<(string, string, string, int)> Trace = new List<(string, string, string, int)>();
#endif

        void Fail(string message, int? at = null) => throw new SpineFormatException($"@0x{at ?? _p:x}: {message}");

        byte[] Take(int n)
        {
            if (_p + n > _b.Length)
            {
                Fail(Loc.T("본문이 끝났습니다"));
            }

            var v = new byte[n];
            Buffer.BlockCopy(_b, _p, v, 0, n);
            _p += n;
            return v;
        }

        byte TakeByte()
        {
            if (_p >= _b.Length)
            {
                Fail(Loc.T("본문이 끝났습니다"));
            }

            return _b[_p++];
        }

        long Varint()
        {
            long r = 0;
            int sh = 0;
            while (true)
            {
                byte x = TakeByte();
                if (sh < 63)
                {
                    r |= (long)(x & 0x7F) << sh;
                }

                sh += 7;
                if ((x & 0x80) == 0)
                {
                    return r;
                }
            }
        }

        string KString()
        {
            byte b0 = TakeByte();
            if ((b0 & 0x80) == 0)
            {
                StringBuilder ascii = _text;
                ascii.Clear();
                ascii.Append((char)b0);
                while (true)
                {
                    byte x = TakeByte();
                    ascii.Append((char)(x & 0x7F));
                    if ((x & 0x80) != 0)
                    {
                        return ascii.ToString();
                    }
                }
            }

            int n = b0 & 0x3F;
            int sh = 6;
            if ((b0 & 0x40) != 0)
            {
                while (true)
                {
                    byte x = TakeByte();
                    n |= (x & 0x7F) << sh;
                    sh += 7;
                    if ((x & 0x80) == 0)
                    {
                        break;
                    }
                }
            }

            if (n == 0)
            {
                return null;
            }

            StringBuilder chars = _text;
            chars.Clear();
            for (int i = 0; i < n - 1; i++)
            {
                int c = TakeByte();
                if (c >= 0xE0)
                {
                    c = ((c & 0x0F) << 12) | ((TakeByte() & 0x3F) << 6) | (TakeByte() & 0x3F);
                }
                else if (c >= 0xC0)
                {
                    c = ((c & 0x1F) << 6) | (TakeByte() & 0x3F);
                }

                chars.Append((char)c);
            }

            return chars.ToString();
        }

        float Float()
        {
            if (_p + 4 > _b.Length)
            {
                Fail(Loc.T("본문이 끝났습니다"));
            }

            int bits = (_b[_p] << 24) | (_b[_p + 1] << 16) | (_b[_p + 2] << 8) | _b[_p + 3];
            _p += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }

        /// <summary>값을 쓰지 않는 구간을 건너뛴다(바이트는 조각으로 따로 기록된다).</summary>
        void Skip(int n)
        {
            if (_p + n > _b.Length)
            {
                Fail(Loc.T("본문이 끝났습니다"));
            }

            _p += n;
        }

        // 태그·클래스 번호를 문자열 키로 바꿀 때마다 문자열을 새로 만들지 않도록 작은 수는 미리 만들어 둔다
        static readonly string[] SmallNumbers = Enumerable.Range(0, 512).Select(i => i.ToString()).ToArray();

        static string Text(long n) => n >= 0 && n < SmallNumbers.Length ? SmallNumbers[n] : n.ToString();

        // ------------------------------------------------------------ 그래프 기록

        void Flush(Node node)
        {
            if (_p > node.Last)
            {
                node.Parts.Add(Part.OfSpan(node.Last, _p, _b, node.Last, _p - node.Last));
            }

            node.Last = _p;
        }

        void AddSlot(int s0, int? prefix, Node obj)
        {
            Node parent = _stack.Peek();
            if (s0 > parent.Last)
            {
                parent.Parts.Add(Part.OfSpan(parent.Last, s0, _b, parent.Last, s0 - parent.Last));
            }

            parent.Parts.Add(Part.OfSlot(s0, _p, new Slot(prefix, obj)));
            parent.Last = _p;
        }

        byte[] Slice(int a, int b)
        {
            var v = new byte[b - a];
            Buffer.BlockCopy(_b, a, v, 0, b - a);
            return v;
        }

        public string TableOf(string cls) =>
            cls == "*A" ? "A" : _schema.Tables.TryGetValue(cls, out string t) ? t : "B";

        Node NewNode(string cls, int start)
        {
            string tb = TableOf(cls);
            NodeRef id = null;
            if (tb != "none" && tb != "flag")
            {
                Objs[tb].Add(cls);
                id = new NodeRef(tb, Objs[tb].Count - 1);
            }

            var node = new Node(cls, id, start, tb);
            if (id != null)
            {
                ById[(id.Table, id.Id)] = node;
            }

            return node;
        }

        /// <summary>참조 표지: 0 = null, 1 = 새 객체, n = 이미 읽은 n-2번 객체(클래스가 속한 표에서).</summary>
        object Marker(string cls)
        {
            int at = _p;
            long r = Varint();
            if (r < 2)
            {
                return (int)r;
            }

            string tb = TableOf(cls);
            long tid = r - 2;
            if (tb == "none" || tb == "flag" || tid >= Objs[tb].Count || (cls != "*A" && Objs[tb][(int)tid] != cls))
            {
                Fail(Loc.T("역참조 {0}{1}가 {2}가 아닙니다", tb, tid, cls), at);
            }

            RefCount++;
            return new NodeRef(tb, (int)tid);
        }

        string StringSlot()
        {
            int s0 = _p;
            object m = Marker("String");
            if (m is int i && i == 1)
            {
                Node node = NewNode("String", s0);
                node.Last = _p;
                _stack.Push(node);
                node.F["s"] = new FieldValue(_p, null, KString());
                Flush(node);
                _stack.Pop();
                node.End = _p;
                Nodes.Add(node);
                AddSlot(s0, null, node);
                return (string)node.F["s"].Value;
            }

            if (m is NodeRef r)
            {
                Node node = ById[(r.Table, r.Id)];
                AddSlot(s0, null, node);
                return (string)node.F["s"].Value;
            }

            return null;
        }

        object ObjSlot()
        {
            int at = _p;
            long c = Varint();
            if (c == 0)
            {
                return null;
            }

            if (_schema.Names.TryGetValue(Text(c), out string cls) == false)
            {
                Fail(Loc.T("모르는 클래스 번호 {0}", c), at);
            }

            if (TableOf(cls) == "none")
            {
                Node plain = Body(cls, at);
                AddSlot(at, (int)c, plain);
                return plain;
            }

            object m = Marker(cls);
            if (m is int i && i == 1)
            {
                Node node = Body(cls, at);
                AddSlot(at, (int)c, node);
                return node;
            }

            if (m is NodeRef r)
            {
                AddSlot(at, (int)c, ById[(r.Table, r.Id)]);
                return r;
            }

            Fail(Loc.T("클래스 {0} 뒤 표지가 0입니다", cls), at);
            return null;
        }

        object TypedSlot(string cls)
        {
            int at = _p;
            object m = Marker(cls);
            if (m is int i && i == 1)
            {
                if (cls == "*A")
                {
                    // 대상 클래스가 바이트에 없다: «필드 수:첫 태그»로 판별(스키마 anyA)
                    int save = _p;
                    string key = $"{Varint()}:{Varint()}";
                    _p = save;
                    if (_schema.AnyA.TryGetValue(key, out string found) == false)
                    {
                        string count = key.Split(':')[0];
                        List<string> same = _schema.AnyA.Where(kv => kv.Key.Split(':')[0] == count).Select(kv => kv.Value).Distinct().ToList();
                        found = same.Count == 1 ? same[0] : null;
                    }

                    if (found == null)
                    {
                        Fail(Loc.T("*A 슬롯의 새 객체를 판별할 수 없습니다(필드 수:첫 태그 = {0})", key), at);
                    }

                    cls = found;
                }

                Node node = Body(cls, at);
                AddSlot(at, null, node);
                return node;
            }

            if (m is NodeRef r)
            {
                AddSlot(at, null, ById[(r.Table, r.Id)]);
                return r;
            }

            return null;
        }

        Node Body(string cls, int at)
        {
            if (_schema.Kinds.TryGetValue(cls, out string kind) == false)
            {
                Fail(Loc.T("모르는 클래스 {0}", cls), at);
            }

            Node node = NewNode(cls, at);
            node.Last = _p;
            _stack.Push(node);
            if (kind == "tagged" && _schema.Groups.TryGetValue(cls, out List<List<int>> groups))
            {
                ReadGrouped(node, cls, groups);
            }
            else if (kind == "tagged")
            {
                ReadTagged(node, cls);
            }
            else if (kind == "timeline")
            {
                if (_schema.TimelineRefs)
                {
                    Field(node, "group", "T");
                }

                int typeAt = _p;
                int type = (int)Varint();
                node.F["type"] = new FieldValue(typeAt, null, (long)type);
                if (_schema.Timelines.TryGetValue(Text(type), out TimelineLayout lay) == false)
                {
                    Fail(Loc.T("모르는 타임라인 종류 {0}", type));
                }

                int? saved = _tlType;
                _tlType = type;
                Field(node, "keys", "T");
                _tlType = saved;
                if (lay.Extra != null)
                {
                    Field(node, $"extra{type}", lay.Extra);
                }

                if (lay.Extra2 != null)
                {
                    Field(node, $"extraB{type}", lay.Extra2); // 시퀀스 타임라인: 슬롯 + 첨부
                }
            }
            else if (kind == "keyframe")
            {
                if (_tlType == null || _schema.Timelines.TryGetValue(Text(_tlType.Value), out TimelineLayout lay) == false)
                {
                    Fail(Loc.T("타임라인 밖의 키"));
                    return null;
                }

                if (_schema.TimelineRefs)
                {
                    Field(node, "group", "T");
                    Field(node, "timeline", "T");
                }

                Field(node, "time", "F");
                if (_schema.CurveCondition != null)
                {
                    // 핸들 블록(들어오는·나가는 핸들): 이 키나 앞 키가 베지어(1)일 때만
                    Skip(lay.Vals);
                    Field(node, "flag", "V");
                    long flag = (long)node.F["flag"].Value;
                    if (flag == 1 || (_klPrev == 1 && _schema.CurveCondition != "own"))
                    {
                        Skip(20 * lay.Curves);
                    }

                    _klPrev = (int)flag;
                }
                else
                {
                    Skip(lay.Vals + 20 * lay.Curves);
                    Field(node, "flag", "V");
                }

                if (lay.Payload != null)
                {
                    Field(node, $"payload{_tlType}", lay.Payload);
                }
            }
            else if (kind == "plain")
            {
                List<string> types = _schema.Plain[cls];
                for (int i = 0; i < types.Count; i++)
                {
                    Field(node, $"p{i}", types[i]);
                }
            }
            else if (kind == "list" || kind == "map")
            {
                node.SizeAt = _p;
                long n = Varint();
                int saved = _klPrev;
                _klPrev = 0;
                for (long i = 0; i < n * (kind == "map" ? 2 : 1); i++)
                {
                    int s = _p;
                    object v = ObjSlot();
                    node.Items.Add(new ListItem(s, _p, v));
                }

                _klPrev = saved;
            }
            else if (kind == "string")
            {
                node.F["s"] = new FieldValue(_p, null, KString());
            }
            else if (kind == "varint")
            {
                node.F["v"] = new FieldValue(_p, null, Varint());
            }
            else if (kind.StartsWith("fixed"))
            {
                Skip(int.Parse(kind.Substring(5)));
            }
            else if (kind == "rfarray" || kind == "rvarray" || kind == "rsarray" || kind == "farray")
            {
                long n = Varint();
                if (kind == "farray")
                {
                    n = Math.Max(n - 1, 0);
                }

                if (kind == "rvarray")
                {
                    for (long i = 0; i < n; i++)
                    {
                        Varint();
                    }
                }
                else
                {
                    Skip((int)(n * (kind == "rsarray" ? 2 : 4)));
                }
            }
            else if (kind == "kstrarray")
            {
                long n = Math.Max(Varint() - 1, 0);
                for (long i = 0; i < n; i++)
                {
                    KString();
                }
            }
            else if (kind == "enumarray")
            {
                long n = Math.Max(Varint() - 1, 0);
                for (long i = 0; i < n; i++)
                {
                    TypedSlot("VideoEnumA");
                }
            }
            else if (kind == "boxfarray")
            {
                long n = Math.Max(Varint() - 1, 0);
                for (long i = 0; i < n; i++)
                {
                    if (Varint() != 0)
                    {
                        Skip(4);
                    }
                }
            }
            else if (kind == "defvtx" || kind == "defvtx0")
            {
                long total = Varint();
                long chunks = Varint();
                for (long i = 0; i < chunks; i++)
                {
                    // 묶음 머리는 varint 가 아니라 1바이트다(0x80 도 나온다)
                    if (TakeByte() != 0 || kind == "defvtx")
                    {
                        Skip((int)(4 * Math.Min(128, total - 128 * i)));
                    }
                }
            }
            else
            {
                Fail(Loc.T("모르는 종류 {0}", kind));
            }

            Flush(node);
            _stack.Pop();
            node.End = _p;
            Nodes.Add(node);
            return node;
        }

        void ReadGrouped(Node node, string cls, List<List<int>> groups)
        {
            // 묶음 방식(4.3 정식 첨부): 묶음 순서대로 나오고 묶음 안 순서는 빌드마다 다르다.
            // 적힌 필드 수는 실제보다 작으므로 쓰지 않고, 묶음마다 모든 필드를 읽으면 다음 묶음으로 간다.
            Varint();
            _schema.Fields.TryGetValue(cls, out Dictionary<string, string> fields);
            List<HashSet<int>> need = groups.Select(g => new HashSet<int>(g)).ToList();
            int total = need.Sum(g => g.Count);
            int gi = 0;
            for (int i = 0; i < total; i++)
            {
                while (need[gi].Count == 0)
                {
                    gi++;
                }

                int tat = _p;
                int tag = (int)Varint();
                string key = gi == 0 ? Text(tag) : $"{gi}:{tag}";
                if (need[gi].Remove(tag) == false)
                {
                    Fail(Loc.T("모르는 필드 {0}.{1}", cls, key), tat);
                }

                ReadField(node, cls, fields, key, tat);
            }
        }

        void ReadTagged(Node node, string cls)
        {
            int tail = _schema.Tail.TryGetValue(cls, out int t) ? t : 0;
            long n0 = Varint();
            long n = n0 + tail;
            _schema.Fields.TryGetValue(cls, out Dictionary<string, string> fields);
            node.F = new FieldMap((int)Math.Min(n, 64));
            int last = -1;
            int level = 0;
            for (long i = 0; i < n; i++)
            {
                int tat = _p;
                int tag = (int)Varint();
                string key;
                if (_schema.Ascending)
                {
                    // 4.3 베타: 클래스 단계마다 태그 오름차순, 내려가면 상위 단계
                    if (tag <= last)
                    {
                        level++;
                    }

                    last = tag;
                    key = level == 0 ? Text(tag) : $"{level}:{tag}";
                }
                else
                {
                    // 4.2: 개수 뒤 꼬리 필드는 t<태그>, 같은 태그가 다시 나오면 d<태그>
                    key = i < n0 ? Text(tag) : $"t{tag}";
                    while (node.F.ContainsKey(key))
                    {
                        key = "d" + key;
                    }
                }

                ReadField(node, cls, fields, key, tat);
            }
        }

        void ReadField(Node node, string cls, Dictionary<string, string> fields, string key, int tat)
        {
            if (fields == null || fields.TryGetValue(key, out string ty) == false)
            {
                Fail(Loc.T("모르는 필드 {0}.{1}", cls, key), tat);
                return;
            }

            int vat = _p;
#if SEI_TRACE
            Trace.Add((cls, key, ty, vat));
            if (Trace.Count > 40) Trace.RemoveAt(0);
#endif
            object val = Value(cls, key, ty);
            node.F[key] = new FieldValue(vat, _p, val);
        }

        void Field(Node node, string key, string ty)
        {
            int vat = _p;
            object val = Value(node.Cls, key, ty);
            node.F[key] = new FieldValue(vat, _p, val);
        }

        object Value(string cls, string key, string ty)
        {
            switch (ty)
            {
                case "V":
                    return Varint();
                case "VB":
                {
                    // 정수 + 표지 1바이트(참조 번호를 쓰지 않는다)
                    long v = Varint();
                    TakeByte();
                    return v;
                }
                case "F":
                    return Float();
                case "S":
                    return StringSlot();
                case "O":
                    return ObjSlot();
                case "T":
                    if (_schema.Slots.TryGetValue(cls, out Dictionary<string, string> decl) == false ||
                        decl.TryGetValue(key, out string target) == false)
                    {
                        Fail(Loc.T("{0}.{1}의 선언 클래스가 없습니다", cls, key));
                        return null;
                    }

                    return TypedSlot(target);
                case "N":
                {
                    // 본문 없는 새 객체(클래스 + 표지 1)
                    int at = _p;
                    long c = Varint();
                    _schema.Names.TryGetValue(Text(c), out string cls2);
                    if (Varint() != 1)
                    {
                        Fail(Loc.T("N 슬롯이 새 객체가 아닙니다"), at);
                    }

                    Node node = NewNode(cls2, at);
                    node.End = _p;
                    Nodes.Add(node);
                    AddSlot(at, (int)c, node);
                    return null;
                }
                case "LO":
                {
                    long n = Varint();
                    var list = new List<object>();
                    for (long i = 0; i < n; i++)
                    {
                        list.Add(ObjSlot());
                    }

                    return list;
                }
                default:
                    Fail(Loc.T("모르는 값 타입 {0}", ty));
                    return null;
            }
        }

        // ------------------------------------------------------------ 조회(4.0 ~ 4.3 공통)

        public IEnumerable<Node> Of(string cls) => Nodes.Where(n => n.Cls == cls).OrderBy(n => n.Start);

        public Node Deref(object v) => v is NodeRef r ? ById[(r.Table, r.Id)] : v as Node;

        public int ClassNumber(string cls) => _cnum[cls];

        /// <summary>편집 범위로 고른 스켈레톤 이름. null 이면 프로젝트 전체(스켈레톤이 하나인 프로젝트).</summary>
        public string ScopeSkeleton;

        public Node ScopedSkeleton => ScopeSkeleton == null ? null : Of("Skeleton").FirstOrDefault(s => (string)s.Value("1") == ScopeSkeleton);

        public List<(string Name, Node Node)> EventDefs()
        {
            Node scoped = ScopedSkeleton;
            return scoped != null ? DefsOf(scoped) : Of("EventData").Select(n => ((string)n.Value("2"), n)).ToList();
        }

        List<(string Name, Node Node)> DefsOf(Node skeleton)
        {
            Node list = Deref(skeleton.Value("10"));
            return list == null
                ? new List<(string Name, Node Node)>()
                : list.Items.Select(i => Deref(i.Value)).Where(n => n != null && n.Cls == "EventData").Select(n => ((string)n.Value("2"), n)).ToList();
        }

        /// <summary>스켈레톤 정의 목록(필드 10) 순서의 정의들. 목록을 못 찾으면 파일에 나오는 순서.</summary>
        public List<(string Name, Node Node)> EventDefsInListOrder()
        {
            List<Node> skeletons = Of("Skeleton").ToList();
            Node skeleton = ScopedSkeleton ?? (skeletons.Count == 1 ? skeletons[0] : null);
            return skeleton == null || Deref(skeleton.Value("10")) == null ? EventDefs() : DefsOf(skeleton);
        }

        public Node EventDef(string name) => EventDefs().FirstOrDefault(d => d.Name == name).Node;

        /// <summary>
        /// 스켈레톤의 애니메이션 목록(필드 7)에 든 것만 돌려준다.
        /// 파일 뒤쪽 에디터 상태에는 복사본이 들어 있을 수 있어 클래스만 보면 같은 애니메이션이 두 번 나온다.
        /// </summary>
        public IEnumerable<Node> Animations()
        {
            Node scoped = ScopedSkeleton;
            List<Node> listed = (scoped != null ? new[] { scoped } : Of("Skeleton"))
                .Select(s => s.F.Keys.FirstOrDefault(k => k == "7" || k.EndsWith(":7")) is string key ? Deref(s.Value(key)) : null)
                .Where(list => list != null)
                .SelectMany(list => list.Items.Select(i => Deref(i.Value)))
                .Where(n => n != null && n.Cls == "Animation")
                .ToList();
            return listed.Count > 0 ? listed : Of("Animation");
        }

        /// <summary>
        /// 애니메이션을 가리키는 이름: «폴더 경로/이름»(JSON 내보내기와 같은 표기).
        /// 폴더가 다르면 같은 이름의 애니메이션이 있을 수 있어 이름만으로는 구분되지 않는다.
        /// </summary>
        public string AnimationName(Node anim)
        {
            string name = (string)anim.Value("1");
            Node folder = anim.F.ContainsKey("7") ? Deref(anim.Value("7")) : null;
            while (folder != null && folder.Cls == "Folder")
            {
                string part = folder.F.ContainsKey("1") ? folder.Value("1") as string : null;
                if (string.IsNullOrEmpty(part) == false)
                {
                    name = part + "/" + name;
                }

                folder = folder.F.ContainsKey("3") ? Deref(folder.Value("3")) : null;
            }

            return name;
        }

        /// <summary>«폴더 경로/이름»으로 찾는다. 이름만 주면 그 이름이 하나일 때만 찾고, 여럿이면 다른 것을 고치지 않도록 거부한다.</summary>
        public Node Animation(string name)
        {
            List<Node> all = Animations().ToList();
            Node exact = all.FirstOrDefault(a => AnimationName(a) == name);
            if (exact != null)
            {
                return exact;
            }

            List<Node> same = all.Where(a => (string)a.Value("1") == name).ToList();
            if (same.Count > 1)
            {
                throw new SpineEventUsageException(Loc.T("이름이 '{0}'인 애니메이션이 여럿입니다. 폴더 경로까지 적어 주세요", name));
            }

            return same.FirstOrDefault();
        }

        public static bool IsEventTimeline(Node tl)
        {
            if (tl.F.TryGetValue("type", out FieldValue type))
            {
                return (long)type.Value == 12; // 4.3식 고정 배치: 종류 12
            }

            return tl.F.TryGetValue("4", out FieldValue def) && (def.Value is NodeRef || def.Value is Node); // 4.0~4.2: 필드 4 = 정의
        }

        /// <summary>이벤트 타임라인이 가리키는 정의 필드. 이벤트 타임라인은 정의마다 하나다.</summary>
        public static string DefKey(Node tl) => tl.F.ContainsKey("extra12") ? "extra12" : "4";

        public static string KeysKey(Node tl) => tl.F.ContainsKey("keys") ? "keys" : "2";

        public static string TimeKey(Node key) => key.F.ContainsKey("time") ? "time" : "2";

        /// <summary>키 안에서 Event 객체가 있는 필드: 4.3식 payload12, 4.0~4.2 '7'.</summary>
        public string EventKey(Node key) => key.F.ContainsKey("payload12") ? "payload12" : _schema.KeyEventField;

        /// <summary>(이벤트 타임라인 그룹, 정의별 이벤트 타임라인들) — 없으면 (null, 빈 목록).</summary>
        public (Node Group, List<Node> Timelines) EventGroup(Node anim)
        {
            Node groups = Deref(anim.Value("2"));
            if (groups == null)
            {
                return (null, new List<Node>());
            }

            foreach (ListItem item in groups.Items)
            {
                Node group = Deref(item.Value);
                if (group == null)
                {
                    continue;
                }

                Node tlist = Deref(group.Value("2"));
                if (tlist == null)
                {
                    continue;
                }

                List<Node> evs = tlist.Items.Select(t => Deref(t.Value)).Where(t => t != null && IsEventTimeline(t)).ToList();
                if (evs.Count > 0)
                {
                    return (group, evs);
                }
            }

            return (null, new List<Node>());
        }

        public Node TimelineDef(Node tl) => Deref(tl.Value(DefKey(tl)));

        public Node KeyList(Node tl) => Deref(tl.Value(KeysKey(tl)));

        /// <summary>애니메이션의 모든 이벤트 키(정의별 타임라인을 합쳐 프레임 순).</summary>
        public List<ProjectEventKey> AnimationEventKeys(Node anim)
        {
            var keys = new List<ProjectEventKey>();
            foreach (Node tl in EventGroup(anim).Timelines)
            {
                keys.AddRange(EventKeys(tl));
            }

            return keys.OrderBy(k => k.Frame).ToList();
        }

        public List<ProjectEventKey> EventKeys(Node tl)
        {
            var keys = new List<ProjectEventKey>();
            foreach (ListItem item in KeyList(tl).Items)
            {
                Node key = Deref(item.Value);
                Node ev = Deref(key.Value(EventKey(key)));
                Node data = Deref(ev.Value("1"));
                keys.Add(new ProjectEventKey
                {
                    Frame = (float)key.Value(TimeKey(key)),
                    Name = (string)data.Value("2"),
                    Int = SpineProjectWriter.Unzigzag((long)ev.Value("2")),
                    Float = (float)ev.Value("3"),
                    String = (string)ev.Value("4"),
                    KeyNode = key,
                    Timeline = tl,
                });
            }

            return keys;
        }

        /// <summary>머리만 읽는 작은 읽개(스키마 고르기 전).</summary>
        sealed class HeadReader
        {
            readonly byte[] _b;
            int _p;

            public HeadReader(byte[] b) => _b = b;

            byte Next()
            {
                if (_p >= _b.Length)
                {
                    throw new SpineFormatException(Loc.T("본문이 너무 짧습니다"));
                }

                return _b[_p++];
            }

            public void Varint()
            {
                while ((Next() & 0x80) != 0)
                {
                }
            }

            public string KString()
            {
                byte b0 = Next();
                if ((b0 & 0x80) != 0)
                {
                    return null; // 포맷 문자열은 ASCII다
                }

                var s = new StringBuilder();
                s.Append((char)b0);
                while (true)
                {
                    byte x = Next();
                    s.Append((char)(x & 0x7F));
                    if ((x & 0x80) != 0)
                    {
                        return s.ToString();
                    }
                }
            }
        }
    }

    /// <summary>.spine 안의 이벤트 키 하나(조회 결과).</summary>
    public sealed class ProjectEventKey
    {
        public float Frame;
        public string Name;
        public int Int;
        public float Float;
        public string String;
        public Node KeyNode;
        public Node Timeline;
    }
}
