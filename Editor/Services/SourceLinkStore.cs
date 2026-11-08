using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 스켈레톤 데이터 에셋 ↔ Spine 원본(.spine) 경로 연결과 최근 연 .spine 목록.
    /// 원본 경로는 PC마다 다르므로(구글 드라이브 등) git 에 들어가지 않는 UserSettings/ 에 둔다.
    /// </summary>
    static class SourceLinkStore
    {
        [Serializable]
        sealed class Link
        {
            public string guid;
            public string path;
        }

        [Serializable]
        sealed class Data
        {
            public List<Link> links = new List<Link>();
            public List<string> recent = new List<string>();
        }

        static string FilePath => Path.Combine(PluginPaths.ProjectRoot, "UserSettings", "NKStudio.SpineEventInjector.json");

        static Data Load()
        {
            try
            {
                return File.Exists(FilePath) ? JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) ?? new Data() : new Data();
            }
            catch (Exception)
            {
                return new Data();
            }
        }

        static void Save(Data data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
        }

        public static string GetOrigin(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return Load().links.FirstOrDefault(l => l.guid == guid)?.path;
        }

        public static void SetOrigin(string assetPath, string spinePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            Data data = Load();
            data.links.RemoveAll(l => l.guid == guid);
            if (string.IsNullOrEmpty(spinePath) == false)
            {
                data.links.Add(new Link { guid = guid, path = spinePath });
            }

            Save(data);
        }

        public static IReadOnlyList<string> Recent => Load().recent;

        public static void AddRecent(string path)
        {
            Data data = Load();
            data.recent.Remove(path);
            data.recent.Insert(0, path);
            if (data.recent.Count > 10)
            {
                data.recent.RemoveRange(10, data.recent.Count - 10);
            }

            Save(data);
        }
    }
}
