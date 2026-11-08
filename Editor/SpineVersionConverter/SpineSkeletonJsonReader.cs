using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>4.2 JSON 리더(원본 SkeletonData42JsonReader). 4.3 JSON 은 <see cref="Spine43Json"/>이 레거시 모양으로 바꾼 뒤 이 리더를 쓴다.</summary>
    public static class SpineSkeletonJsonReader
    {
        public static SpineSkeleton Read(JObject root)
        {
            var skeleton = new SpineSkeleton();
            JObject header = SpineJson.FindObject(root, "skeleton") ?? new JObject();
            string hash = SpineJson.GetString(header, "hash", null);
            skeleton.Hash = hash != null ? SpineJson.Base64ToHash(hash) : 0;
            skeleton.Version = SpineJson.GetString(header, "spine", null);
            skeleton.X = SpineJson.GetFloat(header, "x", 0f);
            skeleton.Y = SpineJson.GetFloat(header, "y", 0f);
            skeleton.Width = SpineJson.GetFloat(header, "width", 0f);
            skeleton.Height = SpineJson.GetFloat(header, "height", 0f);
            skeleton.ReferenceScale = SpineJson.GetFloat(header, "referenceScale", 100f);
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
                string color = SpineJson.GetString(bone, "color", null);
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
                    Inherit = (SpineInherit)SpineNames.IndexOf(SpineNames.Inherit, SpineJson.GetString(bone, "inherit", "normal"), "inherit"),
                    SkinRequired = SpineJson.GetBool(bone, "skin", false),
                    Color = color != null ? SpineJson.ParseColor(color, true) : (SpineColor?)null,
                    // 원본은 아이콘이 없으면 빈 문자열을 두지만, 바이너리의 "아이콘 없음"은 null 이다.
                    // ""로 두면 JSON 을 거친 바이너리가 1바이트씩 달라지므로 null 로 맞춘다(원본과의 의도된 차이).
                    Icon = SpineJson.GetString(bone, "icon", null),
                    Visible = SpineJson.GetBool(bone, "visible", true),
                });
            }
        }

        private static void ReadSlots(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken slot in SpineJson.Items(root, "slots"))
            {
                string color = SpineJson.GetString(slot, "color", null);
                string dark = SpineJson.GetString(slot, "dark", null);
                skeleton.Slots.Add(new SpineSlot
                {
                    Name = SpineJson.GetString(slot, "name", null),
                    Bone = SpineJson.GetString(slot, "bone", null),
                    Color = color != null ? SpineJson.ParseColor(color, true) : (SpineColor?)null,
                    DarkColor = dark != null ? SpineJson.ParseColor(dark, false) : (SpineColor?)null,
                    AttachmentName = SpineJson.GetString(slot, "attachment", null),
                    BlendMode = (SpineBlendMode)SpineNames.IndexOf(SpineNames.BlendMode, SpineJson.GetString(slot, "blend", "normal"), "blend"),
                    Visible = SpineJson.GetBool(slot, "visible", true),
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
                var constraint = new SpineTransformConstraint
                {
                    Name = SpineJson.GetString(transform, "name", null),
                    Order = SpineJson.GetInt(transform, "order", 0),
                    SkinRequired = SpineJson.GetBool(transform, "skin", false),
                    Bones = SpineJson.GetStrings(transform, "bones"),
                    Target = SpineJson.GetString(transform, "target", null),
                    MixRotate = SpineJson.GetFloat(transform, "mixRotate", 1f),
                    MixX = SpineJson.GetFloat(transform, "mixX", 1f),
                    MixScaleX = SpineJson.GetFloat(transform, "mixScaleX", 1f),
                    MixShearY = SpineJson.GetFloat(transform, "mixShearY", 1f),
                    OffsetRotation = SpineJson.GetFloat(transform, "rotation", 0f),
                    OffsetX = SpineJson.GetFloat(transform, "x", 0f),
                    OffsetY = SpineJson.GetFloat(transform, "y", 0f),
                    OffsetScaleX = SpineJson.GetFloat(transform, "scaleX", 0f),
                    OffsetScaleY = SpineJson.GetFloat(transform, "scaleY", 0f),
                    OffsetShearY = SpineJson.GetFloat(transform, "shearY", 0f),
                    Relative = SpineJson.GetBool(transform, "relative", false),
                    Local = SpineJson.GetBool(transform, "local", false),
                };
                // Y 성분은 생략되면 X 와 같다.
                constraint.MixY = SpineJson.GetFloat(transform, "mixY", constraint.MixX);
                constraint.MixScaleY = SpineJson.GetFloat(transform, "mixScaleY", constraint.MixScaleX);
                skeleton.TransformConstraints.Add(constraint);
            }

            foreach (JToken path in SpineJson.Items(root, "path"))
            {
                var constraint = new SpinePathConstraint
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
                    MixRotate = SpineJson.GetFloat(path, "mixRotate", 1f),
                    MixX = SpineJson.GetFloat(path, "mixX", 1f),
                };
                constraint.MixY = SpineJson.GetFloat(path, "mixY", constraint.MixX);
                skeleton.PathConstraints.Add(constraint);
            }

            foreach (JToken physics in SpineJson.Items(root, "physics"))
            {
                skeleton.PhysicsConstraints.Add(new SpinePhysicsConstraint
                {
                    Name = SpineJson.GetString(physics, "name", null),
                    Order = SpineJson.GetInt(physics, "order", 0),
                    SkinRequired = SpineJson.GetBool(physics, "skin", false),
                    Bone = SpineJson.GetString(physics, "bone", null),
                    X = SpineJson.GetFloat(physics, "x", 0f),
                    Y = SpineJson.GetFloat(physics, "y", 0f),
                    Rotate = SpineJson.GetFloat(physics, "rotate", 0f),
                    ScaleX = SpineJson.GetFloat(physics, "scaleX", 0f),
                    ShearX = SpineJson.GetFloat(physics, "shearX", 0f),
                    Limit = SpineJson.GetFloat(physics, "limit", 5000f),
                    Fps = SpineJson.GetFloat(physics, "fps", 60f),
                    Inertia = SpineJson.GetFloat(physics, "inertia", 1f),
                    Strength = SpineJson.GetFloat(physics, "strength", 100f),
                    Damping = SpineJson.GetFloat(physics, "damping", 1f),
                    Mass = SpineJson.GetFloat(physics, "mass", 1f),
                    Wind = SpineJson.GetFloat(physics, "wind", 0f),
                    Gravity = SpineJson.GetFloat(physics, "gravity", 0f),
                    Mix = SpineJson.GetFloat(physics, "mix", 1f),
                    InertiaGlobal = SpineJson.GetBool(physics, "inertiaGlobal", false),
                    StrengthGlobal = SpineJson.GetBool(physics, "strengthGlobal", false),
                    DampingGlobal = SpineJson.GetBool(physics, "dampingGlobal", false),
                    MassGlobal = SpineJson.GetBool(physics, "massGlobal", false),
                    WindGlobal = SpineJson.GetBool(physics, "windGlobal", false),
                    GravityGlobal = SpineJson.GetBool(physics, "gravityGlobal", false),
                    MixGlobal = SpineJson.GetBool(physics, "mixGlobal", false),
                });
            }
        }

        private static void ReadSkins(JObject root, SpineSkeleton skeleton)
        {
            foreach (JToken skinJson in SpineJson.Items(root, "skins"))
            {
                string color = SpineJson.GetString(skinJson, "color", null);
                var skin = new SpineSkin
                {
                    Name = SpineJson.GetString(skinJson, "name", string.Empty),
                    // 원본은 스킨 색(nonessential)을 읽지 않아 JSON 을 거치면 사라진다 — 바이너리와 맞추려고 읽는다.
                    Color = color != null ? SpineJson.ParseColor(color, true) : (SpineColor?)null,
                    Bones = SpineJson.GetStrings(skinJson, "bones"),
                    Ik = SpineJson.GetStrings(skinJson, "ik"),
                    Transform = SpineJson.GetStrings(skinJson, "transform"),
                    Path = SpineJson.GetStrings(skinJson, "path"),
                    Physics = SpineJson.GetStrings(skinJson, "physics"),
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
            string colorText = SpineJson.GetString(json, "color", null);
            SpineColor? color = colorText != null ? SpineJson.ParseColor(colorText, true) : (SpineColor?)null;
            JToken sequenceJson = SpineJson.Find(json, "sequence");
            SpineSequence sequence = sequenceJson == null ? null : new SpineSequence
            {
                Count = SpineJson.GetInt(sequenceJson, "count", 0),
                Start = SpineJson.GetInt(sequenceJson, "start", 1),
                Digits = SpineJson.GetInt(sequenceJson, "digits", 0),
                SetupIndex = SpineJson.GetInt(sequenceJson, "setupIndex", 0),
            };

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
                        Sequence = sequence,
                    };
                case SpineAttachmentType.Mesh:
                    return new SpineMeshAttachment
                    {
                        Name = name,
                        Path = path,
                        Width = SpineJson.GetFloat(json, "width", 32f),
                        Height = SpineJson.GetFloat(json, "height", 32f),
                        Color = color,
                        Sequence = sequence,
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
                        Sequence = sequence,
                        ParentMesh = SpineJson.GetString(json, "parent", null)
                            ?? throw new SpineConversionException($"링크드 메시 '{name}' 에 parent 가 없다."),
                        Timelines = SpineJson.GetInt(json, "timelines", 1),
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
            foreach (JProperty slot in SpineJson.Properties(json, "slots"))
            {
                SpineTimelineSet timelines = animation.Slots.GetOrAdd(slot.Name);
                AddFrames(slot.Value, "attachment", timelines, frameJson => new SpineTimelineFrame
                {
                    Time = SpineJson.GetFloat(frameJson, "time", 0f),
                    Str1 = SpineJson.GetString(frameJson, "name", null),
                });
                AddCurvedFrames(slot.Value, "rgba", timelines, (frameJson, frame) => frame.Color1 = ReadColor(frameJson, "color", true));
                AddCurvedFrames(slot.Value, "rgb", timelines, (frameJson, frame) => frame.Color1 = ReadColor(frameJson, "color", false));
                AddValueFrames(slot.Value, "alpha", timelines, 1, "value", null, 0f);
                AddCurvedFrames(slot.Value, "rgba2", timelines, (frameJson, frame) =>
                {
                    frame.Color1 = ReadColor(frameJson, "light", true);
                    frame.Color2 = ReadColor(frameJson, "dark", false);
                });
                AddCurvedFrames(slot.Value, "rgb2", timelines, (frameJson, frame) =>
                {
                    frame.Color1 = ReadColor(frameJson, "light", false);
                    frame.Color2 = ReadColor(frameJson, "dark", false);
                });
            }

            foreach (JProperty bone in SpineJson.Properties(json, "bones"))
            {
                SpineTimelineSet timelines = animation.Bones.GetOrAdd(bone.Name);
                AddValueFrames(bone.Value, "rotate", timelines, 1, "value", null, 0f);
                AddValueFrames(bone.Value, "translate", timelines, 2, "x", "y", 0f);
                AddValueFrames(bone.Value, "translatex", timelines, 1, "value", null, 0f);
                AddValueFrames(bone.Value, "translatey", timelines, 1, "value", null, 0f);
                AddValueFrames(bone.Value, "scale", timelines, 2, "x", "y", 1f);
                AddValueFrames(bone.Value, "scalex", timelines, 1, "value", null, 1f);
                AddValueFrames(bone.Value, "scaley", timelines, 1, "value", null, 1f);
                AddValueFrames(bone.Value, "shear", timelines, 2, "x", "y", 0f);
                AddValueFrames(bone.Value, "shearx", timelines, 1, "value", null, 0f);
                AddValueFrames(bone.Value, "sheary", timelines, 1, "value", null, 0f);
                AddFrames(bone.Value, "inherit", timelines, frameJson => new SpineTimelineFrame
                {
                    Time = SpineJson.GetFloat(frameJson, "time", 0f),
                    Inherit = (SpineInherit)SpineNames.IndexOf(SpineNames.Inherit, SpineJson.GetString(frameJson, "inherit", "normal"), "inherit"),
                });
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
                    frame.Value1 = SpineJson.GetFloat(frameJson, "mixRotate", 1f);
                    frame.Value2 = SpineJson.GetFloat(frameJson, "mixX", 1f);
                    frame.Value3 = SpineJson.GetFloat(frameJson, "mixY", frame.Value2);
                    frame.Value4 = SpineJson.GetFloat(frameJson, "mixScaleX", 1f);
                    frame.Value5 = SpineJson.GetFloat(frameJson, "mixScaleY", frame.Value4);
                    frame.Value6 = SpineJson.GetFloat(frameJson, "mixShearY", 1f);
                });
            }

            foreach (JProperty path in SpineJson.Properties(json, "path"))
            {
                SpineTimelineSet timelines = animation.Path.GetOrAdd(path.Name);
                AddValueFrames(path.Value, "position", timelines, 1, "value", null, 0f);
                AddValueFrames(path.Value, "spacing", timelines, 1, "value", null, 0f);
                AddCurvedFrames(path.Value, "mix", timelines, (frameJson, frame) =>
                {
                    frame.Value1 = SpineJson.GetFloat(frameJson, "mixRotate", 1f);
                    frame.Value2 = SpineJson.GetFloat(frameJson, "mixX", 1f);
                    frame.Value3 = SpineJson.GetFloat(frameJson, "mixY", frame.Value2);
                });
            }

            foreach (JProperty physics in SpineJson.Properties(json, "physics"))
            {
                SpineTimelineSet timelines = animation.Physics.GetOrAdd(physics.Name);
                AddFrames(physics.Value, "reset", timelines, frameJson => new SpineTimelineFrame { Time = SpineJson.GetFloat(frameJson, "time", 0f) });
                AddValueFrames(physics.Value, "inertia", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "strength", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "damping", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "mass", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "wind", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "gravity", timelines, 1, "value", null, 0f);
                AddValueFrames(physics.Value, "mix", timelines, 1, "value", null, 1f);
            }

            foreach (JProperty skin in SpineJson.Properties(json, "attachments"))
            {
                foreach (JProperty slot in ((JObject)skin.Value).Properties())
                {
                    foreach (JProperty attachment in ((JObject)slot.Value).Properties())
                    {
                        SpineTimelineSet timelines = animation.Attachments.GetOrAdd(skin.Name).GetOrAdd(slot.Name).GetOrAdd(attachment.Name);
                        AddCurvedFrames(attachment.Value, "deform", timelines, (frameJson, frame) =>
                        {
                            if (SpineJson.Has(frameJson, "vertices") == true)
                            {
                                frame.Int1 = SpineJson.GetInt(frameJson, "offset", 0);
                                frame.Vertices = SpineJson.GetFloats(frameJson, "vertices");
                            }
                        });

                        // 딜레이는 생략되면 직전 키의 값을 이어받는다.
                        float lastDelay = 0f;
                        AddFrames(attachment.Value, "sequence", timelines, frameJson =>
                        {
                            var frame = new SpineTimelineFrame
                            {
                                Time = SpineJson.GetFloat(frameJson, "time", 0f),
                                Value1 = SpineJson.GetFloat(frameJson, "delay", lastDelay),
                                Int1 = SpineJson.GetInt(frameJson, "index", 0),
                                SequenceMode = (SpineSequenceMode)SpineNames.IndexOf(SpineNames.SequenceMode, SpineJson.GetString(frameJson, "mode", "hold"), "sequence mode"),
                            };
                            lastDelay = frame.Value1;
                            return frame;
                        });
                    }
                }
            }

            foreach (JToken frameJson in SpineJson.Items(json, "drawOrder"))
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

        private static SpineColor? ReadColor(JToken frameJson, string key, bool hasAlpha)
        {
            string text = SpineJson.GetString(frameJson, key, null);
            return text != null ? SpineJson.ParseColor(text, hasAlpha) : (SpineColor?)null;
        }

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

            frame.CurveType = SpineCurveType.Bezier;
            foreach (JToken value in curve)
            {
                frame.Curve.Add(value.Value<float>());
            }
        }

        private static List<SpineTimelineFrame> ReadCurvedList(JToken frames, System.Action<JToken, SpineTimelineFrame> readValues)
        {
            var result = new List<SpineTimelineFrame>();
            foreach (JToken frameJson in frames)
            {
                var frame = new SpineTimelineFrame { Time = SpineJson.GetFloat(frameJson, "time", 0f) };
                readValues(frameJson, frame);
                ReadCurve(frameJson, frame);
                result.Add(frame);
            }

            return result;
        }

        private static void AddFrames(JToken owner, string key, SpineTimelineSet timelines, System.Func<JToken, SpineTimelineFrame> readFrame)
        {
            if (SpineJson.Find(owner, key) is JArray frames)
            {
                var result = new List<SpineTimelineFrame>();
                foreach (JToken frameJson in frames)
                {
                    result.Add(readFrame(frameJson));
                }

                timelines[key] = result;
            }
        }

        private static void AddCurvedFrames(JToken owner, string key, SpineTimelineSet timelines, System.Action<JToken, SpineTimelineFrame> readValues)
        {
            if (SpineJson.Find(owner, key) is JArray frames)
            {
                timelines[key] = ReadCurvedList(frames, readValues);
            }
        }

        private static void AddValueFrames(JToken owner, string key, SpineTimelineSet timelines, int valueCount, string key1, string key2, float defaultValue)
        {
            AddCurvedFrames(owner, key, timelines, (frameJson, frame) =>
            {
                frame.Value1 = SpineJson.GetFloat(frameJson, key1, defaultValue);
                if (valueCount > 1)
                {
                    frame.Value2 = SpineJson.GetFloat(frameJson, key2, defaultValue);
                }
            });
        }
    }
}
