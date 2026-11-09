using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>atlas 페이지 하나. Rgba 는 위쪽 줄부터, 픽셀당 4바이트(패커가 미리 곱한 알파를 적용한 값).</summary>
    public sealed class SpineExportPage
    {
        public string Name;
        public int Width;
        public int Height;
        public byte[] Rgba;
    }

    /// <summary>스켈레톤 하나의 내보내기 결과.</summary>
    public sealed class SpineExportResult
    {
        public string SkeletonName;
        public string Json;
        /// <summary>atlas 를 만들 수 없으면 null.</summary>
        public string AtlasText;
        public List<SpineExportPage> Pages = new List<SpineExportPage>();
        /// <summary>atlas 가 없는 이유(이미지 폴더 없음 등). atlas 가 있으면 null.</summary>
        public string AtlasMessage;

        /// <summary>Json·atlas 를 아직 만들지 않았으면 false(<see cref="SpineProjectExporter.Complete"/>로 만든다).</summary>
        public bool Ready { get; internal set; }

        internal Func<SkeletonModel> ReadModel;
        internal string TargetVersion;
        internal string SpinePath;
        internal string ImagesDirOverride;
    }

    /// <summary>
    /// Spine 없이 .spine 프로젝트를 런타임용 JSON + atlas 로 내보낸다.
    /// 파이썬 PoC(export_poc/spine_export.py)에서 프리셋을 쓰지 않을 때와 같은 설정이다:
    /// 비필수 데이터 포함, PMA 켬, 블리드·에일리어스 끔, 페이지 최대 4096.
    /// 정적 상태가 없어 백그라운드 스레드에서 호출해도 된다.
    /// </summary>
    public static class SpineProjectExporter
    {
        /// <summary>
        /// lazy: true 면 스켈레톤 이름만 채워 돌려주고, 모델·JSON·atlas 는 <see cref="Complete"/>를 부를 때 만든다
        /// (스켈레톤이 여럿인 프로젝트에서 미리보기는 하나만 쓴다).
        /// </summary>
        public static List<SpineExportResult> Export(SpineProjectFile file, string spinePath, string targetVersion, string imagesDirOverride = null,
            bool lazy = false)
        {
            CheckVersion(targetVersion);
            var results = new List<SpineExportResult>();
            try
            {
                foreach ((string name, Func<SkeletonModel> read) in SkeletonModelReader.Readers(new ProjectView(file)))
                {
                    results.Add(new SpineExportResult
                    {
                        SkeletonName = name,
                        ReadModel = read,
                        TargetVersion = targetVersion,
                        SpinePath = spinePath,
                        ImagesDirOverride = imagesDirOverride,
                    });
                }
            }
            catch (Exception e) when (IsBrokenGraph(e))
            {
                throw BrokenGraph(e);
            }

            if (lazy == false)
            {
                foreach (SpineExportResult result in results)
                {
                    Complete(result);
                }
            }

            return results;
        }

        /// <summary>모델을 읽어 Json·atlas 를 채운다(이미 채웠으면 아무것도 하지 않는다). 같은 결과를 여러 스레드가 부르면 호출하는 쪽에서 잠근다.</summary>
        public static void Complete(SpineExportResult result)
        {
            if (result.Ready)
            {
                return;
            }

            SkeletonModel model;
            try
            {
                model = result.ReadModel();
                result.SkeletonName = model.Name;
                result.Json = SkeletonJsonWriter.Write(model, result.TargetVersion, true);
            }
            catch (Exception e) when (IsBrokenGraph(e))
            {
                throw BrokenGraph(e);
            }

            FillAtlas(result, model, result.SpinePath, result.ImagesDirOverride);
            result.ReadModel = null;
            result.Ready = true;
        }

        static SpineFormatException BrokenGraph(Exception e) =>
            new SpineFormatException(Loc.T("프로젝트 구조를 해석하지 못했습니다: {0}", e.Message));

        static void CheckVersion(string version)
        {
            string[] parts = version?.Split('.');
            bool valid = parts != null && parts.Length == 3 && parts.All(IsDigits);
            if (valid)
            {
                int major = int.Parse(parts[0]);
                int minor = int.Parse(parts[1]);
                valid = major == 4 && minor <= 3;
            }

            if (valid == false)
            {
                throw new SpineEventUsageException(Loc.T("내보낼 버전은 4.0.xx ~ 4.3.xx 형식이어야 합니다: {0}", version));
            }
        }

        static bool IsDigits(string s) => s.Length > 0 && s.Length < 8 && s.All(c => c >= '0' && c <= '9');

        /// <summary>그래프가 예상과 다르면(손상 등) 흔한 런타임 예외로 드러난다. 사용자에게는 형식 오류로 알린다.</summary>
        static bool IsBrokenGraph(Exception e) =>
            e is NullReferenceException || e is KeyNotFoundException || e is InvalidCastException ||
            e is IndexOutOfRangeException || e is ArgumentException || e is InvalidOperationException;

        static void FillAtlas(SpineExportResult result, SkeletonModel model, string spinePath, string imagesDirOverride)
        {
            try
            {
                string images = string.IsNullOrEmpty(imagesDirOverride)
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spinePath)), model.Images)
                    : imagesDirOverride;
                SpineAtlasPacker.Pack(model, images, model.Name, result);
            }
            catch (Exception e) when (e is SpineFormatException || e is IOException || e is UnauthorizedAccessException ||
                                      e is ArgumentException || e is NotSupportedException || e is InvalidDataException ||
                                      e is IndexOutOfRangeException || e is OverflowException)
            {
                result.AtlasText = null;
                result.Pages.Clear();
                result.AtlasMessage = e.Message;
            }
        }
    }
}
