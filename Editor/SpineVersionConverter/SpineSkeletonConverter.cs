using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    public sealed class SpineConversionOptions
    {
        /// <summary>출력 Spine 버전(x.y.z 전체, 예: 4.3.75-beta). null 이면 입력 버전을 유지한다.</summary>
        public string TargetVersion;

        /// <summary>출력 형식. null 이면 입력 형식을 유지한다.</summary>
        public SpineFileFormat? OutputFormat;
    }

    public sealed class SpineConversionResult
    {
        public byte[] Output;
        public string SourceVersion;
        public string OutputVersion;
        public SpineFileFormat SourceFormat;
        public SpineFileFormat OutputFormat;
        public List<string> Messages = new List<string>();
    }

    /// <summary>
    /// 메모리 안에서 Spine 스켈레톤 데이터를 버전·형식 변환한다(원본 SkeletonConverter.cpp). 파일·Unity 에 의존하지 않는다.
    /// 흐름: 입력 버전 판별 → 공통 모델로 읽기 → (4.3→하위 면 호환 검사) → 버전 문자열 교체 → 3.x⇄4.x 변환 패스 → 출력 버전으로 쓰기.
    /// </summary>
    public static class SpineSkeletonConverter
    {
        public static SpineConversionResult Convert(byte[] input, SpineConversionOptions options)
        {
            var result = new SpineConversionResult { SourceFormat = SpineVersionInfo.DetectFormat(input) };
            result.SourceVersion = SpineVersionInfo.ReadVersionString(input)
                ?? throw new SpineConversionException("입력에서 Spine 버전을 읽을 수 없다(Spine .skel/.json 이 아니거나 깨졌다).");
            SpineVersion sourceVersion = SpineVersionInfo.Parse(result.SourceVersion);
            SpineVersion outputVersion = options.TargetVersion != null ? SpineVersionInfo.Parse(options.TargetVersion) : sourceVersion;
            result.OutputFormat = options.OutputFormat ?? result.SourceFormat;

            SpineSkeleton skeleton = Read(input, result.SourceFormat, sourceVersion);
            if (sourceVersion == SpineVersion.Version43 && outputVersion != SpineVersion.Version43)
            {
                Spine43Json.RequireLegacyCompatible(skeleton);
                result.Messages.Add("4.3 통합 제약조건·메시 소스를 4.2 배치로 바꾼다.");
            }

            if (options.TargetVersion != null)
            {
                skeleton.Version = options.TargetVersion;
            }

            ApplyCrossVersionPasses(skeleton, sourceVersion, outputVersion, result.Messages.Add);

            result.OutputVersion = skeleton.Version;
            result.Output = Write(skeleton, result.OutputFormat, outputVersion, result.Messages.Add);
            return result;
        }

        public static SpineSkeleton Read(byte[] input, SpineFileFormat format, SpineVersion version)
        {
            if (format == SpineFileFormat.Skel)
            {
                return version == SpineVersion.Version38 ? Spine38BinaryReader.Read(input) : SpineSkeletonBinaryReader.Read(input, version);
            }

            JObject root = ParseJson(input);
            switch (version)
            {
                case SpineVersion.Version38:
                    return Spine38JsonReader.Read(root);
                case SpineVersion.Version43:
                    return Spine43Json.Read(root);
                default:
                    return SpineSkeletonJsonReader.Read(root);
            }
        }

        /// <summary>모델을 쓴다. 바이너리 쓰기는 모델을 정리(기본 스킨·문자열 테이블 등)하므로, 같은 모델을 다시 쓸 일이 있으면 새로 읽는다.</summary>
        public static byte[] Write(SpineSkeleton skeleton, SpineFileFormat format, SpineVersion version, Action<string> log = null)
        {
            if (format == SpineFileFormat.Skel)
            {
                return version == SpineVersion.Version38 ? Spine38BinaryWriter.Write(skeleton, log) : SpineSkeletonBinaryWriter.Write(skeleton, version, log);
            }

            JObject root;
            switch (version)
            {
                case SpineVersion.Version38:
                    root = Spine38JsonWriter.Write(skeleton);
                    break;
                case SpineVersion.Version43:
                    root = Spine43Json.Write(skeleton);
                    break;
                default:
                    root = SpineSkeletonJsonWriter.Write(skeleton);
                    break;
            }

            return Encoding.UTF8.GetBytes(SpineJson.Dump(root));
        }

        /// <summary>
        /// 3.x 와 4.x 는 곡선·회전 타임라인 표현이 다르다(원본 SkeletonConverter.cpp 의 버전 경계 패스). 같은 계열 안에서는 아무것도 하지 않는다.
        /// 4.0·4.1 은 지원하지 않으므로 «4.0 이상»은 4.2·4.3, «4.1 이하»는 3.8 이다.
        /// </summary>
        public static void ApplyCrossVersionPasses(SpineSkeleton skeleton, SpineVersion sourceVersion, SpineVersion outputVersion, Action<string> log = null)
        {
            log = log ?? (_ => { });
            bool isSource3x = sourceVersion == SpineVersion.Version38;
            bool isOutput3x = outputVersion == SpineVersion.Version38;
            if (isSource3x == false && isOutput3x == true)
            {
                log("4.x proportional 경로 간격을 3.x 의 length 로 바꾼다.");
                Spine3xConversions.ConvertSpacingMode4xTo3x(skeleton);
                log("4.x 회전 타임라인을 3.x 최단 경로 키로 바꾼다.");
                Spine3xConversions.ConvertRotateTimeline4xTo3x(skeleton);
                log("4.x 절대 곡선을 3.x 상대 곡선으로 바꾼다.");
                Spine3xConversions.ConvertCurve4xTo3x(skeleton);

                // 4.2 이상 → 4.1 이하: 제약조건 order 를 빈틈 없는 0..n-1 로 다시 매긴다.
                log("4.2 제약조건 순서를 하위 버전 배치로 정리한다.");
                Spine3xConversions.ConvertOrder42ToBelow(skeleton);
            }
            else if (isSource3x == true && isOutput3x == false)
            {
                log("3.x 회전 타임라인을 4.x 절대 각도로 펼친다.");
                Spine3xConversions.ConvertRotateTimeline3xTo4x(skeleton);
                log("3.x 상대 곡선을 4.x 절대 곡선으로 바꾼다.");
                Spine3xConversions.ConvertCurve3xTo4x(skeleton);
            }
        }

        private static JObject ParseJson(byte[] input)
        {
            try
            {
                // 큰 정수 해시 등에서 정밀도를 잃지 않게 실수는 double 로 받는다.
                string text = Encoding.UTF8.GetString(input).TrimStart('﻿');
                using (var reader = new JsonTextReader(new System.IO.StringReader(text)) { FloatParseHandling = FloatParseHandling.Double })
                {
                    return JObject.Load(reader);
                }
            }
            catch (JsonException exception)
            {
                throw new SpineConversionException($"Spine JSON 을 해석할 수 없다: {exception.Message}", exception);
            }
        }
    }
}
