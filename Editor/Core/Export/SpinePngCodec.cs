using System;
using System.IO;
using System.IO.Compression;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>PNG 읽기·쓰기. 이미지 라이브러리 없이 8·16비트 그레이/RGB/팔레트/알파를 RGBA 로 바꾼다(인터레이스 없음).</summary>
    public static class SpinePngCodec
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>PNG 파일 내용 → 위쪽 줄부터의 RGBA(픽셀당 4바이트, straight).</summary>
        public static byte[] Decode(byte[] data, out int width, out int height, string nameForMessage)
        {
            if (data.Length < 8 || StartsWithSignature(data) == false)
            {
                throw new SpineFormatException(Loc.T("PNG가 아닙니다: {0}", nameForMessage));
            }

            int w = 0;
            int h = 0;
            int depth = 0;
            int colorType = 0;
            int interlace = 0;
            byte[] palette = null;
            byte[] transparency = null;
            using (var idat = new MemoryStream())
            {
                int p = 8;
                while (p + 8 <= data.Length)
                {
                    int length = ReadInt(data, p);
                    string kind = ChunkKind(data, p + 4);
                    int body = p + 8;
                    if (length < 0 || body + length > data.Length)
                    {
                        throw new SpineFormatException(Loc.T("{0}: PNG 데이터가 잘렸습니다", nameForMessage));
                    }

                    p = body + length + 4;
                    if (kind == "IHDR")
                    {
                        w = ReadInt(data, body);
                        h = ReadInt(data, body + 4);
                        depth = data[body + 8];
                        colorType = data[body + 9];
                        interlace = data[body + 12];
                    }
                    else if (kind == "PLTE")
                    {
                        palette = Slice(data, body, length);
                    }
                    else if (kind == "tRNS")
                    {
                        transparency = Slice(data, body, length);
                    }
                    else if (kind == "IDAT")
                    {
                        idat.Write(data, body, length);
                    }
                    else if (kind == "IEND")
                    {
                        break;
                    }
                }

                if (interlace != 0)
                {
                    throw new SpineFormatException(Loc.T("인터레이스 PNG는 지원하지 않습니다: {0}", nameForMessage));
                }

                if (depth != 8 && depth != 16 && (colorType == 3 && (depth == 1 || depth == 2 || depth == 4)) == false)
                {
                    throw new SpineFormatException(Loc.T("{0}비트 PNG는 지원하지 않습니다: {1}", depth, nameForMessage));
                }

                int channels = Channels(colorType, nameForMessage);
                width = w;
                height = h;
                return Unfilter(Inflate(idat), w, h, depth, colorType, channels, palette, transparency, nameForMessage);
            }
        }

        static bool StartsWithSignature(byte[] data)
        {
            for (int i = 0; i < Signature.Length; i++)
            {
                if (data[i] != Signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        static int ReadInt(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

        static string ChunkKind(byte[] d, int at) => new string(new[] { (char)d[at], (char)d[at + 1], (char)d[at + 2], (char)d[at + 3] });

        static byte[] Slice(byte[] d, int at, int length)
        {
            var v = new byte[length];
            Buffer.BlockCopy(d, at, v, 0, length);
            return v;
        }

        static int Channels(int colorType, string name)
        {
            switch (colorType)
            {
                case 0: return 1;
                case 2: return 3;
                case 3: return 1;
                case 4: return 2;
                case 6: return 4;
                default: throw new SpineFormatException(Loc.T("{0}: 모르는 PNG 색 형식 {1}", name, colorType));
            }
        }

        static byte[] Inflate(MemoryStream zlibStream)
        {
            byte[] z = zlibStream.ToArray();
            using (var input = new MemoryStream(z, 2, Math.Max(z.Length - 2, 0)))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                inflate.CopyTo(output);
                return output.ToArray();
            }
        }

        static byte[] Unfilter(byte[] raw, int w, int h, int depth, int colorType, int channels, byte[] palette, byte[] transparency, string name)
        {
            int bits = depth * channels;
            int bpp = Math.Max(1, bits / 8);
            long strideLong = (w * (long)bits + 7) / 8;
            if (w <= 0 || h <= 0 || w > 65536 || h > 65536 || (long)w * h * 4 > int.MaxValue)
            {
                throw new SpineFormatException(Loc.T("{0}: PNG 크기가 잘못되었습니다", name));
            }

            int stride = (int)strideLong;
            var output = new byte[w * h * 4];
            var line = new byte[stride];
            var prev = new byte[stride];
            int q = 0;
            int step = Math.Max(depth / 8, 1);
            for (int y = 0; y < h; y++)
            {
                if (q + 1 + stride > raw.Length)
                {
                    throw new SpineFormatException(Loc.T("{0}: PNG 데이터가 잘렸습니다", name));
                }

                int filter = raw[q];
                Buffer.BlockCopy(raw, q + 1, line, 0, stride);
                q += 1 + stride;
                switch (filter)
                {
                    case 1:
                        for (int i = bpp; i < stride; i++)
                        {
                            line[i] = (byte)(line[i] + line[i - bpp]);
                        }

                        break;
                    case 2:
                        for (int i = 0; i < stride; i++)
                        {
                            line[i] = (byte)(line[i] + prev[i]);
                        }

                        break;
                    case 3:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? line[i - bpp] : 0;
                            line[i] = (byte)(line[i] + (a + prev[i]) / 2);
                        }

                        break;
                    case 4:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? line[i - bpp] : 0;
                            int b = prev[i];
                            int c = i >= bpp ? prev[i - bpp] : 0;
                            int pa = Math.Abs(b - c);
                            int pb = Math.Abs(a - c);
                            int pc = Math.Abs(a + b - 2 * c);
                            int pr = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            line[i] = (byte)(line[i] + pr);
                        }

                        break;
                }

                int o = y * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    if (colorType == 3)
                    {
                        int idx = depth == 8
                            ? line[x]
                            : (line[x * depth / 8] >> (8 - depth - (x * depth) % 8)) & ((1 << depth) - 1);
                        if (palette == null || idx * 3 + 2 >= palette.Length)
                        {
                            throw new SpineFormatException(Loc.T("{0}: PNG 팔레트 범위를 벗어났습니다", name));
                        }

                        output[o] = palette[idx * 3];
                        output[o + 1] = palette[idx * 3 + 1];
                        output[o + 2] = palette[idx * 3 + 2];
                        output[o + 3] = transparency != null && idx < transparency.Length ? transparency[idx] : (byte)255;
                        continue;
                    }

                    int s = x * channels * step;
                    switch (colorType)
                    {
                        case 0:
                            output[o] = output[o + 1] = output[o + 2] = line[s];
                            output[o + 3] = 255;
                            break;
                        case 2:
                            output[o] = line[s];
                            output[o + 1] = line[s + step];
                            output[o + 2] = line[s + 2 * step];
                            output[o + 3] = 255;
                            break;
                        case 4:
                            output[o] = output[o + 1] = output[o + 2] = line[s];
                            output[o + 3] = line[s + step];
                            break;
                        default:
                            output[o] = line[s];
                            output[o + 1] = line[s + step];
                            output[o + 2] = line[s + 2 * step];
                            output[o + 3] = line[s + 3 * step];
                            break;
                    }
                }

                byte[] t = prev;
                prev = line;
                line = t;
            }

            return output;
        }

        /// <summary>RGBA(위쪽 줄부터, 픽셀당 4바이트) → 8비트 RGBA PNG 파일 내용. 줄마다 필터 0.</summary>
        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            var rows = new byte[height * (width * 4 + 1)];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * width * 4, rows, y * (width * 4 + 1) + 1, width * 4);
            }

            using (var output = new MemoryStream())
            {
                output.Write(Signature, 0, Signature.Length);
                var ihdr = new byte[13];
                WriteInt(ihdr, 0, width);
                WriteInt(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(output, "IHDR", ihdr);
                WriteChunk(output, "IDAT", ZlibCompress(rows));
                WriteChunk(output, "IEND", Array.Empty<byte>());
                return output.ToArray();
            }
        }

        static byte[] ZlibCompress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                var adler = new byte[4];
                WriteInt(adler, 0, Adler32(data));
                output.Write(adler, 0, 4);
                return output.ToArray();
            }
        }

        static int Adler32(byte[] data)
        {
            uint a = 1;
            uint b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }

            return (int)((b << 16) | a);
        }

        static void WriteChunk(Stream output, string kind, byte[] body)
        {
            var head = new byte[8];
            WriteInt(head, 0, body.Length);
            for (int i = 0; i < 4; i++)
            {
                head[4 + i] = (byte)kind[i];
            }

            uint crc = Crc32(head, 4, 4, 0xFFFFFFFFu);
            crc = Crc32(body, 0, body.Length, crc) ^ 0xFFFFFFFFu;
            output.Write(head, 0, 8);
            output.Write(body, 0, body.Length);
            var tail = new byte[4];
            WriteInt(tail, 0, (int)crc);
            output.Write(tail, 0, 4);
        }

        static void WriteInt(byte[] d, int at, int v)
        {
            d[at] = (byte)(v >> 24);
            d[at + 1] = (byte)(v >> 16);
            d[at + 2] = (byte)(v >> 8);
            d[at + 3] = (byte)v;
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        static uint Crc32(byte[] data, int offset, int count, uint crc)
        {
            for (int i = 0; i < count; i++)
            {
                crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }
}
