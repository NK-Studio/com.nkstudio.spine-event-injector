using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NKStudio.SpineEventInjector.Project;
using NKStudio.SpineEventInjector.Skeleton;
using UnityEditor;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// «적용하기»: 작업본을 파일에 쓴다.
    /// 순서: 바깥에서 바뀌었는지 확인 → (.spine 이면 작업 중인 사람이 없는지 확인 창) → 백업 → 임시 파일에 쓰고 교체
    /// → 다시 읽어 작업본과 같은지 확인(다르면 백업으로 되돌림) → 에셋이면 다시 임포트.
    /// </summary>
    static class EventFileWriter
    {
        public static string SpineBusyMessage => Loc.T("이 .spine으로 작업 중인 사람이 있으면 넣은 이벤트가 사라집니다.");

        /// <summary>작업본을 대상 파일에 쓴다. 취소하면 false.</summary>
        /// <param name="confirmed">창 안 확인 창에서 이미 확인받았으면 true.</param>
        public static bool Apply(EventSource source, List<string> log, bool confirmed = false)
        {
            if (source.IsDirty == false)
            {
                return true;
            }

            if (source.IsSpineProject && confirmed == false && Confirm(source.FilePath) == false)
            {
                return false;
            }

            byte[] onDisk = File.ReadAllBytes(source.FilePath);
            if (onDisk.AsSpan().SequenceEqual(source.Bytes) == false)
            {
                throw new SpineEventUsageException(Loc.T("불러온 뒤에 파일이 바깥에서 바뀌었습니다. 다시 불러온 뒤 적용하세요"));
            }

            byte[] result = source.Codec.Write(source.Bytes, source.Original, source.Working, log);
            WriteVerified(source.FilePath, source.AssetPath, result, bytes => Verify(source, bytes));
            EventGuard.Remember(source.AssetPath, source.Working);
            source.Reload(result);
            return true;
        }

        public static bool Confirm(string spinePath) =>
            EditorUtility.DisplayDialog(Loc.T("Spine 원본에 쓰기"), $"{Path.GetFileName(spinePath)}\n\n{SpineBusyMessage}", Loc.T("적용하기"), Loc.T("취소"));

        /// <summary>백업 → 임시 파일 → 교체 → 확인(실패하면 백업으로 되돌림) → 임포트.</summary>
        public static void WriteVerified(string path, string assetPath, byte[] bytes, Action<byte[]> verify)
        {
            string backup = Backup(path, assetPath);
            string tmp = path + ".sei-tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }

            try
            {
                verify?.Invoke(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                if (backup != null)
                {
                    File.Copy(backup, path, true);
                }

                throw;
            }

            if (assetPath != null)
            {
                EventGuard.Suppress(assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
        }

        static void Verify(EventSource source, byte[] written)
        {
            EventSet check = source.Codec.Read(written);
            if (source.IsSpineProject)
            {
                foreach (EventKey k in check.Keys)
                {
                    k.Seconds = k.NativeTime / source.Working.Fps;
                }
            }

            string problem = SkeletonDataCodec.Compare(source.Working, check);
            if (problem != null)
            {
                throw new SpineFormatException(Loc.T("쓴 파일을 다시 읽었더니 이벤트가 다릅니다({0}). 백업으로 되돌렸습니다", problem));
            }
        }

        /// <summary>
        /// 백업 위치: 프로젝트 밖 파일(.spine 원본 등)은 옆에 «이름.bak»(처음 한 번만 — 맨 처음 원본을 지킨다),
        /// 프로젝트 안 에셋은 Library 아래(에셋으로 잡히지 않게) 시각을 붙여 남긴다.
        /// </summary>
        static string Backup(string path, string assetPath)
        {
            if (File.Exists(path) == false)
            {
                return null;
            }

            if (assetPath == null)
            {
                string bak = path + ".bak";
                if (File.Exists(bak) == false)
                {
                    File.Copy(path, bak);
                }

                string last = Path.Combine(PluginPaths.LibraryDir, "Backups", Path.GetFileName(path) + "." + Stamp());
                Directory.CreateDirectory(Path.GetDirectoryName(last));
                File.Copy(path, last, true);
                return last;
            }

            string dst = Path.Combine(PluginPaths.LibraryDir, "Backups", assetPath.Replace('/', '_') + "." + Stamp());
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(path, dst, true);
            return dst;
        }

        static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

        public static string Describe(EventSetDiff diff)
        {
            var parts = new List<string>();
            if (diff.AddedDefinitions.Count > 0) parts.Add(Loc.T("정의 추가 {0}", diff.AddedDefinitions.Count));
            if (diff.ChangedDefinitions.Count > 0) parts.Add(Loc.T("정의 수정 {0}", diff.ChangedDefinitions.Count));
            if (diff.RemovedDefinitions.Count > 0) parts.Add(Loc.T("정의 삭제 {0}", diff.RemovedDefinitions.Count));
            if (diff.AddedKeys.Count > 0) parts.Add(Loc.T("키 추가 {0}", diff.AddedKeys.Count));
            if (diff.MovedKeys.Count > 0) parts.Add(Loc.T("키 이동 {0}", diff.MovedKeys.Count));
            if (diff.RetunedKeys.Count > 0) parts.Add(Loc.T("키 값 수정 {0}", diff.RetunedKeys.Count));
            if (diff.RemovedKeys.Count > 0) parts.Add(Loc.T("키 삭제 {0}", diff.RemovedKeys.Count));
            return parts.Count == 0 ? Loc.T("바뀐 것 없음") : string.Join(" · ", parts);
        }

        public static int KeyCount(EventSet set, string animation) => set.Keys.Count(k => k.Animation == animation);
    }
}
