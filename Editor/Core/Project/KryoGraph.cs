using System.Collections.Generic;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>다른 객체를 담는 자리. Prefix: 클래스 번호(필드 선언이 구체 클래스면 null — 표지부터 쓴다).</summary>
    public sealed class Slot
    {
        public readonly int? Prefix;
        public readonly Node Obj;

        public Slot(int? prefix, Node obj)
        {
            Prefix = prefix;
            Obj = obj;
        }
    }

    /// <summary>본문 조각. 원래 위치(Start, End)는 고칠 자리를 찾는 데만 쓴다(복제본은 원본 위치를 물려받는다).</summary>
    public sealed class Part
    {
        public readonly bool IsSlot;
        public readonly int Start;
        public readonly int End;
        public readonly byte[] Raw;
        public readonly Slot Slot;

        Part(bool isSlot, int start, int end, byte[] raw, Slot slot)
        {
            IsSlot = isSlot;
            Start = start;
            End = end;
            Raw = raw;
            Slot = slot;
        }

        public static Part OfRaw(int start, int end, byte[] raw) => new Part(false, start, end, raw, null);
        public static Part OfSlot(int start, int end, Slot slot) => new Part(true, start, end, null, slot);
    }

    /// <summary>역참조: (표, 대상 번호).</summary>
    public sealed class NodeRef
    {
        public readonly string Table;
        public readonly int Id;

        public NodeRef(string table, int id)
        {
            Table = table;
            Id = id;
        }
    }

    /// <summary>필드 값과 그 값의 원래 위치. End 가 null 이면 끝 위치를 기록하지 않은 값(고칠 수 없음).</summary>
    public sealed class FieldValue
    {
        public readonly int At;
        public readonly int? End;
        public readonly object Value;

        public FieldValue(int at, int? end, object value)
        {
            At = at;
            End = end;
            Value = value;
        }
    }

    /// <summary>리스트 원소 (원래 시작, 원래 끝, 값).</summary>
    public sealed class ListItem
    {
        public readonly int Start;
        public readonly int End;
        public readonly object Value;

        public ListItem(int start, int end, object value)
        {
            Start = start;
            End = end;
            Value = value;
        }
    }

    /// <summary>객체 하나. 본문은 «원래 바이트 조각»과 «슬롯»의 나열이다.</summary>
    public sealed class Node
    {
        public readonly string Cls;
        public readonly NodeRef Id;
        public readonly string Table;
        public int Start;
        public int End;
        public Dictionary<string, FieldValue> F = new Dictionary<string, FieldValue>();
        public List<ListItem> Items = new List<ListItem>();
        public int? SizeAt;
        public List<Part> Parts = new List<Part>();
        internal int Last;

        public Node(string cls, NodeRef id, int start, string table)
        {
            Cls = cls;
            Id = id;
            Table = table;
            Start = start;
            End = start;
            Last = start;
        }

        public object Value(string key) => F.TryGetValue(key, out FieldValue v) ? v.Value : null;

        public override string ToString() => Id == null ? $"<{Cls}>" : $"<{Cls} {Id.Table}{Id.Id}>";
    }
}
