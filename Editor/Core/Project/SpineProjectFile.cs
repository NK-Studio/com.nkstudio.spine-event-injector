using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// .spine 파일 하나: 압축을 푼 본문, 꼬리, 고른 스키마, 객체 그래프.
    /// 스키마는 «포맷 문자열이 담당 목록(formats)에 있는 것»을 차례로 실제로 읽어
    /// «끝까지 읽힘 + 다시 쓰기 = 원본»인 첫 것을 쓴다(같은 문자열이라도 구조가 다른 빌드가 있다).
    /// 그래프는 깊게 재귀하므로 모든 읽기·쓰기는 큰 스택 스레드에서 돈다(<see cref="BigStack"/>).
    /// </summary>
    public sealed class SpineProjectFile
    {
        public byte[] Body { get; private set; }
        public byte[] Trailer { get; private set; }
        public SpineProjectSchema Schema { get; private set; }
        public SpineProjectDocument Doc { get; private set; }
        public string FormatVersion => Doc.Version;

        public static SpineProjectFile Load(byte[] file, IReadOnlyList<SpineProjectSchema> schemas)
        {
            byte[] body = SpineProjectContainer.Decompress(file, out byte[] trailer);
            string version = SpineProjectDocument.ReadVersion(body);
            if (version != null && int.TryParse(version.Split('.')[0], out int major) && major >= 1 && major <= 3)
            {
                throw new SpineEventUsageException(Loc.T("Spine {0} 프로젝트입니다. Spine 4.0 이후 프로젝트만 지원합니다(3.x 미지원)", version));
            }

            List<SpineProjectSchema> candidates = schemas
                .Where(s => s.Formats.Contains(version))
                .OrderBy(s => s.Name != version)
                .ToList();
            if (candidates.Count == 0)
            {
                string have = string.Join(", ", schemas.SelectMany(s => s.Formats).Distinct().OrderBy(f => f));
                throw new SpineFormatException(Loc.T("지원하지 않는 프로젝트 포맷 버전 '{0}' (지원: {1})", version, have));
            }

            var errors = new List<string>();
            foreach (SpineProjectSchema schema in candidates)
            {
                try
                {
                    SpineProjectDocument doc = BigStack.Run(() => new SpineProjectDocument(body, schema));
                    byte[] again = BigStack.Run(() => SpineProjectWriter.Serialize(doc));
                    if (again.AsSpan().SequenceEqual(body))
                    {
                        return new SpineProjectFile { Body = body, Trailer = trailer, Schema = schema, Doc = doc };
                    }

                    errors.Add(Loc.T("{0}: 다시 쓰기가 원본과 다름", schema.Name));
                }
                catch (SpineFormatException e)
                {
                    errors.Add($"{schema.Name}: {e.Message}");
                }
            }

            throw new SpineFormatException(string.Join("; ", errors));
        }

        /// <summary>
        /// 내보내기 전용 읽기: <see cref="Load"/>가 실패해도 Project 안 뒤쪽 편집기 상태에서 막힌 것뿐이면
        /// 그때까지의 그래프로 파일을 돌려준다(스켈레톤 목록을 끝까지 읽었을 때만). 이 결과는 다시 쓸 수 없다.
        /// </summary>
        public static SpineProjectFile LoadForExport(byte[] file, IReadOnlyList<SpineProjectSchema> schemas)
        {
            try
            {
                return Load(file, schemas);
            }
            catch (SpineFormatException error)
            {
                byte[] body = SpineProjectContainer.Decompress(file, out byte[] trailer);
                string version = SpineProjectDocument.ReadVersion(body);
                foreach (SpineProjectSchema schema in schemas.Where(s => s.Formats.Contains(version)))
                {
                    try
                    {
                        SpineProjectDocument doc = BigStack.Run(() => new SpineProjectDocument(body, schema, true));
                        return new SpineProjectFile { Body = body, Trailer = trailer, Schema = schema, Doc = doc };
                    }
                    catch (SpineFormatException)
                    {
                        // 다음 스키마를 시험한다
                    }
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }
        }

        /// <summary>그래프를 본문으로 다시 쓰고, 그 본문을 다시 읽어 «읽힘 + 다시 쓰기 = 같음»을 확인한다.</summary>
        public void Commit()
        {
            byte[] body = BigStack.Run(() => SpineProjectWriter.Serialize(Doc));
            SpineProjectDocument check = BigStack.Run(() => new SpineProjectDocument(body, Schema));
            byte[] again = BigStack.Run(() => SpineProjectWriter.Serialize(check));
            if (again.AsSpan().SequenceEqual(body) == false)
            {
                throw new SpineFormatException(Loc.T("결과를 다시 읽어 쓴 바이트가 다릅니다"));
            }

            check.ScopeSkeleton = Doc.ScopeSkeleton;
            Body = body;
            Doc = check;
        }

        public byte[] ToFileBytes() => SpineProjectContainer.Compress(Body, Trailer);
    }

    /// <summary>깊은 재귀를 견디는 큰 스택 스레드에서 실행한다(예외는 그대로 다시 던진다).</summary>
    public static class BigStack
    {
        const int StackSize = 256 * 1024 * 1024;

        public static T Run<T>(Func<T> work)
        {
            T result = default;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, StackSize);
            thread.Start();
            thread.Join();
            if (error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }

            return result;
        }

        public static void Run(Action work) => Run<object>(() =>
        {
            work();
            return null;
        });
    }
}
