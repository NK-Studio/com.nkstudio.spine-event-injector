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
        public readonly Slot Slot;

        // 바이트 조각은 본문 배열의 한 구간을 가리킨다(읽을 때 조각마다 배열을 복사하지 않는다)
        readonly byte[] _buffer;
        readonly int _offset;

        /// <summary>바이트 조각의 길이(슬롯이면 0).</summary>
        public readonly int Length;

        byte[] _raw;

        Part(bool isSlot, int start, int end, byte[] buffer, int offset, int length, Slot slot)
        {
            IsSlot = isSlot;
            Start = start;
            End = end;
            _buffer = buffer;
            _offset = offset;
            Length = length;
            Slot = slot;
        }

        /// <summary>바이트 조각의 사본(슬롯이면 null). 쓰기만 할 때는 <see cref="WriteTo"/>가 복사 없이 쓴다.</summary>
        public byte[] Raw
        {
            get
            {
                if (IsSlot)
                {
                    return null;
                }

                if (_raw == null)
                {
                    _raw = new byte[Length];
                    System.Buffer.BlockCopy(_buffer, _offset, _raw, 0, Length);
                }

                return _raw;
            }
        }

        public void WriteTo(System.IO.Stream output) => output.Write(_buffer, _offset, Length);

        public static Part OfRaw(int start, int end, byte[] raw) => new Part(false, start, end, raw, 0, raw.Length, null);

        /// <summary>본문 배열의 [offset, offset + length) 를 가리키는 조각.</summary>
        public static Part OfSpan(int start, int end, byte[] buffer, int offset, int length) =>
            new Part(false, start, end, buffer, offset, length, null);

        public static Part OfSlot(int start, int end, Slot slot) => new Part(true, start, end, null, 0, 0, slot);
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

    /// <summary>
    /// 객체의 필드 표(키 → 값). 넣은 순서를 지키는 작은 배열이다.
    /// 필드는 객체마다 많아야 수십 개이고 객체는 수십만 개라, Dictionary 를 객체마다 만들면 만드는 값과 넣는 값이 읽는 시간의 큰 몫이 된다.
    /// </summary>
    public sealed class FieldMap : IEnumerable<KeyValuePair<string, FieldValue>>
    {
        string[] _keys;
        FieldValue[] _values;
        int _count;

        public FieldMap()
        {
        }

        public FieldMap(int capacity)
        {
            if (capacity > 0)
            {
                _keys = new string[capacity];
                _values = new FieldValue[capacity];
            }
        }

        public FieldMap(FieldMap other)
        {
            _count = other._count;
            if (_count > 0)
            {
                _keys = new string[_count];
                _values = new FieldValue[_count];
                System.Array.Copy(other._keys, _keys, _count);
                System.Array.Copy(other._values, _values, _count);
            }
        }

        public int Count => _count;

        public IEnumerable<string> Keys
        {
            get
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _keys[i];
                }
            }
        }

        int IndexOf(string key)
        {
            for (int i = 0; i < _count; i++)
            {
                string k = _keys[i];
                if (ReferenceEquals(k, key) || (k.Length == key.Length && string.Equals(k, key, System.StringComparison.Ordinal)))
                {
                    return i;
                }
            }

            return -1;
        }

        public bool ContainsKey(string key) => IndexOf(key) >= 0;

        public bool TryGetValue(string key, out FieldValue value)
        {
            int i = IndexOf(key);
            value = i >= 0 ? _values[i] : null;
            return i >= 0;
        }

        public FieldValue this[string key]
        {
            get
            {
                int i = IndexOf(key);
                if (i < 0)
                {
                    throw new KeyNotFoundException(key);
                }

                return _values[i];
            }

            set
            {
                int i = IndexOf(key);
                if (i >= 0)
                {
                    _values[i] = value;
                    return;
                }

                if (_keys == null)
                {
                    _keys = new string[4];
                    _values = new FieldValue[4];
                }
                else if (_count == _keys.Length)
                {
                    System.Array.Resize(ref _keys, _count * 2);
                    System.Array.Resize(ref _values, _count * 2);
                }

                _keys[_count] = key;
                _values[_count] = value;
                _count++;
            }
        }

        public IEnumerator<KeyValuePair<string, FieldValue>> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return new KeyValuePair<string, FieldValue>(_keys[i], _values[i]);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
        // 필드 표와 원소 목록은 처음 쓸 때 만든다. 큰 프로젝트는 객체가 수십만 개이고 대부분(색·배열·문자열)은
        // 둘 중 하나나 둘 다 쓰지 않으므로, 미리 만들면 그만큼의 할당이 읽는 시간을 잡아먹는다.
        FieldMap _fields;
        List<ListItem> _items;

        public FieldMap F
        {
            get => _fields ?? (_fields = new FieldMap());
            set => _fields = value;
        }

        public List<ListItem> Items
        {
            get => _items ?? (_items = new List<ListItem>());
            set => _items = value;
        }
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

        public object Value(string key) => _fields != null && _fields.TryGetValue(key, out FieldValue v) ? v.Value : null;

        public override string ToString() => Id == null ? $"<{Cls}>" : $"<{Cls} {Id.Table}{Id.Id}>";
    }
}
