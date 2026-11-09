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

            /// <summary>런타임 버전에 맞춘 JSON(백그라운드에서 미리 바꿔 둔다). 바꾸지 못했으면 null 이고 이유는 RuntimeJsonError.</summary>
            public byte[] RuntimeJson;
            public string RuntimeJsonError;

            /// <summary>텍스처에 그대로 올릴 페이지 픽셀(아래 줄부터). Pages 와 같은 순서.</summary>
            public List<byte[]> TexturePixels = new List<byte[]>();

            /// <summary>TexturePixels 가 straight alpha 인가(Linear 색 공간). false 면 미리 곱한 알파(PMA).</summary>
            public bool StraightAlpha;
            public float Fps = 30f;
            public readonly Dictionary<string, float> Durations = new Dictionary<string, float>();
        }

        /// <summary>
        /// 내보내기(백그라운드에서 돈다). spineVersion: 파일에 적힌 버전.
        /// 메인 스레드에서 불러야 한다: 에디터 API 가 필요한 스키마 목록은 여기서 미리 구해 넘긴다.
        /// </summary>
        /// <param name="loaded">이미 읽은 그래프(창이 열 때 읽은 것). 있으면 파일을 다시 읽지 않는다. 고치지 않는다.</param>
        public static Task<Export> ExportAsync(string spinePath, string spineVersion, string skeleton, SpineProjectFile loaded = null)
        {
            IReadOnlyList<SpineProjectSchema> schemas = SchemaLibrary.All;
            string runtime = RuntimeVersion(); // 에디터 API: 메인 스레드에서 구해 넘긴다
            bool straightAlpha = UseStraightAlpha();
            return Task.Run(() => Prepare(ExportNow(spinePath, spineVersion, skeleton, schemas, loaded), runtime, straightAlpha));
        }

        // 스켈레톤을 바꿀 때마다 프로젝트 전체를 다시 내보내지 않도록 마지막 결과를 들고 있는다(스켈레톤이 여럿일 때만)
        static readonly object CacheLock = new object();
        static string _cacheKey;
        static List<SpineExportResult> _cache;

        // 캐시 키에 쓰는, 읽은 그래프별 번호(같은 그래프면 같은 번호)
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpineProjectFile, object> LoadedIds =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SpineProjectFile, object>();
        static int _nextLoadedId;

        static List<SpineExportResult> ExportAll(string spinePath, string spineVersion, IReadOnlyList<SpineProjectSchema> schemas,
            SpineProjectFile loaded = null)
        {
            string key = loaded != null
                ? spinePath + "|loaded" + LoadedIds.GetValue(loaded, _ => System.Threading.Interlocked.Increment(ref _nextLoadedId))
                : spinePath + "|" + File.GetLastWriteTimeUtc(spinePath).Ticks;
            lock (CacheLock)
            {
                if (_cacheKey == key)
                {
                    return _cache;
                }

                SpineProjectFile file = loaded ?? SpineProjectFile.LoadForExport(File.ReadAllBytes(spinePath), schemas);
                List<SpineExportResult> results = SpineProjectExporter.Export(file, spinePath, TargetVersion(spineVersion), lazy: true);
                _cacheKey = results.Count > 1 ? key : null;
                _cache = results.Count > 1 ? results : null;
                return results;
            }
        }

        /// <summary>백그라운드 스레드: 파일만 다룬다(에디터 API 호출 금지).</summary>
        static Export ExportNow(string spinePath, string spineVersion, string skeleton, IReadOnlyList<SpineProjectSchema> schemas,
            SpineProjectFile loaded = null)
        {
            var result = new Export();
            List<SpineExportResult> results = ExportAll(spinePath, spineVersion, schemas, loaded);
            SpineExportResult exported = results.FirstOrDefault(r => r.SkeletonName == skeleton) ?? results.FirstOrDefault();
            if (exported == null)
            {
                result.Error = Loc.T("프로젝트에 스켈레톤이 없어 미리보기를 만들 수 없습니다");
                return result;
            }

            lock (CacheLock)
            {
                SpineProjectExporter.Complete(exported); // 고른 스켈레톤만. 만든 결과는 캐시에 남아 다시 고를 때 쓴다
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

        /// <summary>
        /// 백그라운드 스레드: 에셋을 만들기 전에 할 수 있는 계산을 미리 한다(런타임 버전으로 JSON 변환, 텍스처용 픽셀 뒤집기).
        /// 이 계산을 메인 스레드에 두면 미리보기가 뜨기 직전에 에디터가 0.2~0.5초 멈춘다.
        /// </summary>
        static Export Prepare(Export export, string runtime, bool straightAlpha)
        {
            if (export.Error != null || export.AtlasText == null || export.Pages.Count == 0)
            {
                return export;
            }

            byte[] json = Encoding.UTF8.GetBytes(export.Json);
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
                    export.RuntimeJsonError = Loc.T("미리보기용으로 {0} → {1} 변환을 하지 못했습니다: {2}", exported, runtime, e.Message);
                    return export;
                }
            }

            export.RuntimeJson = json;
            export.StraightAlpha = straightAlpha;
            foreach (SpineExportPage page in export.Pages)
            {
                byte[] rgba = straightAlpha ? ToStraightAlpha(page.Rgba, page.Width, page.Height) : page.Rgba;
                export.TexturePixels.Add(Flip(rgba, page.Width, page.Height));
            }

            return export;
        }

        /// <summary>
        /// Linear 색 공간이면 straight alpha 로 그린다. sRGB 텍스처를 linear 로 풀 때 미리 곱한 값까지 풀려 반투명 가장자리가
        /// 어둡게 나오기 때문이다(spine-unity 도 Linear 에서 PMA 텍스처를 지원하지 않는다). 메인 스레드에서 부른다.
        /// </summary>
        static bool UseStraightAlpha() => QualitySettings.activeColorSpace == ColorSpace.Linear;

        /// <summary>RGBA(위에서 아래) → 텍스처 순서(아래에서 위).</summary>
        static byte[] Flip(byte[] rgba, int width, int height)
        {
            var flipped = new byte[rgba.Length];
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * stride, flipped, (height - 1 - y) * stride, stride);
            }

            return flipped;
        }

        /// <summary>
        /// 미리 곱한 알파(PMA) → straight alpha. 셰이더가 다시 알파를 곱하므로 나눗셈 오차는 화면에서 1단계 안쪽이다.
        /// 완전히 투명한 픽셀에는 이웃 색을 채운다(bleed). 그대로 검정이면 선형 필터가 가장자리에서 검정을 섞어 어두운 테두리가 생긴다.
        /// </summary>
        static byte[] ToStraightAlpha(byte[] pma, int width, int height)
        {
            var straight = new byte[pma.Length];
            for (int i = 0; i < pma.Length; i += 4)
            {
                int a = pma[i + 3];
                straight[i + 3] = (byte)a;
                if (a == 0)
                {
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    straight[i + c] = (byte)Math.Min(255, (pma[i + c] * 255 + a / 2) / a);
                }
            }

            // 투명한 픽셀만 고치고 이웃은 불투명한 것만 읽으므로 한 번에 돌려도 순서에 따라 결과가 바뀌지 않는다
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (straight[i + 3] != 0)
                    {
                        continue;
                    }

                    int r = 0, g = 0, b = 0, n = 0;
                    for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                    {
                        for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                        {
                            int j = (ny * width + nx) * 4;
                            if (straight[j + 3] == 0)
                            {
                                continue;
                            }

                            r += straight[j];
                            g += straight[j + 1];
                            b += straight[j + 2];
                            n++;
                        }
                    }

                    if (n > 0)
                    {
                        straight[i] = (byte)(r / n);
                        straight[i + 1] = (byte)(g / n);
                        straight[i + 2] = (byte)(b / n);
                    }
                }
            }

            return straight;
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

            if (export.RuntimeJson == null && export.RuntimeJsonError == null)
            {
                Prepare(export, RuntimeVersion(), UseStraightAlpha()); // 백그라운드 준비를 거치지 않고 온 경우
            }

            if (export.RuntimeJson == null)
            {
                message = export.RuntimeJsonError;
                return null;
            }

            byte[] json = export.RuntimeJson;
            string name = export.Name;
            var atlasText = new TextAsset(export.AtlasText) { name = name + ".atlas", hideFlags = HideFlags.DontSave };
            Texture2D[] textures = export.Pages.Select((page, i) => CreateTexture(page, export.TexturePixels[i])).ToArray();
            var material = new Material(Shader.Find("Spine/Skeleton")) { hideFlags = HideFlags.DontSave };
            MaterialChecks.EnablePMATextureAtMaterial(material, export.StraightAlpha == false); // 페이지 머티리얼은 이것을 복사한다
            SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntimeInstance(atlasText, textures, material, true);
            atlas.hideFlags = HideFlags.DontSave;
            var data = new TextAsset(Encoding.UTF8.GetString(json)) { name = name + ".json", hideFlags = HideFlags.DontSave };
            SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntimeInstance(data, atlas, true);

            // 여기서 만든 객체는 DontSave 라 스스로 사라지지 않는다. 미리보기를 바꾸거나 닫을 때 Release 로 지운다.
            var parts = new List<UnityEngine.Object> { atlasText, material, data };
            parts.AddRange(textures);
            if (atlas.materials != null)
            {
                parts.AddRange(atlas.materials.Where(m => m != null));
            }

            parts.Add(atlas);
            if (asset != null)
            {
                asset.hideFlags = HideFlags.DontSave;
                message = export.AtlasMessage; // 미리보기는 되지만 빠진 이미지가 있다
                Owned[asset] = parts;
            }
            else
            {
                Destroy(parts);
            }

            return asset;
        }

        // 이 클래스가 만든 미리보기 에셋 → 함께 만든 객체들(텍스처·머티리얼·텍스트 에셋·아틀라스)
        static readonly Dictionary<SkeletonDataAsset, List<UnityEngine.Object>> Owned = new Dictionary<SkeletonDataAsset, List<UnityEngine.Object>>();

        static ExternalSpinePreview()
        {
            // 도메인이 다시 로드되면 이 표가 사라져 지울 길이 없어진다. 그 전에 모두 지운다.
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        }

        /// <summary>
        /// 이 클래스가 만든 미리보기 에셋이면 텍스처까지 함께 지우고 true. 프로젝트의 에셋이면 건드리지 않는다.
        /// 지우지 않으면 파일을 열거나 스켈레톤을 바꿀 때마다 텍스처와 스켈레톤 데이터가 쌓여, GC 가 도는 시간이 길어진다.
        /// </summary>
        public static bool IsOwned(SkeletonDataAsset asset) => ReferenceEquals(asset, null) == false && Owned.ContainsKey(asset);

        public static bool Release(SkeletonDataAsset asset)
        {
            if (ReferenceEquals(asset, null) || Owned.TryGetValue(asset, out List<UnityEngine.Object> parts) == false)
            {
                return false;
            }

            Owned.Remove(asset);
            Destroy(parts);
            if (asset != null)
            {
                asset.Clear();
                UnityEngine.Object.DestroyImmediate(asset);
            }

            return true;
        }

        public static void ReleaseAll()
        {
            foreach (SkeletonDataAsset asset in Owned.Keys.ToList())
            {
                Release(asset);
            }
        }

        static void Destroy(List<UnityEngine.Object> parts)
        {
            foreach (UnityEngine.Object part in parts)
            {
                if (part != null)
                {
                    UnityEngine.Object.DestroyImmediate(part);
                }
            }
        }

        static Texture2D CreateTexture(SpineExportPage page, byte[] flipped)
        {
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
