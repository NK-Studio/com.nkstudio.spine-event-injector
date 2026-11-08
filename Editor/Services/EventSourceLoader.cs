using System;
using System.Collections.Generic;
using System.IO;
using NKStudio.SpineEventInjector.SpineVersionConverter;
using NKStudio.SpineEventInjector.Project;
using NKStudio.SpineEventInjector.Skeleton;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace NKStudio.SpineEventInjector
{
    /// <summary>에셋·파일 경로에서 <see cref="EventSource"/> 를 만든다.</summary>
    static class EventSourceLoader
    {
        public static bool IsSpineProjectPath(string path) => path.EndsWith(".spine", StringComparison.OrdinalIgnoreCase);

        /// <summary>창이 다룰 수 있는 에셋인가(SkeletonDataAsset, 스켈레톤 .skel.bytes/.json TextAsset).</summary>
        public static bool CanOpen(UnityEngine.Object asset)
        {
            if (asset is SkeletonDataAsset data)
            {
                return data.skeletonJSON != null;
            }

            if (asset is TextAsset text)
            {
                string path = AssetDatabase.GetAssetPath(text);
                return IsSkeletonDataPath(path) && LooksLikeSkeletonData(text.bytes);
            }

            string assetPath = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            return assetPath != null && IsSpineProjectPath(assetPath);
        }

        public static bool IsSkeletonDataPath(string path) =>
            path.EndsWith(".skel.bytes", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        static bool LooksLikeSkeletonData(byte[] bytes)
        {
            try
            {
                return SpineVersionInfo.ReadVersionString(bytes) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static EventSource FromAsset(UnityEngine.Object asset)
        {
            if (asset is TextAsset data && FindSkeletonDataAsset(data) is SkeletonDataAsset owner)
            {
                asset = owner; // 데이터 파일 대신 그것을 쓰는 SkeletonDataAsset 으로 연다(미리보기 포함)
            }

            SkeletonDataAsset preview = asset as SkeletonDataAsset;
            UnityEngine.Object file = preview != null ? preview.skeletonJSON : asset;
            string assetPath = AssetDatabase.GetAssetPath(file);
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new SpineEventUsageException(Loc.T("프로젝트 에셋이 아닙니다"));
            }

            EventSource source = FromPath(Path.GetFullPath(assetPath));
            source.AssetPath = assetPath;
            source.Preview = preview != null ? preview : FindSkeletonDataAsset(file as TextAsset);
            return source;
        }

        public static EventSource FromPath(string fullPath)
        {
            if (File.Exists(fullPath) == false)
            {
                throw new SpineEventUsageException(Loc.T("파일이 없습니다: {0}", fullPath));
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            var source = new EventSource
            {
                FilePath = fullPath,
                DisplayName = Path.GetFileName(fullPath),
                Bytes = bytes,
                AssetPath = ToAssetPath(fullPath),
            };
            if (IsSpineProjectPath(fullPath))
            {
                var codec = new SpineProjectCodec(SchemaLibrary.All);
                SpineProjectFile project = codec.Load(bytes);
                source.Kind = EventSourceKind.SpineProject;
                source.Codec = codec;
                source.Version = project.FormatVersion;
                List<string> skeletons = SpineProjectCodec.SkeletonNames(project);
                source.Skeletons = skeletons.Count > 1 ? skeletons : (IReadOnlyList<string>)Array.Empty<string>();
                source.Original = codec.Read(project);
            }
            else
            {
                SkeletonDataCodec codec = SkeletonDataCodec.For(bytes);
                source.Kind = codec.Kind;
                source.Codec = codec;
                source.Version = SpineVersionInfo.ReadVersionString(bytes);
                source.Original = codec.Read(bytes);
            }

            source.Working = source.Original.Clone();
            return source;
        }

        /// <summary>프로젝트 안 경로면 Assets/… 에셋 경로, 아니면 null.</summary>
        public static string ToAssetPath(string fullPath)
        {
            string root = PluginPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string path = Path.GetFullPath(fullPath).Replace('\\', '/');
            if (path.StartsWith(root + "Assets/", StringComparison.Ordinal))
            {
                return path.Substring(root.Length);
            }

            return null;
        }

        /// <summary>이 스켈레톤 데이터 파일을 쓰는 SkeletonDataAsset(미리보기용). 없으면 null.</summary>
        public static SkeletonDataAsset FindSkeletonDataAsset(TextAsset data)
        {
            if (data == null)
            {
                return null;
            }

            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(data));
            foreach (string[] folders in new[] { new[] { dir }, null })
            {
                string[] guids = folders == null
                    ? AssetDatabase.FindAssets("t:SkeletonDataAsset")
                    : AssetDatabase.FindAssets("t:SkeletonDataAsset", folders);
                foreach (string guid in guids)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(AssetDatabase.GUIDToAssetPath(guid));
                    if (asset != null && asset.skeletonJSON == data)
                    {
                        return asset;
                    }
                }
            }

            return null;
        }
    }
}
