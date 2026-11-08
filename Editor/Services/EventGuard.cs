using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NKStudio.SpineEventInjector.Skeleton;
using UnityEditor;
using UnityEngine;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 재익스포트 안전망. 이 도구로 이벤트를 쓴 스켈레톤 데이터는 마지막으로 쓴 이벤트를 Library 에 기억해 둔다(git 밖).
    /// 그 파일이 다시 임포트됐는데(아티스트 재익스포트 등) 기억한 이벤트가 빠져 있으면 «되살릴까요?»라고 묻는다.
    /// 이 도구가 쓴 적 없는 파일은 보지 않는다.
    /// </summary>
    sealed class EventGuard : AssetPostprocessor
    {
        static readonly HashSet<string> Suppressed = new HashSet<string>();

        /// <summary>스켈레톤 데이터 파일이 임포트될 때마다(이 도구가 쓴 경우 포함) 에셋 경로로 알린다. 열린 창이 다시 읽는 데 쓴다.</summary>
        public static event Action<string> Imported;

        static string CacheDir => Path.Combine(PluginPaths.LibraryDir, "Guard");

        static string CachePath(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? null : Path.Combine(CacheDir, guid + ".json");
        }

        /// <summary>이 도구가 방금 쓴 이벤트를 기억한다(.spine 처럼 프로젝트 밖 파일은 대상이 아니다).</summary>
        public static void Remember(string assetPath, EventSet set)
        {
            if (assetPath == null || EventSourceLoader.IsSkeletonDataPath(assetPath) == false)
            {
                return;
            }

            string path = CachePath(assetPath);
            if (path == null)
            {
                return;
            }

            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(path, JsonConvert.SerializeObject(set));
        }

        public static EventSet Recall(string assetPath)
        {
            string path = assetPath == null ? null : CachePath(assetPath);
            return path != null && File.Exists(path) ? JsonConvert.DeserializeObject<EventSet>(File.ReadAllText(path)) : null;
        }

        /// <summary>이 도구가 쓰고 곧바로 임포트하는 한 번은 검사하지 않는다.</summary>
        public static void Suppress(string assetPath) => Suppressed.Add(assetPath);

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string assetPath in imported)
            {
                if (EventSourceLoader.IsSkeletonDataPath(assetPath) == false)
                {
                    continue;
                }

                string notify = assetPath;
                EditorApplication.delayCall += () => Imported?.Invoke(notify);
                if (Suppressed.Remove(assetPath))
                {
                    continue;
                }

                EventSet remembered;
                try
                {
                    remembered = Recall(assetPath);
                }
                catch (Exception)
                {
                    continue;
                }

                if (remembered == null)
                {
                    continue;
                }

                string target = assetPath;
                EditorApplication.delayCall += () => Check(target, remembered);
            }
        }

        /// <summary>기억한 이벤트 중 지금 파일에 없는 것(정의 이름 기준·키는 애니메이션·이름·시각·값 기준).</summary>
        public static (List<EventDefinition> Defs, List<EventKey> Keys) Missing(EventSet remembered, EventSet current)
        {
            List<EventDefinition> defs = remembered.Definitions.Where(d => current.Definition(d.Name) == null).ToList();
            List<EventKey> keys = remembered.Keys.Where(k => current.Keys.Any(c =>
                c.Animation == k.Animation && c.Name == k.Name && Math.Abs(c.Seconds - k.Seconds) < 1e-3 && c.SameValues(k)) == false).ToList();
            return (defs, keys);
        }

        static void Check(string assetPath, EventSet remembered)
        {
            EventSource source;
            try
            {
                source = EventSourceLoader.FromPath(Path.GetFullPath(assetPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Loc.T("[Spine Event Injector] {0} 이벤트를 읽지 못해 재익스포트 검사를 건너뜁니다: {1}", assetPath, e.Message));
                return;
            }

            (List<EventDefinition> defs, List<EventKey> keys) = Missing(remembered, source.Original);
            if (keys.Count == 0 && defs.Count == 0)
            {
                Remember(assetPath, source.Original);
                return;
            }

            string list = string.Join("\n", keys.Take(12).Select(k => Loc.T("  {0} {1:0.###}초 {2}", k.Animation, k.Seconds, k.Name))) +
                          (keys.Count > 12 ? Loc.T("\n  … 외 {0}개", keys.Count - 12) : "");
            string message = Loc.T("{0}가 다시 임포트되면서 이 도구로 넣었던 이벤트 키 {1}개", Path.GetFileName(assetPath), keys.Count) +
                             (defs.Count > 0 ? Loc.T("(정의 {0}개 포함)", defs.Count) : "") + Loc.T("가 사라졌습니다.\n") +
                             Loc.T("재익스포트 전에 Spine 원본에 반영되지 않았던 이벤트일 수 있습니다.\n\n") + list;
            if (Application.isBatchMode)
            {
                Debug.LogWarning("[Spine Event Injector] " + message);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(Loc.T("이벤트가 사라졌습니다"), message, Loc.T("되살리기"), Loc.T("지금 상태 받아들이기"), Loc.T("나중에"));
            if (choice == 0)
            {
                Restore(source, defs, keys);
            }
            else if (choice == 1)
            {
                Remember(assetPath, source.Original);
            }
        }

        static void Restore(EventSource source, List<EventDefinition> defs, List<EventKey> keys)
        {
            source.Edit(set =>
            {
                foreach (EventDefinition d in defs)
                {
                    EventDefinition copy = d.Clone();
                    copy.Id = -1;
                    set.Definitions.Add(copy);
                }

                foreach (EventKey k in keys.Where(k => set.Animations.Contains(k.Animation)))
                {
                    EventKey copy = k.Clone();
                    copy.Id = -1;
                    set.Keys.Add(copy);
                }
            });
            var log = new List<string>();
            try
            {
                EventFileWriter.Apply(source, log);
                Debug.Log(Loc.T("[Spine Event Injector] {0}: 사라진 이벤트를 되살렸습니다. {1}", source.DisplayName, string.Join(" / ", log)));
            }
            catch (Exception e)
            {
                Debug.LogError(Loc.T("[Spine Event Injector] {0}: 되살리지 못했습니다 — {1}", source.DisplayName, e.Message));
            }
        }
    }
}
