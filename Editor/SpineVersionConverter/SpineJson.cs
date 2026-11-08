using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// Spine JSON 을 다루는 공용 헬퍼. 원본(nlohmann ordered_json)의 <c>value(key, default)</c> 의미를 따른다:
    /// 키가 없으면 기본값, 있으면 그 값을 변환한다.
    /// </summary>
    public static class SpineJson
    {
        public static float GetFloat(JToken obj, string key, float defaultValue)
        {
            JToken token = Find(obj, key);
            return token == null ? defaultValue : token.Value<float>();
        }

        public static int GetInt(JToken obj, string key, int defaultValue)
        {
            JToken token = Find(obj, key);
            return token == null ? defaultValue : token.Value<int>();
        }

        public static bool GetBool(JToken obj, string key, bool defaultValue)
        {
            JToken token = Find(obj, key);
            return token == null ? defaultValue : token.Value<bool>();
        }

        public static string GetString(JToken obj, string key, string defaultValue)
        {
            JToken token = Find(obj, key);
            return token == null ? defaultValue : token.Value<string>();
        }

        public static bool Has(JToken obj, string key)
        {
            return obj is JObject jObject && jObject.ContainsKey(key);
        }

        /// <summary>키가 있고 null 이 아니면 그 값을, 아니면 null 을 돌려준다.</summary>
        public static JToken Find(JToken obj, string key)
        {
            if (obj is JObject jObject && jObject.TryGetValue(key, out JToken token) == true && token.Type != JTokenType.Null)
            {
                return token;
            }

            return null;
        }

        public static JObject FindObject(JToken obj, string key)
        {
            return Find(obj, key) as JObject;
        }

        public static IEnumerable<JToken> Items(JToken obj, string key)
        {
            return Find(obj, key) is JArray array ? array : Enumerable.Empty<JToken>();
        }

        public static IEnumerable<JProperty> Properties(JToken obj, string key)
        {
            return Find(obj, key) is JObject jObject ? jObject.Properties() : Enumerable.Empty<JProperty>();
        }

        public static List<float> GetFloats(JToken obj, string key)
        {
            return Items(obj, key).Select(token => token.Value<float>()).ToList();
        }

        public static List<int> GetInts(JToken obj, string key)
        {
            return Items(obj, key).Select(token => token.Value<int>()).ToList();
        }

        public static List<string> GetStrings(JToken obj, string key)
        {
            return Items(obj, key).Select(token => token.Value<string>()).ToList();
        }

        public static void Rename(JObject obj, string from, string to)
        {
            if (obj.TryGetValue(from, out JToken value) == false)
            {
                return;
            }

            obj.Remove(from);
            obj[to] = value;
        }

        /// <summary>
        /// float 을 JSON 숫자로 만든다. double 로 바로 넓히면 0.1f 가 0.10000000149… 로 적히므로,
        /// float 의 최단 왕복 표기를 거쳐 사람이 읽는 값 그대로 담는다.
        /// </summary>
        public static JValue Number(float value)
        {
            return new JValue(double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }

        public static JArray Numbers(IEnumerable<float> values)
        {
            return new JArray(values.Select(Number));
        }

        /// <summary>
        /// 원본처럼 공백 없이 쓴다. 정수 값은 소수점 없이 쓴다(원본 formatNumber 와 같음).
        /// XXX : 원본은 소수를 2·5자리로 잘라 쓰지만(손실), 여기서는 float 를 손실 없이 쓴다 — 변환기가 값을 깎을 이유가 없다.
        /// </summary>
        public static string Dump(JToken token)
        {
            var builder = new StringBuilder();
            Write(token, builder);
            return builder.ToString();
        }

        private static void Write(JToken token, StringBuilder builder)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                {
                    builder.Append('{');
                    bool isFirst = true;
                    foreach (JProperty property in ((JObject)token).Properties())
                    {
                        if (isFirst == false)
                        {
                            builder.Append(',');
                        }

                        isFirst = false;
                        builder.Append(JsonConvert.ToString(property.Name)).Append(':');
                        Write(property.Value, builder);
                    }

                    builder.Append('}');
                    break;
                }
                case JTokenType.Array:
                {
                    builder.Append('[');
                    var array = (JArray)token;
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        Write(array[i], builder);
                    }

                    builder.Append(']');
                    break;
                }
                case JTokenType.Float:
                {
                    double value = token.Value<double>();
                    bool isIntegral = Math.Floor(value) == value && Math.Abs(value) < 1e15;
                    builder.Append(isIntegral == true
                        ? ((long)value).ToString(CultureInfo.InvariantCulture)
                        : value.ToString("R", CultureInfo.InvariantCulture));
                    break;
                }
                case JTokenType.Integer:
                    builder.Append(token.Value<long>().ToString(CultureInfo.InvariantCulture));
                    break;
                case JTokenType.String:
                    builder.Append(JsonConvert.ToString(token.Value<string>()));
                    break;
                case JTokenType.Boolean:
                    builder.Append(token.Value<bool>() == true ? "true" : "false");
                    break;
                case JTokenType.Null:
                    builder.Append("null");
                    break;
                default:
                    throw new SpineConversionException($"JSON 으로 쓸 수 없는 값이다: {token.Type}");
            }
        }

        /// <summary>"rrggbb" / "rrggbbaa". 원본처럼 자릿수가 모자란 성분은 0xff 로 채운다.</summary>
        public static SpineColor ParseColor(string text, bool hasAlpha)
        {
            byte ParseHex(int index)
            {
                if (index * 2 + 1 >= text.Length)
                {
                    return 0xff;
                }

                return byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return new SpineColor(ParseHex(0), ParseHex(1), ParseHex(2), hasAlpha == true ? ParseHex(3) : (byte)0xff);
        }

        public static string FormatColor(SpineColor color, bool hasAlpha)
        {
            string rgb = $"{color.R:x2}{color.G:x2}{color.B:x2}";
            return hasAlpha == true ? rgb + $"{color.A:x2}" : rgb;
        }

        /// <summary>skeleton.hash: 빅엔디언 8바이트의 base64 에서 끝 '=' 를 뗀 값.</summary>
        public static string HashToBase64(ulong hash)
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                bytes[7 - i] = (byte)(hash >> (i * 8));
            }

            return Convert.ToBase64String(bytes).TrimEnd('=');
        }

        public static ulong Base64ToHash(string text)
        {
            string padded = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            byte[] bytes = Convert.FromBase64String(padded);
            int start = Math.Max(0, bytes.Length - 8);
            ulong hash = 0;
            for (int i = start; i < bytes.Length; i++)
            {
                hash = (hash << 8) | bytes[i];
            }

            return hash;
        }

        private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        /// <summary>
        /// 3.8 해시 문자열(보통 SHA-1 20바이트의 base64) → 디코드 결과의 마지막 8바이트(원본 common.cpp base64ToUint64).
        /// <see cref="Base64ToHash"/> 와 달리 원본 decodeBase64 처럼 관대하다: 알파벳 밖 문자('=' 포함)에서 멈추고,
        /// 남는 비트는 버린다. 길이가 4 의 배수가 아니거나 잘못된 문자가 있어도 예외 없이 원본과 같은 값을 낸다.
        /// </summary>
        public static ulong LenientBase64ToHash(string text)
        {
            var bytes = new List<byte>();
            int value = 0;
            int bitCount = -8;
            foreach (char c in text)
            {
                int digit = c < 128 ? Base64Alphabet.IndexOf(c) : -1;
                if (digit < 0)
                {
                    break;
                }

                value = (value << 6) + digit;
                bitCount += 6;
                if (bitCount >= 0)
                {
                    bytes.Add((byte)((value >> bitCount) & 0xFF));
                    bitCount -= 8;
                }
            }

            int start = Math.Max(0, bytes.Count - 8);
            ulong hash = 0;
            for (int i = start; i < bytes.Count; i++)
            {
                hash = (hash << 8) | bytes[i];
            }

            return hash;
        }
    }

    /// <summary>JSON 이름과 바이너리 타임라인 번호 표. 배열 인덱스가 곧 enum 값/바이너리 번호다.</summary>
    public static class SpineNames
    {
        public static readonly string[] Inherit = { "normal", "onlyTranslation", "noRotationOrReflection", "noScale", "noScaleOrReflection" };
        public static readonly string[] BlendMode = { "normal", "additive", "multiply", "screen" };
        public static readonly string[] PositionMode = { "fixed", "percent" };
        public static readonly string[] SpacingMode = { "length", "fixed", "percent", "proportional" };
        public static readonly string[] RotateMode = { "tangent", "chain", "chainScale" };
        public static readonly string[] AttachmentType = { "region", "boundingbox", "mesh", "linkedmesh", "path", "point", "clipping" };
        public static readonly string[] SequenceMode = { "hold", "once", "loop", "pingpong", "onceReverse", "loopReverse", "pingpongReverse" };

        public static readonly string[] SlotTimeline = { "attachment", "rgba", "rgb", "rgba2", "rgb2", "alpha" };
        public static readonly string[] BoneTimeline = { "rotate", "translate", "translatex", "translatey", "scale", "scalex", "scaley", "shear", "shearx", "sheary", "inherit" };
        public static readonly string[] PathTimeline = { "position", "spacing", "mix" };

        // 3 번은 비어 있다(런타임 번호 체계 그대로).
        public static readonly string[] PhysicsTimeline = { "inertia", "strength", "damping", null, "mass", "wind", "gravity", "mix", "reset" };
        public static readonly string[] AttachmentTimeline = { "deform", "sequence" };

        public static int IndexOf(string[] names, string value, string what)
        {
            int index = Array.IndexOf(names, value);
            if (index < 0 || value == null)
            {
                throw new SpineConversionException($"알 수 없는 {what}: '{value}'");
            }

            return index;
        }

        public static string NameAt(string[] names, int index, string what)
        {
            if (index < 0 || index >= names.Length || names[index] == null)
            {
                throw new SpineConversionException($"알 수 없는 {what} 번호: {index}");
            }

            return names[index];
        }
    }
}
