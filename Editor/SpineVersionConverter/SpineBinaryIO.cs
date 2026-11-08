using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>Spine 바이너리 읽기 원시 연산(빅엔디언 int/float, varint, UTF-8 문자열).</summary>
    public sealed class SpineBinaryInput
    {
        private readonly byte[] _data;
        private int _position;

        public SpineBinaryInput(byte[] data)
        {
            _data = data;
        }

        public bool IsAtEnd => _position >= _data.Length;

        public int Remaining => _data.Length - _position;

        public void Skip(int count)
        {
            if (count > Remaining)
            {
                throw new SpineConversionException("Spine 바이너리가 중간에 끊겼다.");
            }

            _position += count;
        }

        public byte ReadByte()
        {
            if (_position >= _data.Length)
            {
                throw new SpineConversionException("Spine 바이너리가 중간에 끊겼다.");
            }

            return _data[_position++];
        }

        public sbyte ReadSByte()
        {
            return (sbyte)ReadByte();
        }

        public bool ReadBoolean()
        {
            return ReadByte() != 0;
        }

        public int ReadInt()
        {
            int result = ReadByte();
            result = (result << 8) | ReadByte();
            result = (result << 8) | ReadByte();
            result = (result << 8) | ReadByte();
            return result;
        }

        public SpineColor ReadColor(bool hasAlpha = true)
        {
            byte r = ReadByte();
            byte g = ReadByte();
            byte b = ReadByte();
            byte a = hasAlpha == true ? ReadByte() : (byte)0xff;
            return new SpineColor(r, g, b, a);
        }

        public int ReadVarint(bool optimizePositive)
        {
            int b = ReadByte();
            int value = b & 0x7F;
            if ((b & 0x80) != 0)
            {
                b = ReadByte();
                value |= (b & 0x7F) << 7;
                if ((b & 0x80) != 0)
                {
                    b = ReadByte();
                    value |= (b & 0x7F) << 14;
                    if ((b & 0x80) != 0)
                    {
                        b = ReadByte();
                        value |= (b & 0x7F) << 21;
                        if ((b & 0x80) != 0)
                        {
                            value |= (ReadByte() & 0x7F) << 28;
                        }
                    }
                }
            }

            if (optimizePositive == false)
            {
                value = (int)((uint)value >> 1) ^ -(value & 1);
            }

            return value;
        }

        public float ReadFloat()
        {
            return BitConverter.Int32BitsToSingle(ReadInt());
        }

        /// <summary>길이 0 은 null(문자열 없음), 1 은 빈 문자열이다.</summary>
        public string ReadString()
        {
            int length = ReadVarint(true);
            if (length == 0)
            {
                return null;
            }

            int byteCount = length - 1;
            if (byteCount > Remaining)
            {
                throw new SpineConversionException("Spine 바이너리의 문자열 길이가 잘못됐다.");
            }

            string value = Encoding.UTF8.GetString(_data, _position, byteCount);
            _position += byteCount;
            return value;
        }

        public string ReadRequiredString(string what)
        {
            return ReadString() ?? throw new SpineConversionException($"Spine 바이너리에 {what} 이(가) 비어 있다.");
        }

        public string ReadStringRef(List<string> strings)
        {
            int index = ReadVarint(true);
            if (index == 0)
            {
                return null;
            }

            if (index - 1 >= strings.Count)
            {
                throw new SpineConversionException($"Spine 바이너리의 문자열 참조({index})가 테이블 밖이다.");
            }

            return strings[index - 1];
        }

        public string ReadRequiredStringRef(List<string> strings, string what)
        {
            return ReadStringRef(strings) ?? throw new SpineConversionException($"Spine 바이너리에 {what} 이(가) 비어 있다.");
        }

        /// <summary>읽기 전에 남은 바이트로 길이를 검사한다 — 깨진 파일이 거대한 배열 할당을 일으키지 않게.</summary>
        public void ReadFloats(int count, List<float> into)
        {
            EnsureCount(count);
            for (int i = 0; i < count; i++)
            {
                into.Add(ReadFloat());
            }
        }

        public void ReadShorts(int count, List<int> into)
        {
            EnsureCount(count);
            for (int i = 0; i < count; i++)
            {
                // 원본은 (short) 로 자른 값을 unsigned short 배열에 담는다 — 같은 16비트로 맞춘다.
                into.Add(ReadVarint(true) & 0xFFFF);
            }
        }

        private void EnsureCount(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new SpineConversionException($"Spine 바이너리의 배열 길이({count})가 잘못됐다.");
            }
        }
    }

    /// <summary>Spine 바이너리 쓰기 원시 연산.</summary>
    public sealed class SpineBinaryOutput
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private Dictionary<string, int> _stringIndexDict = new Dictionary<string, int>();

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }

        /// <summary>문자열 참조 테이블을 정한다. 이후 <see cref="WriteStringRef"/>는 이 테이블의 1-기반 인덱스를 쓴다.</summary>
        public void SetStringTable(IReadOnlyList<string> strings)
        {
            _stringIndexDict = new Dictionary<string, int>(strings.Count);
            for (int i = 0; i < strings.Count; i++)
            {
                _stringIndexDict[strings[i]] = i + 1;
            }
        }

        public void WriteByte(int value)
        {
            _stream.WriteByte((byte)value);
        }

        public void WriteSByte(int value)
        {
            _stream.WriteByte((byte)(sbyte)value);
        }

        public void WriteBoolean(bool value)
        {
            WriteByte(value == true ? 1 : 0);
        }

        public void WriteInt(int value)
        {
            WriteByte(value >> 24);
            WriteByte(value >> 16);
            WriteByte(value >> 8);
            WriteByte(value);
        }

        public void WriteColor(SpineColor color, bool hasAlpha = true)
        {
            WriteByte(color.R);
            WriteByte(color.G);
            WriteByte(color.B);
            if (hasAlpha == true)
            {
                WriteByte(color.A);
            }
        }

        public void WriteVarint(int value, bool optimizePositive)
        {
            uint unsignedValue = optimizePositive == true ? (uint)value : (uint)((value << 1) ^ (value >> 31));
            while (unsignedValue > 0x7F)
            {
                WriteByte((int)((unsignedValue & 0x7F) | 0x80));
                unsignedValue >>= 7;
            }

            WriteByte((int)(unsignedValue & 0x7F));
        }

        public void WriteFloat(float value)
        {
            WriteInt(BitConverter.SingleToInt32Bits(value));
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteByte(0);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteVarint(bytes.Length + 1, true);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteStringRef(string value)
        {
            if (value == null)
            {
                WriteVarint(0, true);
                return;
            }

            if (_stringIndexDict.TryGetValue(value, out int index) == false)
            {
                throw new SpineConversionException($"문자열 참조 테이블에 없는 문자열이다: '{value}'");
            }

            WriteVarint(index, true);
        }

        public void WriteFloats(IEnumerable<float> values)
        {
            foreach (float value in values)
            {
                WriteFloat(value);
            }
        }

        public void WriteShorts(IEnumerable<int> values)
        {
            foreach (int value in values)
            {
                WriteVarint(value, true);
            }
        }
    }
}
