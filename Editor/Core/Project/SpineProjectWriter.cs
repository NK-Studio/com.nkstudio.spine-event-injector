using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// 객체 그래프를 본문 바이트로 쓰고(Kryo 참조 규칙), 그래프를 고치는 도구들.
    /// 어떤 객체든 «처음 만나는 슬롯»에 본문이 들어가고 번호도 그 순서로 매겨지므로, 참조 번호 재계산과
    /// «정의가 처음 쓰이는 위치» 문제가 저절로 풀린다. 아무것도 고치지 않으면 원본과 바이트가 같다.
    /// </summary>
    public static class SpineProjectWriter
    {
        // ------------------------------------------------------------ 기본 인코딩

        public static byte[] Varint(long v)
        {
            var out_ = new List<byte>();
            ulong u = (ulong)v;
            while (true)
            {
                byte x = (byte)(u & 0x7F);
                u >>= 7;
                if (u == 0)
                {
                    out_.Add(x);
                    return out_.ToArray();
                }

                out_.Add((byte)(x | 0x80));
            }
        }

        public static long Zigzag(int i) => (uint)((i << 1) ^ (i >> 31));

        public static int Unzigzag(long v) => (int)((v >> 1) ^ -(v & 1));

        public static byte[] FloatBytes(float f)
        {
            byte[] b = BitConverter.GetBytes(f);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b);
            }

            return b;
        }

        /// <summary>Kryo writeString: 2~32자 ASCII는 마지막 바이트 |0x80, 그 외는 «플래그 varint 길이+1» + UTF-8.</summary>
        public static byte[] KryoString(string s)
        {
            if (s.Length > 1 && s.Length <= 32 && s.All(c => c >= 0x20 && c < 0x80))
            {
                byte[] ascii = Encoding.ASCII.GetBytes(s);
                ascii[ascii.Length - 1] |= 0x80;
                return ascii;
            }

            int n = s.Length + 1;
            var out_ = new List<byte> { (byte)(0x80 | (n & 0x3F) | ((n >> 6) != 0 ? 0x40 : 0)) };
            if ((n >> 6) != 0)
            {
                out_.AddRange(Varint(n >> 6));
            }

            out_.AddRange(Encoding.UTF8.GetBytes(s));
            return out_.ToArray();
        }

        // ------------------------------------------------------------ 쓰기

        /// <summary>그래프를 본문 바이트로. 객체는 처음 만나는 슬롯에서 본문째 쓰고(표지 1), 그 뒤로는 번호 + 2.</summary>
        public static byte[] Serialize(SpineProjectDocument doc)
        {
            var output = new MemoryStream(doc.Top.End > 0 ? doc.Top.End + 1024 : 4096);
            var ids = new Dictionary<string, Dictionary<Node, int>>
            {
                ["A"] = new Dictionary<Node, int>(ReferenceComparer.Instance),
                ["B"] = new Dictionary<Node, int>(ReferenceComparer.Instance),
            };
            PutBody(doc.Top, output, ids);
            return output.ToArray();
        }

        static void PutBody(Node o, MemoryStream output, Dictionary<string, Dictionary<Node, int>> ids)
        {
            foreach (Part part in o.Parts)
            {
                if (part.IsSlot == false)
                {
                    part.WriteTo(output);
                    continue;
                }

                Slot s = part.Slot;
                Node obj = s.Obj;
                if (s.Prefix.HasValue)
                {
                    PutVarint(output, s.Prefix.Value);
                }

                if (obj.Table == "none")
                {
                    PutBody(obj, output, ids);
                    continue;
                }

                if (obj.Table == "flag")
                {
                    output.WriteByte(1); // 참조 추적 없이 «null 아님» 표지(1)만
                    PutBody(obj, output, ids);
                    continue;
                }

                Dictionary<Node, int> seen = ids[obj.Table];
                if (seen.TryGetValue(obj, out int id))
                {
                    PutVarint(output, id + 2);
                    continue;
                }

                seen[obj] = seen.Count;
                output.WriteByte(1);
                PutBody(obj, output, ids);
            }
        }

        static void Write(Stream s, byte[] b) => s.Write(b, 0, b.Length);

        /// <summary><see cref="Varint"/>와 같은 바이트를 배열 없이 바로 쓴다.</summary>
        static void PutVarint(Stream s, long v)
        {
            ulong u = (ulong)v;
            while (true)
            {
                byte x = (byte)(u & 0x7F);
                u >>= 7;
                if (u == 0)
                {
                    s.WriteByte(x);
                    return;
                }

                s.WriteByte((byte)(x | 0x80));
            }
        }

        sealed class ReferenceComparer : IEqualityComparer<Node>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Node a, Node b) => ReferenceEquals(a, b);
            public int GetHashCode(Node n) => RuntimeHelpers.GetHashCode(n);
        }

        // ------------------------------------------------------------ 그래프 편집

        /// <summary>node 본문에서 원래 위치 [p0, p1)를 새 조각들로 바꾼다(p0 == p1 이면 그 자리에 끼운다).</summary>
        public static void ReplaceRange(Node node, int p0, int p1, IEnumerable<Part> replacement)
        {
            var before = new List<Part>();
            var after = new List<Part>();
            foreach (Part part in node.Parts)
            {
                if (part.End <= p0)
                {
                    before.Add(part);
                }
                else if (part.Start >= p1)
                {
                    after.Add(part);
                }
                else if (p0 <= part.Start && part.End <= p1)
                {
                }
                else
                {
                    if (part.IsSlot || part.Length != part.End - part.Start)
                    {
                        throw new SpineFormatException(Loc.T("{0} 본문의 [0x{1:x}, 0x{2:x})를 고칠 수 없습니다", node.Cls, p0, p1));
                    }

                    if (part.Start < p0)
                    {
                        before.Add(Part.OfRaw(part.Start, p0, Sub(part.Raw, 0, p0 - part.Start)));
                    }

                    if (part.End > p1)
                    {
                        after.Add(Part.OfRaw(p1, part.End, Sub(part.Raw, p1 - part.Start, part.End - p1)));
                    }
                }
            }

            var parts = new List<Part>(before);
            foreach (Part r in replacement)
            {
                parts.Add(r.IsSlot ? Part.OfSlot(p0, p0, r.Slot) : Part.OfRaw(p0, p0, r.Raw));
            }

            parts.AddRange(after);
            node.Parts = parts;
        }

        static byte[] Sub(byte[] b, int at, int n)
        {
            var v = new byte[n];
            Buffer.BlockCopy(b, at, v, 0, n);
            return v;
        }

        static (int At, int End) Span(Node node, string key)
        {
            FieldValue f = node.F[key];
            if (f.End == null)
            {
                throw new SpineFormatException(Loc.T("{0}.{1}의 끝 위치를 몰라 고칠 수 없습니다", node.Cls, key));
            }

            return (f.At, f.End.Value);
        }

        public static void SetRaw(Node node, string key, byte[] data)
        {
            (int at, int end) = Span(node, key);
            ReplaceRange(node, at, end, new[] { Part.OfRaw(0, 0, data) });
        }

        public static void SetSlot(Node node, string key, Slot slot)
        {
            (int at, int end) = Span(node, key);
            ReplaceRange(node, at, end, new[] { Part.OfSlot(0, 0, slot) });
        }

        /// <summary>필드 key 자리의 슬롯이 가리키는 객체(복제본에서도 복제된 자식을 돌려준다).</summary>
        public static Node Child(Node node, string key)
        {
            int at = node.F[key].At;
            return node.Parts.FirstOrDefault(p => p.IsSlot && p.Start == at)?.Slot.Obj;
        }

        public static List<Slot> ListSlots(Node node) => node.Parts.Where(p => p.IsSlot).Select(p => p.Slot).ToList();

        /// <summary>리스트 본문을 크기 varint + 슬롯들로 다시 짠다(원소에 null 이 없을 때만).</summary>
        public static void SetList(Node node, IList<Slot> slots)
        {
            if (node.Items.Any(i => i.Value == null))
            {
                throw new SpineFormatException(Loc.T("{0} 리스트에 null 원소가 있어 고칠 수 없습니다", node.Cls));
            }

            int at = node.SizeAt ?? throw new SpineFormatException(Loc.T("{0}는 리스트가 아닙니다", node.Cls));
            var parts = new List<Part> { Part.OfRaw(at, at, Varint(slots.Count)) };
            parts.AddRange(slots.Select(s => Part.OfSlot(at, at, s)));
            node.Parts = parts;
        }

        /// <summary>객체 복제. deep 클래스의 자식은 함께 복제하고, 이미 복제한 객체(부모 등)를 가리키는 슬롯은 복제본으로 돌린다.</summary>
        public static Node Clone(Node node, ICollection<string> deep, Dictionary<Node, Node> memo = null)
        {
            memo = memo ?? new Dictionary<Node, Node>(ReferenceComparer.Instance);
            var copy = new Node(node.Cls, null, node.Start, node.Table)
            {
                F = new FieldMap(node.F),
                Items = new List<ListItem>(node.Items),
                SizeAt = node.SizeAt,
                End = node.End,
            };
            memo[node] = copy;
            foreach (Part part in node.Parts)
            {
                if (part.IsSlot == false)
                {
                    copy.Parts.Add(part);
                    continue;
                }

                Node o = part.Slot.Obj;
                if (memo.TryGetValue(o, out Node done))
                {
                    o = done;
                }
                else if (deep.Contains(o.Cls))
                {
                    o = Clone(o, deep, memo);
                }

                copy.Parts.Add(Part.OfSlot(part.Start, part.End, new Slot(part.Slot.Prefix, o)));
            }

            return copy;
        }

        /// <summary>새 문자열 객체. 빈 문자열은 Spine처럼 이미 있는 '' 객체를 함께 쓴다.</summary>
        public static Node NewString(SpineProjectDocument doc, string s)
        {
            if (s == string.Empty)
            {
                Node empty = doc.Nodes.FirstOrDefault(n => n.Cls == "String" && (string)n.Value("s") == string.Empty);
                if (empty != null)
                {
                    return empty;
                }
            }

            var node = new Node("String", null, 0, doc.TableOf("String"));
            node.Parts.Add(Part.OfRaw(0, 0, KryoString(s)));
            node.F["s"] = new FieldValue(0, 0, s);
            return node;
        }
    }
}
