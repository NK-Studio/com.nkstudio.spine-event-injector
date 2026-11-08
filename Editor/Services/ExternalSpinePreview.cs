using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NKStudio.SpineEventInjector.SpineVersionConverter;
using NKStudio.SpineEventInjector.Export;
using NKStudio.SpineEventInjector.Project;
using Newtonsoft.Json.Linq;
using Spine.Unity;
using UnityEngine;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 프로젝트 밖 .spine 의 미리보기: Spine 프로그램 없이 프로젝트를 직접 읽어 «JSON + 아틀라스»를 만들고,
    /// 설치된 spine-unity 런타임이 읽는 버전으로 맞춘 뒤 런타임 SkeletonDataAsset 을 만든다.
    /// 이미지 폴더가 없어 아틀라스를 못 만들면 애니메이션 길이·FPS 만 돌려준다.
    /// </summary>
    static class ExternalSpinePreview
    {
        public sealed class Export
        {
            public string Name;
            public string Json;
            public string AtlasText;
            public string AtlasMessage;
            public List<SpineExportPage> Pages = new List<SpineExportPage>();
            public string Error;
            public float Fps = 30f;
            public readonly Dictionary<string, float> Durations = new Dictionary<string, float>();
        }

        /// <summary>
        /// 내보내기(백그라운드에서 돈다). spineVersion: 파일에 적힌 버전.
        /// 메인 스레드에서 불러야 한다: 에디터 API 가 필요한 스키마 목록은 여기서 미리 구해 넘긴다.
        /// </summary>
        public static Task<Export> ExportAsync(string spinePath, string spineVersion, string skeleton)
        {
            IReadOnlyList<SpineProjectSchema> schemas = SchemaLibrary.All;
            return Task.Run(() => ExportNow(spinePath, spineVersion, skeleton, schemas));
        }

        // 스켈레톤을 바꿀 때마다 프로젝트 전체를 다시 내보내지 않도록 마지막 결과를 들고 있는다(스켈레톤이 여럿일 때만)
        static readonly object CacheLock = new object();
        static string _cacheKey;
        static List<SpineExportResult> _cache;

        static List<SpineExportResult> ExportAll(string spinePath, string spineVersion, IReadOnlyList<SpineProjectSchema> schemas)
        {
            string key = spinePath + "|" + File.GetLastWriteTimeUtc(spinePath).Ticks;
            lock (CacheLock)
            {
                if (_cacheKey == key)
                {
                    return _cache;
                }

                SpineProjectFile file = SpineProjectFile.LoadForExport(File.ReadAllBytes(spinePath), schemas);
                List<SpineExportResult> results = SpineProjectExporter.Export(file, spinePath, TargetVersion(spineVersion));
                _cacheKey = results.Count > 1 ? key : null;
                _cache = results.Count > 1 ? results : null;
                return results;
            }
        }

        /// <summary>백그라운드 스레드: 파일만 다룬다(에디터 API 호출 금지).</summary>
        static Export ExportNow(string spinePath, string spineVersion, string skeleton, IReadOnlyList<SpineProjectSchema> schemas)
        {
            var result = new Export();
            List<SpineExportResult> results = ExportAll(spinePath, spineVersion, schemas);
            SpineExportResult exported = results.FirstOrDefault(r => r.SkeletonName == skeleton) ?? results.FirstOrDefault();
            if (exported == null)
            {
                result.Error = Loc.T("프로젝트에 스켈레톤이 없어 미리보기를 만들 수 없습니다");
                return result;
            }

            result.Name = exported.SkeletonName;
            result.Json = exported.Json;
            result.AtlasText = exported.AtlasText;
            result.AtlasMessage = exported.AtlasMessage;
            result.Pages = exported.Pages;
            JObject root = JObject.Parse(exported.Json);
            result.Fps = (float?)root["skeleton"]?["fps"] ?? 30f;
            if (root["animations"] is JObject animations)
            {
                foreach (JProperty p in animations.Properties())
                {
                    result.Durations[p.Name] = MaxTime(p.Value);
                }
            }

            return result;
        }

        /// <summary>내보낼 JSON 버전. 4.3 프로젝트는 4.3.26, 그 밖은 4.2.43 으로 쓴다.</summary>
        static string TargetVersion(string fileVersion) =>
            fileVersion != null && fileVersion.StartsWith("4.3") ? "4.3.26" : "4.2.43";

        /// <summary>내보낸 파일로 런타임 SkeletonDataAsset 을 만든다(메인 스레드). 아틀라스가 없으면 null.</summary>
        public static SkeletonDataAsset CreateAsset(Export export, out string message)
        {
            message = null;
            if (export.AtlasText == null || export.Pages.Count == 0)
            {
                message = export.AtlasMessage ?? Loc.T("이미지 폴더가 없어 아틀라스를 만들지 못했습니다. 미리보기 없이 이벤트만 편집합니다");
                return null;
            }

            byte[] json = Encoding.UTF8.GetBytes(export.Json);
            string runtime = RuntimeVersion();
            string exported = SpineVersionInfo.ReadVersionString(json);
            if (exported != null && runtime != null && exported.StartsWith(runtime) == false)
            {
                try
                {
                    byte[] source = json;
                    json = SpineSkeletonConverter.Convert(json, new SpineConversionOptions { TargetVersion = runtime + (runtime == "4.2" ? ".43" : ".26") }).Output;
                    json = KeepUnmappedTransformMixesOff(source, json);
                    json = LinkedMeshTimelinesAsBool(json);
                }
                catch (Exception e)
                {
                    message = Loc.T("미리보기용으로 {0} → {1} 변환을 하지 못했습니다: {2}", exported, runtime, e.Message);
                    return null;
                }
            }

            string name = export.Name;
            var atlasText = new TextAsset(export.AtlasText) { name = name + ".atlas", hideFlags = HideFlags.DontSave };
            Texture2D[] textures = export.Pages.Select(CreateTexture).ToArray();
            var material = new Material(Shader.Find("Spine/Skeleton")) { hideFlags = HideFlags.DontSave };
            SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntimeInstance(atlasText, textures, material, true);
            atlas.hideFlags = HideFlags.DontSave;
            var data = new TextAsset(Encoding.UTF8.GetString(json)) { name = name + ".json", hideFlags = HideFlags.DontSave };
            SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntimeInstance(data, atlas, true);
            if (asset != null)
            {
                asset.hideFlags = HideFlags.DontSave;
            }

            return asset;
        }

        /// <summary>RGBA(위에서 아래) → Texture2D(아래에서 위).</summary>
        static Texture2D CreateTexture(SpineExportPage page)
        {
            var flipped = new byte[page.Rgba.Length];
            int stride = page.Width * 4;
            for (int y = 0; y < page.Height; y++)
            {
                Buffer.BlockCopy(page.Rgba, y * stride, flipped, (page.Height - 1 - y) * stride, stride);
            }

            var texture = new Texture2D(page.Width, page.Height, TextureFormat.RGBA32, false)
            {
                name = Path.GetFileNameWithoutExtension(page.Name),
                hideFlags = HideFlags.DontSave,
            };
            texture.LoadRawTextureData(flipped);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// 4.3 변환 보정. 4.3 트랜스폼 제약은 «properties»에 적힌 채널만 움직이지만, 4.2 는 모든 채널을 믹스로 움직이고
        /// 키에 믹스가 없으면 1 로 읽는다. 그래서 4.3 키(대개 mixRotate 하나)를 그대로 옮기면 쓰지 않던 이동·크기·기울임까지 켜져
        /// 포즈가 꼬인다. 제약이 쓰지 않는 채널은 키에 0 을 적어 둔다.
        /// </summary>
        static byte[] KeepUnmappedTransformMixesOff(byte[] source, byte[] converted)
        {
            JObject from = JObject.Parse(Encoding.UTF8.GetString(source));
            var mapped = new Dictionary<string, HashSet<string>>();
            foreach (JObject constraint in (from["constraints"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if ((string)constraint["type"] != "transform" || constraint["name"] == null)
                {
                    continue;
                }

                var channels = new HashSet<string>();
                foreach (JProperty property in (constraint["properties"] as JObject ?? new JObject()).Properties())
                {
                    foreach (JProperty to in (property.Value["to"] as JObject ?? new JObject()).Properties())
                    {
                        channels.Add(to.Name);
                    }
                }

                mapped[(string)constraint["name"]] = channels;
            }

            JObject result = JObject.Parse(Encoding.UTF8.GetString(converted));
            foreach (JProperty animation in (result["animations"] as JObject ?? new JObject()).Properties())
            {
                foreach (JProperty timeline in (animation.Value["transform"] as JObject ?? new JObject()).Properties())
                {
                    if (mapped.TryGetValue(timeline.Name, out HashSet<string> channels) == false)
                    {
                        continue;
                    }

                    foreach (JObject key in (timeline.Value as JArray ?? new JArray()).OfType<JObject>())
                    {
                        foreach ((string channel, string mix) in MixChannels)
                        {
                            if (channels.Contains(channel) == false && key[mix] == null)
                            {
                                key[mix] = 0;
                            }
                        }
                    }
                }
            }

            return Encoding.UTF8.GetBytes(result.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>4.3 내보내기는 연결 메시의 «timelines»를 숫자(0/1)로 쓰지만 4.2 리더는 불리언으로만 읽는다.</summary>
        static byte[] LinkedMeshTimelinesAsBool(byte[] converted)
        {
            JObject result = JObject.Parse(Encoding.UTF8.GetString(converted));
            foreach (JValue value in result.SelectTokens("skins[*].attachments.*.*.timelines").OfType<JValue>().ToList())
            {
                if (value.Type == JTokenType.Integer)
                {
                    value.Value = value.ToObject<long>() != 0;
                }
            }

            return Encoding.UTF8.GetBytes(result.ToString(Newtonsoft.Json.Formatting.None));
        }

        static readonly (string Channel, string Mix)[] MixChannels =
        {
            ("rotate", "mixRotate"), ("x", "mixX"), ("y", "mixY"), ("scaleX", "mixScaleX"), ("scaleY", "mixScaleY"), ("shearY", "mixShearY"),
        };

        /// <summary>설치된 spine-unity 런타임의 major.minor(예: "4.2").</summary>
        static string RuntimeVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SkeletonDataAsset).Assembly);
            string v = info?.version;
            return v == null ? null : string.Join(".", v.Split('.').Take(2));
        }

        static float MaxTime(JToken token)
        {
            float max = 0f;
            foreach (JToken t in token.SelectTokens("..time"))
            {
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                {
                    max = Math.Max(max, (float)t);
                }
            }

            return max;
        }
    }
}
