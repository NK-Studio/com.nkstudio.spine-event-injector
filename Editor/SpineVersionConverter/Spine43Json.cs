using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 4.3 JSON ⇄ 공통 모델(원본 SkeletonData43Json.cpp). 4.3 JSON 을 4.2 모양(레거시)으로 바꿔 4.2 리더/라이터를 재사용하고,
    /// 4.3 에만 있는 값(통합 제약조건, 뼈 아이콘, slider, drawOrderFolder 등)은 따로 옮긴다.
    /// 다운그레이드 가능 여부 검사(<see cref="RequireLegacyCompatible"/>)도 여기 있다.
    /// </summary>
    public static class Spine43Json
    {
        private static readonly string[] LegacyTypes = { "ik", "transform", "path", "physics" };
        private static readonly string[] Properties = { "rotate", "x", "y", "scaleX", "scaleY", "shearY" };
        private static readonly string[] Mixes = { "mixRotate", "mixX", "mixY", "mixScaleX", "mixScaleY", "mixShearY" };

        /// <summary>
        /// 4.3 JSON → 4.2 모양. 통합 constraints 배열을 ik/transform/path/physics 로 나누고(적용 순서는 order 로 보존),
        /// transform 의 source/속성 매핑 → target/mix, path 의 slot → target, 링크드 메시 source → parent 로 되돌린다. slider 는 버린다.
        /// </summary>
        public static JObject ToLegacyJson(JObject source)
        {
            var root = (JObject)source.DeepClone();
            if (root["constraints"] is JArray constraints)
            {
                root.Remove("constraints");
                int order = 0;
                foreach (JObject original in constraints.OfType<JObject>())
                {
                    var constraint = (JObject)original.DeepClone();
                    string type = (string)constraint["type"];
                    constraint.Remove("type");
                    constraint["order"] = order++;
                    if (type == "slider")
                    {
                        continue;
                    }

                    if (LegacyTypes.Contains(type) == false)
                    {
                        throw new SpineConversionException($"알 수 없는 4.3 제약조건 종류: '{type}'");
                    }

                    switch (type)
                    {
                        case "ik":
                            constraint["uniform"] = SpineJson.GetString(constraint, "scaleY", "none") == "uniform";
                            break;
                        case "transform":
                        {
                            SpineJson.Rename(constraint, "source", "target");
                            constraint["local"] = SpineJson.GetBool(constraint, "localSource", false);
                            constraint["relative"] = SpineJson.GetBool(constraint, "additive", false);
                            JObject properties = SpineJson.FindObject(constraint, "properties") ?? new JObject();
                            // 순서 의존: i=2(mixY)·i=4(mixScaleY)의 대체값은 바로 앞에서 정한 mixX·mixScaleX 다.
                            for (int i = 0; i < Properties.Length; i++)
                            {
                                bool isTargeted = properties.Properties().Any(property => SpineJson.FindObject(property.Value, "to")?.ContainsKey(Properties[i]) == true);
                                float fallback = i == 2 ? SpineJson.GetFloat(constraint, "mixX", 1f) : i == 4 ? SpineJson.GetFloat(constraint, "mixScaleX", 1f) : 1f;
                                constraint[Mixes[i]] = SpineJson.Number(isTargeted == true ? SpineJson.GetFloat(constraint, Mixes[i], fallback) : 0f);
                            }

                            break;
                        }
                        case "path":
                            SpineJson.Rename(constraint, "slot", "target");
                            break;
                        case "physics":
                            // 4.3 기본값(0.5, 0.85)을 명시해 4.2 리더의 기본값(1, 1)으로 바뀌지 않게 한다.
                            constraint["inertia"] = SpineJson.Number(SpineJson.GetFloat(constraint, "inertia", 0.5f));
                            constraint["damping"] = SpineJson.Number(SpineJson.GetFloat(constraint, "damping", 0.85f));
                            break;
                    }

                    if (root[type] is JArray legacyArray == false)
                    {
                        legacyArray = new JArray();
                        root[type] = legacyArray;
                    }

                    legacyArray.Add(constraint);
                }
            }

            if (root["skeleton"] is JObject header)
            {
                foreach (string key in new[] { "images", "audio" })
                {
                    if (header.TryGetValue(key, out JToken value) == true && value.Type == JTokenType.Null)
                    {
                        header.Remove(key);
                    }
                }
            }

            foreach (JObject attachment in EnumerateSkinAttachments(root))
            {
                if (attachment.ContainsKey("source") == true)
                {
                    attachment["type"] = "linkedmesh";
                }

                SpineJson.Rename(attachment, "source", "parent");
                if (attachment["sequence"] is JObject sequence)
                {
                    SpineJson.Rename(sequence, "setup", "setupIndex");
                }
            }

            return root;
        }

        /// <summary>4.2 모양 → 4.3 JSON. <see cref="ToLegacyJson"/>의 역방향.</summary>
        public static JObject FromLegacyJson(JObject source)
        {
            var root = (JObject)source.DeepClone();
            var constraints = new List<JObject>();
            foreach (string type in LegacyTypes)
            {
                if (root[type] is JArray legacyArray == false)
                {
                    continue;
                }

                foreach (JObject original in legacyArray.OfType<JObject>())
                {
                    var constraint = (JObject)original.DeepClone();
                    constraint["type"] = type;
                    switch (type)
                    {
                        case "ik":
                            if (SpineJson.GetBool(constraint, "uniform", false) == true)
                            {
                                constraint["scaleY"] = "uniform";
                            }

                            constraint.Remove("uniform");
                            break;
                        case "transform":
                        {
                            SpineJson.Rename(constraint, "target", "source");
                            bool isLocal = SpineJson.GetBool(constraint, "local", false);
                            constraint["localSource"] = isLocal;
                            constraint["localTarget"] = isLocal;
                            constraint["additive"] = SpineJson.GetBool(constraint, "relative", false);
                            constraint.Remove("local");
                            constraint.Remove("relative");
                            // 4.2 transform 은 각 성분을 자기 자신에게 매핑하는 것과 같다.
                            var properties = new JObject();
                            foreach (string property in Properties)
                            {
                                properties[property] = new JObject { ["to"] = new JObject { [property] = new JObject() } };
                            }

                            constraint["properties"] = properties;
                            break;
                        }
                        case "path":
                            SpineJson.Rename(constraint, "target", "slot");
                            break;
                        case "physics":
                            constraint["inertia"] = SpineJson.Number(SpineJson.GetFloat(constraint, "inertia", 1f));
                            constraint["damping"] = SpineJson.Number(SpineJson.GetFloat(constraint, "damping", 1f));
                            break;
                    }

                    constraints.Add(constraint);
                }

                root.Remove(type);
            }

            // 안정 정렬: order 가 같으면 ik → transform → path → physics 순서를 유지한다.
            List<JObject> ordered = constraints.OrderBy(constraint => SpineJson.GetInt(constraint, "order", 0)).ToList();
            foreach (JObject constraint in ordered)
            {
                constraint.Remove("order");
            }

            if (ordered.Count > 0)
            {
                root["constraints"] = new JArray(ordered);
            }

            foreach (JObject attachment in EnumerateSkinAttachments(root))
            {
                SpineJson.Rename(attachment, "parent", "source");
                if (attachment["sequence"] is JObject sequence)
                {
                    SpineJson.Rename(sequence, "setupIndex", "setup");
                }
            }

            return root;
        }

        public static SpineSkeleton Read(JObject root)
        {
            SpineSkeleton skeleton = SpineSkeletonJsonReader.Read(ToLegacyJson(root));
            skeleton.Constraints43 = SpineJson.Find(root, "constraints") is JArray constraints ? (JArray)constraints.DeepClone() : new JArray();

            List<JToken> bones = SpineJson.Items(root, "bones").ToList();
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                skeleton.Bones[i].IconSize = SpineJson.GetFloat(bones[i], "iconSize", 1f);
                skeleton.Bones[i].IconRotation = SpineJson.GetFloat(bones[i], "iconRotation", 0f);
            }

            List<JToken> skins = SpineJson.Items(root, "skins").ToList();
            foreach (SpineSkin skin in skeleton.Skins)
            {
                JToken rawSkin = skins.FirstOrDefault(candidate => SpineJson.GetString(candidate, "name", string.Empty) == skin.Name);
                if (rawSkin == null)
                {
                    continue;
                }

                skin.Slider = SpineJson.GetStrings(rawSkin, "slider");
                foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
                {
                    foreach (KeyValuePair<string, SpineAttachment> entry in slotEntry.Value)
                    {
                        JToken raw = rawSkin["attachments"]?[slotEntry.Key]?[entry.Key];
                        switch (entry.Value)
                        {
                            case SpineLinkedMeshAttachment linkedMesh:
                                linkedMesh.SourceSlot = SpineJson.GetString(raw, "slot", null);
                                break;
                            case SpineClippingAttachment clipping:
                                clipping.Convex = SpineJson.GetBool(raw, "convex", false);
                                clipping.Inverse = SpineJson.GetBool(raw, "inverse", false);
                                break;
                        }
                    }
                }
            }

            foreach (SpineAnimation animation in skeleton.Animations)
            {
                JToken raw = root["animations"]?[animation.Name];
                string color = SpineJson.GetString(raw, "color", null);
                if (color != null)
                {
                    animation.Color = SpineJson.ParseColor(color, true);
                }

                animation.DrawOrderFolder = SpineJson.Find(raw, "drawOrderFolder") is JArray folders ? (JArray)folders.DeepClone() : new JArray();
                foreach (JProperty slider in SpineJson.Properties(raw, "slider"))
                {
                    foreach (JProperty timeline in ((JObject)slider.Value).Properties())
                    {
                        if (timeline.Name != "time" && timeline.Name != "mix")
                        {
                            throw new SpineConversionException($"알 수 없는 slider 타임라인: '{timeline.Name}'");
                        }

                        var frames = new List<SpineTimelineFrame>();
                        foreach (JToken key in timeline.Value)
                        {
                            var frame = new SpineTimelineFrame
                            {
                                Time = SpineJson.GetFloat(key, "time", 0f),
                                Value1 = SpineJson.GetFloat(key, "value", 0f),
                            };
                            JToken curve = SpineJson.Find(key, "curve");
                            if (curve != null && curve.Type == JTokenType.String && (string)curve == "stepped")
                            {
                                frame.CurveType = SpineCurveType.Stepped;
                            }
                            else if (curve != null)
                            {
                                frame.CurveType = SpineCurveType.Bezier;
                                frame.Curve = curve.Select(value => value.Value<float>()).ToList();
                            }

                            frames.Add(frame);
                        }

                        animation.Slider.GetOrAdd(slider.Name)[timeline.Name] = frames;
                    }
                }
            }

            // 바이너리는 이 편집기 값들을 nonessential 일 때만 싣는다 — 있으면 잃지 않게 nonessential 로 올린다.
            if (skeleton.Bones.Any(bone => bone.IconSize != 1f || bone.IconRotation != 0f) == true
                || skeleton.Animations.Any(animation => animation.Color.HasValue == true) == true)
            {
                skeleton.Nonessential = true;
            }

            return skeleton;
        }

        public static JObject Write(SpineSkeleton skeleton)
        {
            JObject root = FromLegacyJson(SpineSkeletonJsonWriter.Write(skeleton));
            if (skeleton.Constraints43.Count > 0)
            {
                root["constraints"] = skeleton.Constraints43.DeepClone();
            }

            if (root["bones"] is JArray bones)
            {
                for (int i = 0; i < skeleton.Bones.Count; i++)
                {
                    if (skeleton.Bones[i].IconSize != 1f)
                    {
                        bones[i]["iconSize"] = SpineJson.Number(skeleton.Bones[i].IconSize);
                    }

                    if (skeleton.Bones[i].IconRotation != 0f)
                    {
                        bones[i]["iconRotation"] = SpineJson.Number(skeleton.Bones[i].IconRotation);
                    }
                }
            }

            foreach (SpineSkin skin in skeleton.Skins)
            {
                foreach (JObject target in SpineJson.Items(root, "skins").OfType<JObject>())
                {
                    if (SpineJson.GetString(target, "name", string.Empty) != skin.Name)
                    {
                        continue;
                    }

                    if (skin.Slider.Count > 0)
                    {
                        target["slider"] = new JArray(skin.Slider);
                    }

                    foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
                    {
                        foreach (KeyValuePair<string, SpineAttachment> entry in slotEntry.Value)
                        {
                            var raw = (JObject)target["attachments"][slotEntry.Key][entry.Key];
                            if (entry.Value is SpineLinkedMeshAttachment linkedMesh && linkedMesh.SourceSlot != null)
                            {
                                raw["slot"] = linkedMesh.SourceSlot;
                            }

                            if (entry.Value is SpineClippingAttachment clipping)
                            {
                                if (clipping.Convex == true)
                                {
                                    raw["convex"] = true;
                                }

                                if (clipping.Inverse == true)
                                {
                                    raw["inverse"] = true;
                                }
                            }
                        }
                    }
                }
            }

            foreach (SpineAnimation animation in skeleton.Animations)
            {
                var raw = (JObject)root["animations"][animation.Name];
                if (animation.Color.HasValue == true)
                {
                    raw["color"] = SpineJson.FormatColor(animation.Color.Value, true);
                }

                if (animation.DrawOrderFolder.Count > 0)
                {
                    raw["drawOrderFolder"] = animation.DrawOrderFolder.DeepClone();
                }

                foreach (KeyValuePair<string, SpineTimelineSet> slider in animation.Slider)
                {
                    var sliderJson = raw["slider"] as JObject ?? new JObject();
                    raw["slider"] = sliderJson;
                    var timelinesJson = sliderJson[slider.Key] as JObject ?? new JObject();
                    sliderJson[slider.Key] = timelinesJson;
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in slider.Value)
                    {
                        var keys = new JArray();
                        foreach (SpineTimelineFrame frame in timeline.Value)
                        {
                            var key = new JObject { ["time"] = SpineJson.Number(frame.Time), ["value"] = SpineJson.Number(frame.Value1) };
                            if (frame.CurveType == SpineCurveType.Stepped)
                            {
                                key["curve"] = "stepped";
                            }
                            else if (frame.CurveType == SpineCurveType.Bezier)
                            {
                                key["curve"] = SpineJson.Numbers(frame.Curve);
                            }

                            keys.Add(key);
                        }

                        timelinesJson[timeline.Key] = keys;
                    }
                }
            }

            return root;
        }

        /// <summary>
        /// 4.3 → 4.2 로 내릴 때 4.2 가 표현할 수 없는 기능이 있으면 거부한다. 조용히 버리면 애니메이션이 다르게 보이기 때문이다.
        /// 거부되면 Spine 편집기에서 해당 기능을 굽거나(bake) 빼고 다시 내보내야 한다.
        /// </summary>
        public static void RequireLegacyCompatible(SpineSkeleton skeleton)
        {
            void Fail(string feature)
            {
                throw new SpineConversionException($"Spine 4.3 의 {feature} 은(는) 4.2 로 내릴 수 없다. Spine 에서 해당 기능을 굽거나 빼고 다시 내보낸 뒤 변환한다.");
            }

            foreach (JObject constraint in skeleton.Constraints43.OfType<JObject>())
            {
                string type = (string)constraint["type"];
                string scaleY = SpineJson.GetString(constraint, "scaleY", "none");
                if (type == "slider")
                {
                    Fail($"slider 제약조건 '{SpineJson.GetString(constraint, "name", string.Empty)}'");
                }

                if ((type == "ik" || type == "physics") && scaleY == "volume")
                {
                    Fail("scaleY=volume");
                }

                if (type == "physics" && scaleY == "uniform")
                {
                    Fail("physics scaleY=uniform");
                }

                if (type != "transform")
                {
                    continue;
                }

                if (SpineJson.GetBool(constraint, "clamp", false) == true
                    || SpineJson.GetBool(constraint, "localSource", false) != SpineJson.GetBool(constraint, "localTarget", false))
                {
                    Fail("transform clamp 또는 서로 다른 좌표계(localSource≠localTarget)");
                }

                foreach (JProperty property in SpineJson.Properties(constraint, "properties"))
                {
                    if (SpineJson.GetFloat(property.Value, "offset", 0f) != 0f)
                    {
                        Fail("transform 속성 offset");
                    }

                    foreach (JProperty mapping in SpineJson.Properties(property.Value, "to"))
                    {
                        if (mapping.Name != property.Name
                            || SpineJson.GetFloat(mapping.Value, "offset", 0f) != 0f
                            || SpineJson.GetFloat(mapping.Value, "scale", 1f) != 1f)
                        {
                            Fail("다른 성분으로의 transform 속성 매핑");
                        }
                    }
                }
            }

            foreach (SpineSkin skin in skeleton.Skins)
            {
                foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
                {
                    foreach (SpineAttachment attachment in slotEntry.Value.Values)
                    {
                        if (attachment is SpineLinkedMeshAttachment linkedMesh && linkedMesh.SourceSlot != null && linkedMesh.SourceSlot != slotEntry.Key)
                        {
                            Fail($"다른 슬롯을 원본으로 쓰는 링크드 메시('{attachment.Name}')");
                        }

                        if (attachment is SpineClippingAttachment clipping && clipping.Inverse == true)
                        {
                            Fail($"inverse 클리핑('{attachment.Name}')");
                        }
                    }
                }
            }

            foreach (SpineAnimation animation in skeleton.Animations)
            {
                if (animation.DrawOrderFolder.Count > 0 || animation.Slider.Count > 0)
                {
                    Fail($"애니메이션 '{animation.Name}' 의 slider·drawOrderFolder 타임라인");
                }
            }
        }

        private static IEnumerable<JObject> EnumerateSkinAttachments(JObject root)
        {
            foreach (JObject skin in SpineJson.Items(root, "skins").OfType<JObject>())
            {
                foreach (JProperty slot in SpineJson.Properties(skin, "attachments"))
                {
                    foreach (JObject attachment in ((JObject)slot.Value).Properties().Select(property => property.Value).OfType<JObject>())
                    {
                        yield return attachment;
                    }
                }
            }
        }
    }
}
