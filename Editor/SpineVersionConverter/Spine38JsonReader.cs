using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 3.8 JSON 리더(원본 SkeletonData38JsonReader). 4.x 와 키 이름이 다르다(뼈 <c>transform</c>, 제약조건 <c>rotateMix</c>·<c>translateMix</c>,
    /// 슬롯 <c>color</c>·<c>twoColor</c>, 링크드 메시 <c>deform</c> 등). 곡선은 3.x 의 상대(0~1) 베지어 4개 값을 그대로 담는다 —
    /// 4.x 절대 곡선으로 바꾸는 일은 버전 경계 패스(<see cref="Spine3xConversions"/>)가 한다.
    /// </summary>
    public static class Spine38JsonReader
    {
        public static SpineSkeleton Read(JObject root)
        {
            var skeleton = new SpineSkeleton();
            JObject header = SpineJson.FindObject(root, "skeleton") ?? new JObject();
            string hash = SpineJson.GetString(header, "hash", null);
            if (hash != null)
            {
                // 3.8 해시는 보통 SHA-1(20바이트)의 base64 다. 원문을 그대로 들고 있다가 3.8 로 다시 쓸 때 쓴다.
                skeleton.HashString = hash;
                skeleton.Hash = Base64ToHash38(hash);
            }

            skeleton.Version = SpineJson.GetString(header, "spine", null);
            skeleton.X = SpineJson.GetFloat(header, "x", 0f);
            skeleton.Y = SpineJson.GetFloat(header, "y", 0f);
            skeleton.Width = SpineJson.GetFloat(header, "width", 0f);
            skeleton.Height = SpineJson.GetFloat(header, "height", 0f);
            skeleton.Fps = SpineJson.GetFloat(header, "fps", 30f);
            skeleton.ImagesPath = SpineJson.GetString(header, "images", null);
            skeleton.AudioPath = SpineJson.GetString(header, "audio", null);
            // JSON 에는 nonessential 플래그가 없다 — 편집기 전용 값이 하나라도 있으면 nonessential 로 본다.
            skeleton.Nonessential = (skeleton.Fps == 30f && skeleton.ImagesPath == null && skeleton.AudioPath == null) == false;

            ReadBones(root, skeleton);
            ReadSlots(root, skeleton);
            ReadConstraints(root, skeleton);
            ReadSkins(root, skeleton);
            ReadEvents(root, skeleton);
            foreach (JProperty property in SpineJson.Properties(root, "animations"))
            {
                skeleton.Animations.Add(ReadAnimation(property.Name, property.Value, skeleton));
            }

            return skeleton;
        }

        private static void ReadBones(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken bone in SpineJson.Items(root, "bones"))
            {
                skeleton.Bones.Add(new SpineBone
                {
                    Name = SpineJson.GetString(bone, "name", null),
                    Parent = SpineJson.GetString(bone, "parent", null),
                    Length = SpineJson.GetFloat(bone, "length", 0f),
                    X = SpineJson.GetFloat(bone, "x", 0f),
                    Y = SpineJson.GetFloat(bone, "y", 0f),
                    Rotation = SpineJson.GetFloat(bone, "rotation", 0f),
                    ScaleX = SpineJson.GetFloat(bone, "scaleX", 1f),
                    ScaleY = SpineJson.GetFloat(bone, "scaleY", 1f),
                    ShearX = SpineJson.GetFloat(bone, "shearX", 0f),
                    ShearY = SpineJson.GetFloat(bone, "shearY", 0f),
                    // 3.8 은 상속 모드를 "transform" 키에 쓴다(4.x 는 "inherit").
                    Inherit = (SpineInherit)SpineNames.IndexOf(SpineNames.Inherit, SpineJson.GetString(bone, "transform", "normal"), "transform"),
                    SkinRequired = SpineJson.GetBool(bone, "skin", false),
                    Color = ReadColor(bone, "color", true),
                });
            }
        }

        private static void ReadSlots(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken slot in SpineJson.Items(root, "slots"))
            {
                skeleton.Slots.Add(new SpineSlot
                {
                    Name = SpineJson.GetString(slot, "name", null),
                    Bone = SpineJson.GetString(slot, "bone", null),
                    Color = ReadColor(slot, "color", true),
                    DarkColor = ReadColor(slot, "dark", false),
                    AttachmentName = SpineJson.GetString(slot, "attachment", null),
                    BlendMode = (SpineBlendMode)SpineNames.IndexOf(SpineNames.BlendMode, SpineJson.GetString(slot, "blend", "normal"), "blend"),
                });
            }
        }

        private static void ReadConstraints(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken ik in SpineJson.Items(root, "ik"))
            {
                skeleton.IkConstraints.Add(new SpineIkConstraint
                {
                    Name = SpineJson.GetString(ik, "name", null),
                    Order = SpineJson.GetInt(ik, "order", 0),
                    SkinRequired = SpineJson.GetBool(ik, "skin", false),
                    Bones = SpineJson.GetStrings(ik, "bones"),
                    Target = SpineJson.GetString(ik, "target", null),
                    Mix = SpineJson.GetFloat(ik, "mix", 1f),
                    Softness = SpineJson.GetFloat(ik, "softness", 0f),
                    BendPositive = SpineJson.GetBool(ik, "bendPositive", true),
                    Compress = SpineJson.GetBool(ik, "compress", false),
                    Stretch = SpineJson.GetBool(ik, "stretch", false),
                    Uniform = SpineJson.GetBool(ik, "uniform", false),
                });
            }

            foreach (JToken transform in SpineJson.Items(root, "transform"))
            {
                // 3.8 은 X/Y 성분을 나누지 않는다 — translateMix 하나가 mixX·mixY, scaleMix 하나가 mixScaleX·mixScaleY 다.
                float translateMix = SpineJson.GetFloat(transform, "translateMix", 1f);
                float scaleMix = SpineJson.GetFloat(transform, "scaleMix", 1f);
                skeleton.TransformConstraints.Add(new SpineTransformConstraint
                {
                    Name = SpineJson.GetString(transform, "name", null),
                    Order = SpineJson.GetInt(transform, "order", 0),
                    SkinRequired = SpineJson.GetBool(transform, "skin", false),
                    Bones = SpineJson.GetStrings(transform, "bones"),
                    Target = SpineJson.GetString(transform, "target", null),
                    MixRotate = SpineJson.GetFloat(transform, "rotateMix", 1f),
                    MixX = translateMix,
                    MixY = translateMix,
                    MixScaleX = scaleMix,
                    MixScaleY = scaleMix,
                    MixShearY = SpineJson.GetFloat(transform, "shearMix", 1f),
                    OffsetRotation = SpineJson.GetFloat(transform, "rotation", 0f),
                    OffsetX = SpineJson.GetFloat(transform, "x", 0f),
                    OffsetY = SpineJson.GetFloat(transform, "y", 0f),
                    OffsetScaleX = SpineJson.GetFloat(transform, "scaleX", 0f),
                    OffsetScaleY = SpineJson.GetFloat(transform, "scaleY", 0f),
                    OffsetShearY = SpineJson.GetFloat(transform, "shearY", 0f),
                    Relative = SpineJson.GetBool(transform, "relative", false),
                    Local = SpineJson.GetBool(transform, "local", false),
                });
            }

            foreach (JToken path in SpineJson.Items(root, "path"))
            {
                float translateMix = SpineJson.GetFloat(path, "translateMix", 1f);
                skeleton.PathConstraints.Add(new SpinePathConstraint
                {
                    Name = SpineJson.GetString(path, "name", null),
                    Order = SpineJson.GetInt(path, "order", 0),
                    SkinRequired = SpineJson.GetBool(path, "skin", false),
                    Bones = SpineJson.GetStrings(path, "bones"),
                    Target = SpineJson.GetString(path, "target", null),
                    PositionMode = (SpinePositionMode)SpineNames.IndexOf(SpineNames.PositionMode, SpineJson.GetString(path, "positionMode", "percent"), "positionMode"),
                    SpacingMode = (SpineSpacingMode)SpineNames.IndexOf(SpineNames.SpacingMode, SpineJson.GetString(path, "spacingMode", "length"), "spacingMode"),
                    RotateMode = (SpineRotateMode)SpineNames.IndexOf(SpineNames.RotateMode, SpineJson.GetString(path, "rotateMode", "tangent"), "rotateMode"),
                    OffsetRotation = SpineJson.GetFloat(path, "rotation", 0f),
                    Position = SpineJson.GetFloat(path, "position", 0f),
                    Spacing = SpineJson.GetFloat(path, "spacing", 0f),
                    MixRotate = SpineJson.GetFloat(path, "rotateMix", 1f),
                    MixX = translateMix,
                    MixY = translateMix,
                });
            }
        }

        private static void ReadSkins(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken skinJson in SpineJson.Items(root, "skins"))
            {
                // 3.8 스킨에는 색이 없다(4.2 의 nonessential 스킨 색은 그 뒤에 생겼다).
                var skin = new SpineSkin
                {
                    Name = SpineJson.GetString(skinJson, "name", string.Empty),
                    Bones = SpineJson.GetStrings(skinJson, "bones"),
                    Ik = SpineJson.GetStrings(skinJson, "ik"),
                    Transform = SpineJson.GetStrings(skinJson, "transform"),
                    Path = SpineJson.GetStrings(skinJson, "path"),
                };
                foreach (JProperty slot in SpineJson.Properties(skinJson, "attachments"))
                {
                    SpineMap<SpineAttachment> slotAttachments = skin.Attachments.GetOrAdd(slot.Name);
                    foreach (JProperty entry in ((JObject)slot.Value).Properties())
                    {
                        slotAttachments[entry.Name] = ReadAttachment(entry.Name, entry.Value);
                    }
                }

                skeleton.Skins.Add(skin);
            }
        }

        private static SpineAttachment ReadAttachment(string attachmentName, JToken json)
        {
            string name = SpineJson.GetString(json, "name", attachmentName);
            string path = SpineJson.GetString(json, "path", name);
            SpineColor? color = ReadColor(json, "color", true);
            var type = (SpineAttachmentType)SpineNames.IndexOf(SpineNames.AttachmentType, SpineJson.GetString(json, "type", "region"), "어태치먼트 종류");
            switch (type)
            {
                case SpineAttachmentType.Region:
                    return new SpineRegionAttachment
                    {
                        Name = name,
                        Path = path,
                        X = SpineJson.GetFloat(json, "x", 0f),
                        Y = SpineJson.GetFloat(json, "y", 0f),
                        Rotation = SpineJson.GetFloat(json, "rotation", 0f),
                        ScaleX = SpineJson.GetFloat(json, "scaleX", 1f),
                        ScaleY = SpineJson.GetFloat(json, "scaleY", 1f),
                        Width = SpineJson.GetFloat(json, "width", 32f),
                        Height = SpineJson.GetFloat(json, "height", 32f),
                        Color = color,
                    };
                case SpineAttachmentType.Mesh:
                    return new SpineMeshAttachment
                    {
                        Name = name,
                        Path = path,
                        Width = SpineJson.GetFloat(json, "width", 32f),
                        Height = SpineJson.GetFloat(json, "height", 32f),
                        Color = color,
                        HullLength = SpineJson.GetInt(json, "hull", 0),
                        Triangles = SpineJson.GetInts(json, "triangles"),
                        Edges = SpineJson.GetInts(json, "edges"),
                        Uvs = SpineJson.GetFloats(json, "uvs"),
                        Vertices = SpineJson.GetFloats(json, "vertices"),
                    };
                case SpineAttachmentType.LinkedMesh:
                    return new SpineLinkedMeshAttachment
                    {
                        Name = name,
                        Path = path,
                        Width = SpineJson.GetFloat(json, "width", 32f),
                        Height = SpineJson.GetFloat(json, "height", 32f),
                        Color = color,
                        ParentMesh = SpineJson.GetString(json, "parent", null)
                            ?? throw new SpineConversionException($"링크드 메시 '{name}' 에 parent 가 없다."),
                        // 3.8 은 "부모 디폼 타임라인을 이어받는가"를 bool "deform" 으로 쓴다(4.x 의 "timelines"). 원본처럼 정수(0/1)로 받는다.
                        Timelines = SpineJson.GetInt(json, "deform", 1),
                        Skin = SpineJson.GetString(json, "skin", null),
                    };
                case SpineAttachmentType.BoundingBox:
                    return new SpineBoundingBoxAttachment
                    {
                        Name = name,
                        Path = path,
                        VertexCount = SpineJson.GetInt(json, "vertexCount", 0),
                        Color = color,
                        Vertices = SpineJson.GetFloats(json, "vertices"),
                    };
                case SpineAttachmentType.Path:
                    return new SpinePathAttachment
                    {
                        Name = name,
                        Path = path,
                        VertexCount = SpineJson.GetInt(json, "vertexCount", 0),
                        Closed = SpineJson.GetBool(json, "closed", false),
                        ConstantSpeed = SpineJson.GetBool(json, "constantSpeed", true),
                        Color = color,
                        Vertices = SpineJson.GetFloats(json, "vertices"),
                        Lengths = SpineJson.GetFloats(json, "lengths"),
                    };
                case SpineAttachmentType.Point:
                    return new SpinePointAttachment
                    {
                        Name = name,
                        Path = path,
                        X = SpineJson.GetFloat(json, "x", 0f),
                        Y = SpineJson.GetFloat(json, "y", 0f),
                        Rotation = SpineJson.GetFloat(json, "rotation", 0f),
                        Color = color,
                    };
                default:
                    return new SpineClippingAttachment
                    {
                        Name = name,
                        Path = path,
                        VertexCount = SpineJson.GetInt(json, "vertexCount", 0),
                        Color = color,
                        EndSlot = SpineJson.GetString(json, "end", null),
                        Vertices = SpineJson.GetFloats(json, "vertices"),
                    };
            }
        }

        private static void ReadEvents(JObject root, SpineSkeleton skeleton)
        {
            foreach (JProperty property in SpineJson.Properties(root, "events"))
            {
                var spineEvent = new SpineEvent
                {
                    Name = property.Name,
                    IntValue = SpineJson.GetInt(property.Value, "int", 0),
                    FloatValue = SpineJson.GetFloat(property.Value, "float", 0f),
                    StringValue = SpineJson.GetString(property.Value, "string", null),
                    AudioPath = SpineJson.GetString(property.Value, "audio", null),
                };
                if (spineEvent.AudioPath != null)
                {
                    spineEvent.Volume = SpineJson.GetFloat(property.Value, "volume", 1f);
                    spineEvent.Balance = SpineJson.GetFloat(property.Value, "balance", 0f);
                }

                skeleton.Events.Add(spineEvent);
            }
        }

        private static SpineAnimation ReadAnimation(string name, JToken json, SpineSkeleton skeleton)
        {
            var animation = new SpineAnimation { Name = name };

            // 3.8 의 슬롯 타임라인 이름을 모델(4.x) 이름으로 옮긴다: color → rgba, twoColor → rgba2.
            // 원본처럼 키가 하나라도 있을 때만 타임라인을 만든다(빈 배열이면 타임라인 자체가 없다).
            foreach (JProperty slot in SpineJson.Properties(json, "slots"))
            {
                SpineTimelineSet timelines = animation.Slots.GetOrAdd(slot.Name);
                foreach (JToken frameJson in SpineJson.Items(slot.Value, "attachment"))
                {
                    timelines.GetOrAdd("attachment").Add(new SpineTimelineFrame
                    {
                        Time = SpineJson.GetFloat(frameJson, "time", 0f),
                        Str1 = SpineJson.GetString(frameJson, "name", null),
                    });
                }

                foreach (JToken frameJson in SpineJson.Items(slot.Value, "color"))
                {
                    var frame = new SpineTimelineFrame
                    {
                        Time = SpineJson.GetFloat(frameJson, "time", 0f),
                        Color1 = ReadColor(frameJson, "color", true),
                    };
                    ReadCurve(frameJson, frame);
                    timelines.GetOrAdd("rgba").Add(frame);
                }

                foreach (JToken frameJson in SpineJson.Items(slot.Value, "twoColor"))
                {
                    var frame = new SpineTimelineFrame
                    {
                        Time = SpineJson.GetFloat(frameJson, "time", 0f),
                        Color1 = ReadColor(frameJson, "light", true),
                        Color2 = ReadColor(frameJson, "dark", false),
                    };
                    ReadCurve(frameJson, frame);
                    timelines.GetOrAdd("rgba2").Add(frame);
                }
            }

            foreach (JProperty bone in SpineJson.Properties(json, "bones"))
            {
                SpineTimelineSet timelines = animation.Bones.GetOrAdd(bone.Name);
                ReadValueTimeline(bone.Value, "rotate", timelines, 1, "angle", null, 0f);
                ReadValueTimeline(bone.Value, "translate", timelines, 2, "x", "y", 0f);
                ReadValueTimeline(bone.Value, "scale", timelines, 2, "x", "y", 1f);
                ReadValueTimeline(bone.Value, "shear", timelines, 2, "x", "y", 0f);
            }

            foreach (JProperty ik in SpineJson.Properties(json, "ik"))
            {
                animation.Ik[ik.Name] = ReadCurvedList(ik.Value, (frameJson, frame) =>
                {
                    frame.Value1 = SpineJson.GetFloat(frameJson, "mix", 1f);
                    frame.Value2 = SpineJson.GetFloat(frameJson, "softness", 0f);
                    frame.BendPositive = SpineJson.GetBool(frameJson, "bendPositive", true);
                    frame.Compress = SpineJson.GetBool(frameJson, "compress", false);
                    frame.Stretch = SpineJson.GetBool(frameJson, "stretch", false);
                });
            }

            foreach (JProperty transform in SpineJson.Properties(json, "transform"))
            {
                animation.Transform[transform.Name] = ReadCurvedList(transform.Value, (frameJson, frame) =>
                {
                    frame.Value1 = SpineJson.GetFloat(frameJson, "rotateMix", 1f);
                    frame.Value2 = SpineJson.GetFloat(frameJson, "translateMix", 1f);
                    frame.Value3 = frame.Value2;
                    frame.Value4 = SpineJson.GetFloat(frameJson, "scaleMix", 1f);
                    frame.Value5 = frame.Value4;
                    frame.Value6 = SpineJson.GetFloat(frameJson, "shearMix", 1f);
                });
            }

            foreach (JProperty path in SpineJson.Properties(json, "path"))
            {
                SpineTimelineSet timelines = animation.Path.GetOrAdd(path.Name);
                ReadValueTimeline(path.Value, "position", timelines, 1, "position", null, 0f);
                ReadValueTimeline(path.Value, "spacing", timelines, 1, "spacing", null, 0f);
                foreach (JToken frameJson in SpineJson.Items(path.Value, "mix"))
                {
                    float translateMix = SpineJson.GetFloat(frameJson, "translateMix", 1f);
                    var frame = new SpineTimelineFrame
                    {
                        Time = SpineJson.GetFloat(frameJson, "time", 0f),
                        Value1 = SpineJson.GetFloat(frameJson, "rotateMix", 1f),
                        Value2 = translateMix,
                        Value3 = translateMix,
                    };
                    ReadCurve(frameJson, frame);
                    timelines.GetOrAdd("mix").Add(frame);
                }
            }

            // 3.8 은 "deform" 아래에 스킨 → 슬롯 → 어태치먼트 순으로 둔다(4.x 는 "attachments" 아래 "deform").
            foreach (JProperty skin in SpineJson.Properties(json, "deform"))
            {
                foreach (JProperty slot in ((JObject)skin.Value).Properties())
                {
                    foreach (JProperty attachment in ((JObject)slot.Value).Properties())
                    {
                        SpineTimelineSet timelines = animation.Attachments.GetOrAdd(skin.Name).GetOrAdd(slot.Name).GetOrAdd(attachment.Name);
                        timelines["deform"] = ReadCurvedList(attachment.Value, (frameJson, frame) =>
                        {
                            if (SpineJson.Has(frameJson, "vertices") == true)
                            {
                                frame.Int1 = SpineJson.GetInt(frameJson, "offset", 0);
                                frame.Vertices = SpineJson.GetFloats(frameJson, "vertices");
                            }
                        });
                    }
                }
            }

            // 원본은 옛 소문자 키 "draworder" 도 받는다.
            string drawOrderKey = SpineJson.Has(json, "drawOrder") == true ? "drawOrder" : "draworder";
            foreach (JToken frameJson in SpineJson.Items(json, drawOrderKey))
            {
                var frame = new SpineTimelineFrame { Time = SpineJson.GetFloat(frameJson, "time", 0f) };
                foreach (JToken offset in SpineJson.Items(frameJson, "offsets"))
                {
                    frame.Offsets.Add(new KeyValuePair<string, int>((string)offset["slot"], SpineJson.GetInt(offset, "offset", 0)));
                }

                animation.DrawOrder.Add(frame);
            }

            foreach (JToken frameJson in SpineJson.Items(json, "events"))
            {
                string eventName = SpineJson.GetString(frameJson, "name", null);
                SpineEvent eventData = skeleton.Events.Find(candidate => candidate.Name == eventName)
                    ?? throw new SpineConversionException($"애니메이션 '{name}' 이 정의되지 않은 이벤트를 쓴다: '{eventName}'");
                var frame = new SpineTimelineFrame
                {
                    Time = SpineJson.GetFloat(frameJson, "time", 0f),
                    Str1 = eventName,
                    Int1 = SpineJson.GetInt(frameJson, "int", eventData.IntValue),
                    Value1 = SpineJson.GetFloat(frameJson, "float", eventData.FloatValue),
                    Str2 = SpineJson.Has(frameJson, "string") == true ? SpineJson.GetString(frameJson, "string", null) : eventData.StringValue,
                };
                if (eventData.AudioPath != null)
                {
                    frame.Value2 = SpineJson.GetFloat(frameJson, "volume", 1f);
                    frame.Value3 = SpineJson.GetFloat(frameJson, "balance", 0f);
                }

                animation.Events.Add(frame);
            }

            return animation;
        }

        private static SpineColor? ReadColor(JToken json, string key, bool hasAlpha)
        {
            string text = SpineJson.GetString(json, key, null);
            return text != null ? SpineJson.ParseColor(text, hasAlpha) : (SpineColor?)null;
        }

        /// <summary>
        /// 3.x 곡선: <c>"curve": "stepped"</c> 또는 <c>"curve": c1, "c2": c2, "c3": c3, "c4": c4</c>(상대 좌표, 생략값 0·0·1·1).
        /// 모델에는 [c1, c2, c3, c4] 로 그대로 담는다.
        /// </summary>
        private static void ReadCurve(JToken frameJson, SpineTimelineFrame frame)
        {
            JToken curve = SpineJson.Find(frameJson, "curve");
            if (curve == null)
            {
                return;
            }

            if (curve.Type == JTokenType.String && (string)curve == "stepped")
            {
                frame.CurveType = SpineCurveType.Stepped;
                return;
            }

            if (curve.Type != JTokenType.Float && curve.Type != JTokenType.Integer)
            {
                throw new SpineConversionException($"3.8 곡선은 숫자나 \"stepped\" 여야 한다: {curve.ToString(Newtonsoft.Json.Formatting.None)}");
            }

            frame.CurveType = SpineCurveType.Bezier;
            frame.Curve.Add(curve.Value<float>());
            frame.Curve.Add(SpineJson.GetFloat(frameJson, "c2", 0f));
            frame.Curve.Add(SpineJson.GetFloat(frameJson, "c3", 1f));
            frame.Curve.Add(SpineJson.GetFloat(frameJson, "c4", 1f));
        }

        private static List<SpineTimelineFrame> ReadCurvedList(JToken frames, System.Action<JToken, SpineTimelineFrame> readValues)
        {
            var result = new List<SpineTimelineFrame>();
            foreach (JToken frameJson in frames is JArray array ? array : new JArray())
            {
                var frame = new SpineTimelineFrame { Time = SpineJson.GetFloat(frameJson, "time", 0f) };
                readValues(frameJson, frame);
                ReadCurve(frameJson, frame);
                result.Add(frame);
            }

            return result;
        }

        /// <summary>
        /// 원본 readTimeline. 키가 있으면(빈 배열이어도) 타임라인을 만든다 — 원본 라이터가 빈 타임라인을 쓰는 모양까지 같게 하려고
        /// 원본의 «키 존재» 판정을 그대로 따른다.
        /// </summary>
        private static void ReadValueTimeline(JToken owner, string key, SpineTimelineSet timelines, int valueCount, string key1, string key2, float defaultValue)
        {
            if (SpineJson.Has(owner, key) == false)
            {
                return;
            }

            timelines[key] = ReadCurvedList(SpineJson.Find(owner, key), (frameJson, frame) =>
            {
                frame.Value1 = SpineJson.GetFloat(frameJson, key1, defaultValue);
                if (valueCount > 1)
                {
                    frame.Value2 = SpineJson.GetFloat(frameJson, key2, defaultValue);
                }
            });
        }

        /// <summary>
        /// 원본 base64ToUint64(3.8 전용 의미): '=' 로 길이를 4 의 배수로 채운 뒤 관대하게 디코드하고(base64 가 아닌 첫 문자에서 멈춘다),
        /// 결과의 <b>마지막 8바이트</b>를 빅엔디언으로 읽는다. 4.x 의 <see cref="SpineJson.Base64ToHash"/>는 잘못된 문자에서 예외를 던지므로 따로 둔다.
        /// </summary>
        private static ulong Base64ToHash38(string text)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            string padded = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            var bytes = new List<byte>();
            int value = 0;
            int bits = -8;
            foreach (byte character in Encoding.UTF8.GetBytes(padded))
            {
                int digit = alphabet.IndexOf((char)character);
                if (character >= 0x80 || digit < 0)
                {
                    break;
                }

                value = unchecked((value << 6) + digit);
                bits += 6;
                if (bits >= 0)
                {
                    bytes.Add((byte)((value >> bits) & 0xff));
                    bits -= 8;
                }
            }

            int start = System.Math.Max(0, bytes.Count - 8);
            ulong hash = 0;
            for (int i = start; i < bytes.Count; i++)
            {
                hash = (hash << 8) | bytes[i];
            }

            return hash;
        }
    }
}
