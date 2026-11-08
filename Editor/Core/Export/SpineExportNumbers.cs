using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>float 와 비트 표현을 바꾼다(BitConverter 의 배열 할당 없이, 바이트 순서와 무관하게).</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public int I;

        public static int ToInt(float value)
        {
            var b = default(FloatBits);
            b.F = value;
            return b.I;
        }

        public static float ToFloat(int value)
        {
            var b = default(FloatBits);
            b.I = value;
            return b.F;
        }

        /// <summary>빅엔디안 float32 하나.</summary>
        public static float Read(byte[] data, int at) =>
            ToFloat((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
    }

    /// <summary>
    /// JSON 숫자 표기. 파이썬 PoC(json_writer.py)의 규칙을 정수 산술(BigInteger)로 그대로 옮겼다.
    /// 런타임마다 다른 double.ToString("R") 에 기대지 않는다(Unity 의 Mono 와 .NET 의 결과가 다르다).
    /// </summary>
    internal static class SpineNumbers
    {
        static readonly BigInteger[] PowersOfTen = BuildPowers();

        static BigInteger[] BuildPowers()
        {
            var table = new BigInteger[400];
            table[0] = BigInteger.One;
            for (int i = 1; i < table.Length; i++)
            {
                table[i] = table[i - 1] * 10;
            }

            return table;
        }

        static BigInteger Pow10(int n) => n < PowersOfTen.Length ? PowersOfTen[n] : BigInteger.Pow(10, n);

        public static double F32(double x) => (float)x;

        /// <summary>Math.round(x * 10^d) / 10^d — 값은 float32, 곱셈은 double.</summary>
        public static double RoundHalfUp(double x, int digits)
        {
            double m = Scale(digits);
            return Math.Floor(F32(x) * m + 0.5) / m;
        }

        static double Scale(int digits)
        {
            switch (digits)
            {
                case 2: return 100.0;
                case 3: return 1000.0;
                case 4: return 10000.0;
                case 5: return 100000.0;
                default: return Math.Pow(10.0, digits);
            }
        }

        public static string FmtRound(double x, int digits) => JavaDouble(RoundHalfUp(x, digits));

        static string Int(double x) => ((long)x).ToString(CultureInfo.InvariantCulture);

        /// <summary>Java Double.toString 과 같은 표기(정수면 '.0' 을 뗀 형태).</summary>
        public static string JavaDouble(double v)
        {
            if (v == 0)
            {
                return "0";
            }

            if (v == Math.Truncate(v) && Math.Abs(v) < 1e7)
            {
                return Int(v);
            }

            double a = Math.Abs(v);
            string sign = v < 0 ? "-" : string.Empty;
            ShortestDouble(a, out string digits, out int point);
            if (1e-3 <= a && a < 1e7)
            {
                if (point <= 0)
                {
                    return sign + "0." + new string('0', -point) + digits;
                }

                return point >= digits.Length
                    ? sign + digits + new string('0', point - digits.Length) + ".0"
                    : sign + digits.Substring(0, point) + "." + digits.Substring(point);
            }

            string mantissa = digits[0] + "." + (digits.Length > 1 ? digits.Substring(1) : "0");
            return sign + mantissa + "E" + ScientificExponent(a).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>a 를 유효숫자 18자리로 반올림했을 때의 십진 지수(파이썬 '%.17e' 의 지수).</summary>
        static int ScientificExponent(double a)
        {
            Decompose(a, out long m, out int e);
            BigInteger num = m;
            BigInteger den = BigInteger.One;
            if (e >= 0)
            {
                num <<= e;
            }
            else
            {
                den <<= -e;
            }

            int k = (int)Math.Floor(Math.Log10(a));
            while (Compare(num, den, k) < 0)
            {
                k--;
            }

            while (Compare(num, den, k + 1) >= 0)
            {
                k++;
            }

            // n = a / 10^(k-17) 를 절반은 짝수로 반올림: 10^18 이 되면 지수가 하나 오른다
            int shift = 17 - k;
            BigInteger n = shift >= 0 ? num * Pow10(shift) : num;
            BigInteger d = shift >= 0 ? den : den * Pow10(-shift);
            BigInteger q = BigInteger.DivRem(n, d, out BigInteger r);
            BigInteger twice = r * 2;
            if (twice > d || (twice == d && q.IsEven == false))
            {
                q += 1;
            }

            return q == Pow10(18) ? k + 1 : k;
        }

        /// <summary>num/den 과 10^k 비교.</summary>
        static int Compare(BigInteger num, BigInteger den, int k) =>
            k >= 0 ? num.CompareTo(den * Pow10(k)) : (num * Pow10(-k)).CompareTo(den);

        static void Decompose(double a, out long mantissa, out int exponent)
        {
            long bits = BitConverter.DoubleToInt64Bits(a);
            int biased = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            if (biased == 0)
            {
                mantissa = frac;
                exponent = -1074;
            }
            else
            {
                mantissa = frac | (1L << 52);
                exponent = biased - 1075;
            }
        }

        /// <summary>왕복 가능한 가장 짧은 십진수(파이썬 repr 과 같은 값). 값 = 0.digits × 10^point.</summary>
        static void ShortestDouble(double a, out string digits, out int point)
        {
            Decompose(a, out long m, out int e);
            bool even = (m & 1) == 0;
            bool lowerGap = m == (1L << 52) && e > -1074;
            BigInteger r, s, mp, mm;
            if (e >= 0)
            {
                BigInteger be = BigInteger.One << e;
                if (lowerGap)
                {
                    r = (BigInteger)m * be * 4;
                    s = 4;
                    mp = be * 2;
                    mm = be;
                }
                else
                {
                    r = (BigInteger)m * be * 2;
                    s = 2;
                    mp = be;
                    mm = be;
                }
            }
            else if (lowerGap)
            {
                r = (BigInteger)m * 4;
                s = BigInteger.One << (-e + 2);
                mp = 2;
                mm = 1;
            }
            else
            {
                r = (BigInteger)m * 2;
                s = BigInteger.One << (-e + 1);
                mp = 1;
                mm = 1;
            }

            int k = (int)Math.Ceiling(Math.Log10(a) - 1e-10);
            if (k >= 0)
            {
                s *= Pow10(k);
            }
            else
            {
                BigInteger scale = Pow10(-k);
                r *= scale;
                mp *= scale;
                mm *= scale;
            }

            // 추정한 k 맞추기: (r + m+)/s 가 1 미만(경계 포함 여부는 짝수 규칙)이어야 한다
            while (even ? r + mp >= s : r + mp > s)
            {
                s *= 10;
                k++;
            }

            while (true)
            {
                BigInteger r10 = r * 10;
                BigInteger mp10 = mp * 10;
                if (even ? r10 + mp10 >= s : r10 + mp10 > s)
                {
                    break;
                }

                r = r10;
                mp = mp10;
                mm *= 10;
                k--;
            }

            var sb = new StringBuilder(18);
            while (true)
            {
                r *= 10;
                mp *= 10;
                mm *= 10;
                BigInteger q = BigInteger.DivRem(r, s, out r);
                int d = (int)q;
                bool low = even ? r <= mm : r < mm;
                bool high = even ? r + mp >= s : r + mp > s;
                if (low == false && high == false)
                {
                    sb.Append((char)('0' + d));
                    continue;
                }

                if (low && high)
                {
                    int cmp = (r * 2).CompareTo(s);
                    d += cmp > 0 || (cmp == 0 && (d & 1) != 0) ? 1 : 0;
                }
                else if (high)
                {
                    d += 1;
                }

                AppendWithCarry(sb, d, ref k);
                break;
            }

            digits = sb.ToString().TrimEnd('0');
            point = k;
        }

        static void AppendWithCarry(StringBuilder sb, int d, ref int k)
        {
            if (d < 10)
            {
                sb.Append((char)('0' + d));
                return;
            }

            int i = sb.Length - 1;
            sb.Append('0');
            while (i >= 0 && sb[i] == '9')
            {
                sb[i] = '0';
                i--;
            }

            if (i >= 0)
            {
                sb[i]++;
            }
            else
            {
                sb.Insert(0, '1');
                k++;
            }
        }

        /// <summary>4.3 편집기 숫자 표기에 가장 가까운 규칙: float32 이웃 간격의 3/4 안에서 가장 짧은 십진수(Dragon4 자유 형식).</summary>
        static string Dragon(double x)
        {
            if (x == 0)
            {
                return "0";
            }

            bool negative = x < 0;
            x = Math.Abs(x);
            int bits = FloatBits.ToInt((float)x);
            int biased = (bits >> 23) & 0xFF;
            int frac = bits & 0x7FFFFF;
            int e = biased == 0 ? -149 : biased - 150;
            long mantissa = biased == 0 ? frac : frac | 0x800000;

            // 단위 2^(e-2) 로 모두 정수가 된다: 값 = M·4, 위 간격·3/2 = 6, 아래 간격·3/2 = 6(지수 경계면 3)
            BigInteger up = e > 2 ? BigInteger.One << (e - 2) : BigInteger.One;
            BigInteger s = e < 2 ? BigInteger.One << (2 - e) : BigInteger.One;
            BigInteger r = mantissa * 4 * up;
            BigInteger mm = (frac == 0 && biased > 1 ? 3 : 6) * up;
            BigInteger mp = 6 * up;

            // 10^(k-1) <= 값 < 10^k
            int k = (int)Math.Ceiling(Math.Log10(x));
            while (Compare(r, s, k) >= 0)
            {
                k++;
            }

            while (Compare(r, s, k - 1) < 0)
            {
                k--;
            }

            if (k >= 0)
            {
                s *= Pow10(k);
            }
            else
            {
                BigInteger scale = Pow10(-k);
                r *= scale;
                mm *= scale;
                mp *= scale;
            }

            var digits = new List<int>(14);
            int d;
            bool low;
            bool high;
            while (true)
            {
                r *= 10;
                mm *= 10;
                mp *= 10;
                d = (int)BigInteger.DivRem(r, s, out r);
                low = r < mm;
                high = r > s - mp;
                if (low || high || digits.Count > 12)
                {
                    break;
                }

                digits.Add(d);
            }

            if (high && (low == false || r * 2 >= s))
            {
                d++;
            }

            digits.Add(d);
            for (int i = digits.Count - 1; digits[i] == 10; i--)
            {
                digits[i] = 0;
                if (i == 0)
                {
                    digits.Insert(0, 1);
                    k++;
                    break;
                }

                digits[i - 1]++;
            }

            var text = new StringBuilder(digits.Count);
            foreach (int digit in digits)
            {
                text.Append((char)('0' + digit));
            }

            string t = text.ToString().TrimEnd('0');
            if (t.Length == 0)
            {
                t = "0";
            }

            string output;
            if (k <= 0)
            {
                output = "0." + new string('0', -k) + t;
            }
            else if (k >= t.Length)
            {
                output = t + new string('0', k - t.Length);
            }
            else
            {
                output = t.Substring(0, k) + "." + t.Substring(k);
            }

            return negative ? "-" + output : output;
        }

        /// <summary>Java Float.toString 과 같은 가장 짧은 표기. intOk 면 정수는 '.0' 없이.</summary>
        public static string FmtF32(double value, bool intOk = true)
        {
            double x = F32(value);
            if (x == Math.Truncate(x) && Math.Abs(x) < 1e7)
            {
                return intOk ? Int(x) : Int(x) + ".0";
            }

            string s = Dragon(x);
            if (s.IndexOf('.') < 0 && intOk == false)
            {
                s += ".0";
            }

            double a = Math.Abs(x);
            if (a < 1e-3 || a >= 1e7)
            {
                // Java식 과학 표기. 지수는 십진 표기에서 직접 센다(0.0001f 의 log10 은 -4 보다 살짝 작아 한 자리 어긋난다)
                string plain = s.TrimStart('-');
                string digits = plain.Replace(".", string.Empty).TrimStart('0').TrimEnd('0');
                if (digits.Length == 0)
                {
                    digits = "0";
                }

                int exponent;
                int dot = plain.IndexOf('.');
                if (a < 1e-3)
                {
                    string fraction = plain.Substring(dot + 1);
                    exponent = -(fraction.Length - fraction.TrimStart('0').Length + 1);
                }
                else
                {
                    exponent = (dot < 0 ? plain.Length : dot) - 1;
                }

                s = (x < 0 ? "-" : string.Empty) + digits[0] + "." + (digits.Length > 1 ? digits.Substring(1) : "0") +
                    "E" + exponent.ToString(CultureInfo.InvariantCulture);
            }

            return s;
        }
    }

    /// <summary>편집기의 이름 정렬: 글자 단위로 비교하되 양쪽이 숫자면 수로 비교(flare2 &lt; flare10, muzzle-glow &lt; muzzle01).</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string a, string b)
        {
            int i = 0;
            int j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int i2 = i;
                    while (i2 < a.Length && char.IsDigit(a[i2]))
                    {
                        i2++;
                    }

                    int j2 = j;
                    while (j2 < b.Length && char.IsDigit(b[j2]))
                    {
                        j2++;
                    }

                    int order = CompareRuns(a, i, i2, b, j, j2);
                    if (order != 0)
                    {
                        return order;
                    }

                    i = i2;
                    j = j2;
                    continue;
                }

                if (a[i] != b[j])
                {
                    char ca = char.ToLowerInvariant(a[i]);
                    char cb = char.ToLowerInvariant(b[j]);
                    if (ca != cb)
                    {
                        return ca < cb ? -1 : 1;
                    }

                    return a[i] < b[j] ? -1 : 1;
                }

                i++;
                j++;
            }

            return (a.Length - i) - (b.Length - j);
        }

        /// <summary>숫자 구간을 값으로 비교한다(길이에 제한이 없게 자릿수로).</summary>
        static int CompareRuns(string a, int i, int i2, string b, int j, int j2)
        {
            while (i < i2 && CharUnicodeInfo.GetDecimalDigitValue(a[i]) == 0)
            {
                i++;
            }

            while (j < j2 && CharUnicodeInfo.GetDecimalDigitValue(b[j]) == 0)
            {
                j++;
            }

            if (i2 - i != j2 - j)
            {
                return i2 - i < j2 - j ? -1 : 1;
            }

            for (; i < i2; i++, j++)
            {
                int x = CharUnicodeInfo.GetDecimalDigitValue(a[i]);
                int y = CharUnicodeInfo.GetDecimalDigitValue(b[j]);
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }

            return 0;
        }
    }
}
