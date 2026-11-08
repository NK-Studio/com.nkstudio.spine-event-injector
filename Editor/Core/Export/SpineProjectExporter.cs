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
    }

    /// <summary>
    /// Spine 없이 .spine 프로젝트를 런타임용 JSON + atlas 로 내보낸다.
    /// 파이썬 PoC(export_poc/spine_export.py)에서 프리셋을 쓰지 않을 때와 같은 설정이다:
    /// 비필수 데이터 포함, PMA 켬, 블리드·에일리어스 끔, 페이지 최대 4096.
    /// 정적 상태가 없어 백그라운드 스레드에서 호출해도 된다.
    /// </summary>
    public static class SpineProjectExporter
    {
        public static List<SpineExportResult> Export(SpineProjectFile file, string spinePath, string targetVersion, string imagesDirOverride = null)
        {
            CheckVersion(targetVersion);
            List<SkeletonModel> models;
            var results = new List<SpineExportResult>();
            try
            {
                models = SkeletonModelReader.ReadAll(new ProjectView(file));
                foreach (SkeletonModel model in models)
                {
                    results.Add(new SpineExportResult
                    {
                        SkeletonName = model.Name,
                        Json = SkeletonJsonWriter.Write(model, targetVersion, true),
                    });
                }
            }
            catch (Exception e) when (IsBrokenGraph(e))
            {
                throw new SpineFormatException(Loc.T("프로젝트 구조를 해석하지 못했습니다: {0}", e.Message));
            }

            for (int i = 0; i < models.Count; i++)
            {
                FillAtlas(results[i], models[i], spinePath, imagesDirOverride);
            }

            return results;
        }

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
