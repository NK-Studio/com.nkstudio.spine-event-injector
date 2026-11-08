using System;
using System.IO;
using System.IO.Compression;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// .spine 컨테이너 = zlib 헤더 없는 raw deflate 스트림 하나.
    /// 4.3.17부터 뒤에 20바이트 꼬리가 붙는다: 00 00 00 00 | 압축 길이(BE 4) | 8바이트 | 02 0b 02 0b.
    /// 꼬리의 8바이트는 Spine이 읽을 때 검사하지 않는다(실측). 쓸 때는 압축 길이만 고쳐 그대로 붙인다.
    /// </summary>
    public static class SpineProjectContainer
    {
        public const int TrailerLength = 20;

        public static byte[] Decompress(byte[] file, out byte[] trailer)
        {
            trailer = null;
            int deflateLength = file.Length;
            if (LooksLikeTrailer(file))
            {
                trailer = new byte[TrailerLength];
                Buffer.BlockCopy(file, file.Length - TrailerLength, trailer, 0, TrailerLength);
                deflateLength = file.Length - TrailerLength;
            }

            try
            {
                using (var input = new MemoryStream(file, 0, deflateLength))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    inflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch (InvalidDataException e)
            {
                throw new SpineFormatException(Loc.T("raw deflate 스트림이 아닙니다: {0}", e.Message));
            }
        }

        public static byte[] Compress(byte[] body, byte[] trailer)
        {
            byte[] deflated;
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                {
                    deflate.Write(body, 0, body.Length);
                }

                deflated = output.ToArray();
            }

            if (trailer == null)
            {
                return deflated;
            }

            var result = new byte[deflated.Length + TrailerLength];
            Buffer.BlockCopy(deflated, 0, result, 0, deflated.Length);
            Buffer.BlockCopy(trailer, 0, result, deflated.Length, TrailerLength);
            int at = deflated.Length + 4;
            result[at] = (byte)(deflated.Length >> 24);
            result[at + 1] = (byte)(deflated.Length >> 16);
            result[at + 2] = (byte)(deflated.Length >> 8);
            result[at + 3] = (byte)deflated.Length;
            return result;
        }

        static bool LooksLikeTrailer(byte[] file)
        {
            if (file.Length <= TrailerLength)
            {
                return false;
            }

            int t = file.Length - TrailerLength;
            if (file[t] != 0 || file[t + 1] != 0 || file[t + 2] != 0 || file[t + 3] != 0)
            {
                return false;
            }

            int length = (file[t + 4] << 24) | (file[t + 5] << 16) | (file[t + 6] << 8) | file[t + 7];
            return length == t && file[t + 16] == 0x02 && file[t + 17] == 0x0b && file[t + 18] == 0x02 && file[t + 19] == 0x0b;
        }
    }
}
