using System;
using System.Text.RegularExpressions;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    // 원본 툴은 3.8~4.3 을 다루지만, 이 에셋은 프로젝트가 실제로 오가는 3.8·4.2·4.3 만 옮겼다(4.0·4.1 은 없다).
    // 다른 버전을 붙일 때는 리더/라이터를 추가하고 여기 값과 Parse 를 늘린다(값 순서 = 버전 순서 — 변환 패스가 대소 비교한다).
    public enum SpineVersion
    {
        Version38,
        Version42,
        Version43,
    }

    public enum SpineFileFormat
    {
        Json,
        Skel,
    }

    public sealed class SpineConversionException : Exception
    {
        public SpineConversionException(string message) : base(message)
        {
        }

        public SpineConversionException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public static class SpineVersionInfo
    {
        // 원본 규칙: 반드시 x.y.z 전체를 받는다. "-beta" 접미사는 4.3 만 허용한다.
        private static readonly Regex VersionPattern = new Regex(@"^(\d+)\.(\d+)\.(\d+)(-beta)?$");

        public static bool TryParse(string versionString, out SpineVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(versionString) == true)
            {
                return false;
            }

            Match match = VersionPattern.Match(versionString);
            if (match.Success == false)
            {
                return false;
            }

            string majorMinor = match.Groups[1].Value + "." + match.Groups[2].Value;
            bool isBeta = match.Groups[4].Success;
            switch (majorMinor)
            {
                case "3.8":
                    version = SpineVersion.Version38;
                    return isBeta == false;
                case "4.2":
                    version = SpineVersion.Version42;
                    return isBeta == false;
                case "4.3":
                    version = SpineVersion.Version43;
                    return true;
                default:
                    return false;
            }
        }

        public static SpineVersion Parse(string versionString)
        {
            if (TryParse(versionString, out SpineVersion version) == false)
            {
                throw new SpineConversionException(
                    $"지원하지 않는 Spine 버전 문자열이다: '{versionString}'. 3.8.x / 4.2.x / 4.3.x 를 x.y.z 전체로 적는다(예: 3.8.99, 4.2.43, 4.3.75-beta).");
            }

            return version;
        }

        public static string ToMajorMinor(SpineVersion version)
        {
            switch (version)
            {
                case SpineVersion.Version38:
                    return "3.8";
                case SpineVersion.Version42:
                    return "4.2";
                default:
                    return "4.3";
            }
        }

        /// <summary>
        /// 내용으로 형식을 가른다. UTF-8 BOM·공백을 건너뛴 첫 글자가 '{' 이고 그다음 의미 있는 글자가 '"' 나 '}' 면 JSON, 아니면 바이너리.
        /// 4.x 바이너리는 해시 8바이트로 시작해 첫 바이트가 우연히 0x7B('{')일 수 있다(256개 중 1개꼴 — 실측: anim_ui_lock 을
        /// 4.2 로 올린 결과). 첫 글자만 보면 그런 파일을 JSON 으로 오판해 버전도 못 읽는다(원본 툴도 같은 결함).
        /// </summary>
        public static SpineFileFormat DetectFormat(byte[] data)
        {
            bool hasBom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
            int first = SkipJsonWhitespace(data, hasBom == true ? 3 : 0);
            if (first >= data.Length || data[first] != '{')
            {
                return SpineFileFormat.Skel;
            }

            int second = SkipJsonWhitespace(data, first + 1);
            return second < data.Length && (data[second] == '"' || data[second] == '}') ? SpineFileFormat.Json : SpineFileFormat.Skel;
        }

        private static int SkipJsonWhitespace(byte[] data, int index)
        {
            while (index < data.Length && (data[index] == ' ' || data[index] == '\n' || data[index] == '\r' || data[index] == '\t'))
            {
                index++;
            }

            return index;
        }

        /// <summary>
        /// 파일에 적힌 Spine 버전 문자열을 읽는다(지원 여부와 무관). 못 읽으면 null.
        /// 바이너리는 4.x 해시(8바이트) 뒤, 3.8 은 해시 문자열 뒤에 버전이 있다 — 둘 다 본다.
        /// </summary>
        public static string ReadVersionString(byte[] data)
        {
            if (DetectFormat(data) == SpineFileFormat.Json)
            {
                try
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿'));
                    return (string)root["skeleton"]?["spine"];
                }
                catch (Exception)
                {
                    return null;
                }
            }

            foreach (bool isLegacyLayout in new[] { false, true })
            {
                try
                {
                    var input = new SpineBinaryInput(data);
                    if (isLegacyLayout == true)
                    {
                        input.ReadString();
                    }
                    else
                    {
                        input.Skip(8);
                    }

                    string version = input.ReadString();
                    if (version != null && VersionPattern.IsMatch(version) == true)
                    {
                        return version;
                    }
                }
                catch (SpineConversionException)
                {
                    // 이 배치가 아니다 — 다음 배치를 시도한다.
                }
            }

            return null;
        }
    }
}
