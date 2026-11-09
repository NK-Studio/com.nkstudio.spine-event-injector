using System.Collections.Generic;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 3.8 바이너리(.skel) 리더. 원본 SkeletonData38BinaryReader.cpp 를 줄 단위로 옮겼다.
    /// 4.x 와 다른 점: 해시가 base64 문자열, 플래그 바이트 없이 필드를 그대로 나열, 삼각형·엣지가 2바이트 short,
    /// 타임라인마다 베지어 수가 없고 커브는 프레임 뒤에 4값, 이벤트 이름·스킨 이름·링크드 메시 스킨이 문자열 참조.
    /// 원본이 범위 밖 인덱스·알 수 없는 타입 번호를 그대로 진행하는 곳(정의되지 않은 동작 또는 조용한 오독)은 예외로 막는다.
    /// </summary>
    public sealed class Spine38BinaryReader
    {
        // 원본 CurveType: 0 선형, 1 계단, 2 베지어.
        private const int CurveStepped = 1;
        private const int CurveBezier = 2;

        private readonly SpineBinaryInput _input;
        private readonly SpineSkeleton _skeleton = new SpineSkeleton();

        private Spine38BinaryReader(byte[] data)
        {
            _input = new SpineBinaryInput(data);
        }

        public static SpineSkeleton Read(byte[] data)
        {
            return new Spine38BinaryReader(data).ReadSkeleton();
        }

        private SpineSkeleton ReadSkeleton()
        {
            // 3.8 은 해시를 base64 문자열로 쓴다. 원문은 HashString 으로 보존하고, Hash 는 그 마지막 8바이트다.
            _skeleton.HashString = _input.ReadRequiredString("해시");
            _skeleton.Hash = SpineJson.LenientBase64ToHash(_skeleton.HashString);
            _skeleton.Version = _input.ReadRequiredString("버전");

            _skeleton.X = _input.ReadFloat();
            _skeleton.Y = _input.ReadFloat();
            _skeleton.Width = _input.ReadFloat();
            _skeleton.Height = _input.ReadFloat();

            _skeleton.Nonessential = _input.ReadBoolean();
            if (_skeleton.Nonessential == true)
            {
                _skeleton.Fps = _input.ReadFloat();
                _skeleton.ImagesPath = _input.ReadString();
                _skeleton.AudioPath = _input.ReadString();
            }

            var layout = new Spine38EventLayout { StringsStart = _input.Position };
            _skeleton.Layout38 = layout;
            int stringCount = _input.ReadVarint(true);
            layout.StringsBodyStart = _input.Position;
            for (int i = 0; i < stringCount; i++)
            {
                _skeleton.Strings.Add(_input.ReadRequiredString("문자열 테이블 항목"));
            }

            layout.StringsEnd = _input.Position;

            ReadBones();
            ReadSlots();
            ReadIkConstraints();
            ReadTransformConstraints();
            ReadPathConstraints();

            _skeleton.Skins.Add(ReadSkin(true));
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                _skeleton.Skins.Add(ReadSkin(false));
            }

            layout.EventsStart = _input.Position;
            ReadEvents();
            layout.EventsEnd = _input.Position;
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                _skeleton.Animations.Add(ReadAnimation());
            }

            layout.End = _input.Position;

            return _skeleton;
        }

        private void ReadBones()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var bone = new SpineBone { Name = _input.ReadString() };
                if (i != 0)
                {
                    bone.Parent = BoneAt(_input.ReadVarint(true)).Name;
                }

                bone.Rotation = _input.ReadFloat();
                bone.X = _input.ReadFloat();
                bone.Y = _input.ReadFloat();
                bone.ScaleX = _input.ReadFloat();
                bone.ScaleY = _input.ReadFloat();
                bone.ShearX = _input.ReadFloat();
                bone.ShearY = _input.ReadFloat();
                bone.Length = _input.ReadFloat();
                bone.Inherit = (SpineInherit)_input.ReadVarint(true);
                bone.SkinRequired = _input.ReadBoolean();
                if (_skeleton.Nonessential == true)
                {
                    SpineColor color = _input.ReadColor();
                    if (color != SpineColor.DefaultBone)
                    {
                        bone.Color = color;
                    }
                }

                _skeleton.Bones.Add(bone);
            }
        }

        private void ReadSlots()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var slot = new SpineSlot
                {
                    Name = _input.ReadString(),
                    Bone = BoneAt(_input.ReadVarint(true)).Name,
                };

                SpineColor color = _input.ReadColor();
                if (color != SpineColor.White)
                {
                    slot.Color = color;
                }

                // 원본과 의도적으로 다르다: 원본은 3.8 다크 컬러를 r,g,b,a 순서로 읽는데, 3.8 공식 런타임(spine-csharp·libgdx)은
                // int 하나(0x??rrggbb, -1 이면 없음)로 읽는다 — 즉 [미사용][r][g][b] 로 4.x 와 같은 배치다. 원본 순서로 읽으면
                // 3.8 → 3.8 왕복은 대칭이라 멀쩡하지만, 4.x 로 올리거나 JSON 으로 내보내면 성분이 한 칸 밀린다
                // (실측: Spine 3.8 alien-pro 의 eyeball dark — 공식 JSON c57c1e, 원본 변환 00c57c).
                byte a = _input.ReadByte();
                byte r = _input.ReadByte();
                byte g = _input.ReadByte();
                byte b = _input.ReadByte();
                if ((r == 0xff && g == 0xff && b == 0xff && a == 0xff) == false)
                {
                    slot.DarkColor = new SpineColor(r, g, b, a);
                }

                slot.AttachmentName = _input.ReadStringRef(_skeleton.Strings);
                slot.BlendMode = (SpineBlendMode)_input.ReadVarint(true);
                _skeleton.Slots.Add(slot);
            }
        }

        private void ReadIkConstraints()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var ik = new SpineIkConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                    SkinRequired = _input.ReadBoolean(),
                };
                ReadBoneNames(ik.Bones);
                ik.Target = BoneAt(_input.ReadVarint(true)).Name;
                ik.Mix = _input.ReadFloat();
                ik.Softness = _input.ReadFloat();
                ik.BendPositive = _input.ReadSByte() > 0;
                ik.Compress = _input.ReadBoolean();
                ik.Stretch = _input.ReadBoolean();
                ik.Uniform = _input.ReadBoolean();
                _skeleton.IkConstraints.Add(ik);
            }
        }

        private void ReadTransformConstraints()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var transform = new SpineTransformConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                    SkinRequired = _input.ReadBoolean(),
                };
                ReadBoneNames(transform.Bones);
                transform.Target = BoneAt(_input.ReadVarint(true)).Name;
                transform.Local = _input.ReadBoolean();
                transform.Relative = _input.ReadBoolean();
                transform.OffsetRotation = _input.ReadFloat();
                transform.OffsetX = _input.ReadFloat();
                transform.OffsetY = _input.ReadFloat();
                transform.OffsetScaleX = _input.ReadFloat();
                transform.OffsetScaleY = _input.ReadFloat();
                transform.OffsetShearY = _input.ReadFloat();

                // 3.8 은 rotate·translate·scale·shear 믹스 4개뿐이다 — Y 축 믹스는 X 축 값을 따른다.
                transform.MixRotate = _input.ReadFloat();
                transform.MixX = _input.ReadFloat();
                transform.MixY = transform.MixX;
                transform.MixScaleX = _input.ReadFloat();
                transform.MixScaleY = transform.MixScaleX;
                transform.MixShearY = _input.ReadFloat();
                _skeleton.TransformConstraints.Add(transform);
            }
        }

        private void ReadPathConstraints()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var path = new SpinePathConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                    SkinRequired = _input.ReadBoolean(),
                };
                ReadBoneNames(path.Bones);
                path.Target = SlotAt(_input.ReadVarint(true)).Name;
                path.PositionMode = (SpinePositionMode)_input.ReadVarint(true);
                path.SpacingMode = (SpineSpacingMode)_input.ReadVarint(true);
                path.RotateMode = (SpineRotateMode)_input.ReadVarint(true);
                path.OffsetRotation = _input.ReadFloat();
                path.Position = _input.ReadFloat();
                path.Spacing = _input.ReadFloat();
                path.MixRotate = _input.ReadFloat();
                path.MixX = _input.ReadFloat();
                path.MixY = path.MixX;
                _skeleton.PathConstraints.Add(path);
            }
        }

        private SpineSkin ReadSkin(bool isDefaultSkin)
        {
            var skin = new SpineSkin();
            int slotCount;
            if (isDefaultSkin == true)
            {
                slotCount = _input.ReadVarint(true);
                skin.Name = "default";
            }
            else
            {
                skin.Name = _input.ReadRequiredStringRef(_skeleton.Strings, "스킨 이름");
                ReadNames(skin.Bones, index => RequireName(BoneAt(index).Name, "뼈"));
                ReadNames(skin.Ik, index => RequireName(ListAt(_skeleton.IkConstraints, index, "IK 제약조건").Name, "IK 제약조건"));
                ReadNames(skin.Transform, index => RequireName(ListAt(_skeleton.TransformConstraints, index, "transform 제약조건").Name, "transform 제약조건"));
                ReadNames(skin.Path, index => RequireName(ListAt(_skeleton.PathConstraints, index, "path 제약조건").Name, "path 제약조건"));
                slotCount = _input.ReadVarint(true);
            }

            for (int i = 0; i < slotCount; i++)
            {
                string slotName = SlotName(_input.ReadVarint(true));
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string attachmentName = _input.ReadRequiredStringRef(_skeleton.Strings, "어태치먼트 이름");
                    // 원본처럼 어태치먼트가 있을 때만 슬롯 항목을 만든다(std::map operator[] 가 안쪽 루프에 있다).
                    skin.Attachments.GetOrAdd(slotName)[attachmentName] = ReadAttachment(attachmentName);
                }
            }

            return skin;
        }

        private SpineAttachment ReadAttachment(string attachmentName)
        {
            // 원본: 이름 참조가 없거나 빈 문자열이면 플레이스홀더 이름을 쓴다. 경로도 같은 규칙으로 이름을 따른다.
            string name = OrDefault(_input.ReadStringRef(_skeleton.Strings), attachmentName);
            int type = _input.ReadByte();
            switch ((SpineAttachmentType)type)
            {
                case SpineAttachmentType.Region:
                {
                    var region = new SpineRegionAttachment { Name = name };
                    region.Path = OrDefault(_input.ReadStringRef(_skeleton.Strings), name);
                    region.Rotation = _input.ReadFloat();
                    region.X = _input.ReadFloat();
                    region.Y = _input.ReadFloat();
                    region.ScaleX = _input.ReadFloat();
                    region.ScaleY = _input.ReadFloat();
                    region.Width = _input.ReadFloat();
                    region.Height = _input.ReadFloat();
                    region.Color = ColorOrNull(_input.ReadColor());
                    return region;
                }
                case SpineAttachmentType.BoundingBox:
                {
                    var box = new SpineBoundingBoxAttachment { Name = name, Path = name };
                    box.VertexCount = _input.ReadVarint(true);
                    ReadVertices(box.Vertices, box.VertexCount);
                    box.Color = ReadNonessentialColor();
                    return box;
                }
                case SpineAttachmentType.Mesh:
                {
                    var mesh = new SpineMeshAttachment { Name = name };
                    mesh.Path = OrDefault(_input.ReadStringRef(_skeleton.Strings), name);
                    mesh.Color = ColorOrNull(_input.ReadColor());
                    int vertexCount = _input.ReadVarint(true);
                    _input.ReadFloats(vertexCount << 1, mesh.Uvs);
                    ReadShortArray(mesh.Triangles);
                    ReadVertices(mesh.Vertices, vertexCount);
                    mesh.HullLength = _input.ReadVarint(true);
                    if (_skeleton.Nonessential == true)
                    {
                        ReadShortArray(mesh.Edges);
                        mesh.Width = _input.ReadFloat();
                        mesh.Height = _input.ReadFloat();
                    }

                    return mesh;
                }
                case SpineAttachmentType.LinkedMesh:
                {
                    var linkedMesh = new SpineLinkedMeshAttachment { Name = name };
                    linkedMesh.Path = OrDefault(_input.ReadStringRef(_skeleton.Strings), name);
                    linkedMesh.Color = ColorOrNull(_input.ReadColor());

                    // 3.8 은 스킨을 인덱스가 아니라 이름 참조로 쓴다. null 은 기본 스킨(4.2 리더가 푼 Skin 과 같은 의미).
                    linkedMesh.Skin = _input.ReadStringRef(_skeleton.Strings);
                    linkedMesh.ParentMesh = _input.ReadRequiredStringRef(_skeleton.Strings, "링크드 메시 부모");
                    linkedMesh.Timelines = _input.ReadBoolean() == true ? 1 : 0;
                    if (_skeleton.Nonessential == true)
                    {
                        linkedMesh.Width = _input.ReadFloat();
                        linkedMesh.Height = _input.ReadFloat();
                    }

                    return linkedMesh;
                }
                case SpineAttachmentType.Path:
                {
                    var path = new SpinePathAttachment { Name = name, Path = name };
                    path.Closed = _input.ReadBoolean();
                    path.ConstantSpeed = _input.ReadBoolean();
                    path.VertexCount = _input.ReadVarint(true);
                    ReadVertices(path.Vertices, path.VertexCount);
                    _input.ReadFloats(path.VertexCount / 3, path.Lengths);
                    path.Color = ReadNonessentialColor();
                    return path;
                }
                case SpineAttachmentType.Point:
                {
                    var point = new SpinePointAttachment { Name = name, Path = name };
                    point.Rotation = _input.ReadFloat();
                    point.X = _input.ReadFloat();
                    point.Y = _input.ReadFloat();
                    point.Color = ReadNonessentialColor();
                    return point;
                }
                case SpineAttachmentType.Clipping:
                {
                    var clipping = new SpineClippingAttachment { Name = name, Path = name };
                    clipping.EndSlot = SlotAt(_input.ReadVarint(true)).Name;
                    clipping.VertexCount = _input.ReadVarint(true);
                    ReadVertices(clipping.Vertices, clipping.VertexCount);
                    clipping.Color = ReadNonessentialColor();
                    return clipping;
                }
                default:
                    // 원본은 데이터 없이 진행해 이후를 오독한다.
                    throw new SpineConversionException($"알 수 없는 어태치먼트 종류: {type}");
            }
        }

        private SpineColor? ReadNonessentialColor()
        {
            if (_skeleton.Nonessential == false)
            {
                return null;
            }

            return ColorOrNull(_input.ReadColor());
        }

        private static SpineColor? ColorOrNull(SpineColor color)
        {
            return color != SpineColor.White ? color : (SpineColor?)null;
        }

        private static string OrDefault(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) == false ? value : fallback;
        }

        private void ReadVertices(List<float> vertices, int vertexCount)
        {
            if (_input.ReadBoolean() == false)
            {
                _input.ReadFloats(vertexCount << 1, vertices);
                return;
            }

            for (int i = 0; i < vertexCount; i++)
            {
                int boneCount = _input.ReadVarint(true);
                vertices.Add(boneCount);
                for (int ii = 0; ii < boneCount; ii++)
                {
                    vertices.Add(_input.ReadVarint(true));
                    vertices.Add(_input.ReadFloat());
                    vertices.Add(_input.ReadFloat());
                    vertices.Add(_input.ReadFloat());
                }
            }
        }

        /// <summary>3.8 의 short 배열: varint 길이 + 빅엔디언 2바이트 값(4.x 는 값마다 varint).</summary>
        private void ReadShortArray(List<int> into)
        {
            int count = _input.ReadVarint(true);
            if (count < 0 || count > _input.Remaining / 2)
            {
                throw new SpineConversionException($"Spine 바이너리의 short 배열 길이({count})가 잘못됐다.");
            }

            for (int i = 0; i < count; i++)
            {
                int value = _input.ReadByte() << 8;
                value |= _input.ReadByte();
                into.Add(value);
            }
        }

        private void ReadEvents()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var spineEvent = new SpineEvent
                {
                    Name = _input.ReadRequiredStringRef(_skeleton.Strings, "이벤트 이름"),
                    IntValue = _input.ReadVarint(false),
                    FloatValue = _input.ReadFloat(),
                    StringValue = _input.ReadString(),
                    AudioPath = _input.ReadString(),
                };
                if (string.IsNullOrEmpty(spineEvent.AudioPath) == false)
                {
                    spineEvent.Volume = _input.ReadFloat();
                    spineEvent.Balance = _input.ReadFloat();
                }

                _skeleton.Events.Add(spineEvent);
            }
        }

        private SpineAnimation ReadAnimation()
        {
            var animation = new SpineAnimation { Name = _input.ReadRequiredString("애니메이션 이름") };
            ReadSlotTimelines(animation);
            ReadBoneTimelines(animation);
            ReadIkTimelines(animation);
            ReadTransformTimelines(animation);
            ReadPathTimelines(animation);
            ReadDeformTimelines(animation);
            ReadDrawOrderTimeline(animation);
            ReadEventTimeline(animation);
            return animation;
        }

        private void ReadSlotTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string slotName = SlotName(_input.ReadVarint(true));
                var timelines = new SpineTimelineSet();
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    int timelineType = _input.ReadByte();
                    int frameCount = _input.ReadVarint(true);
                    var frames = new List<SpineTimelineFrame>();
                    switch (timelineType)
                    {
                        case 0: // SLOT_ATTACHMENT
                            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                            {
                                frames.Add(new SpineTimelineFrame
                                {
                                    Time = _input.ReadFloat(),
                                    Str1 = _input.ReadStringRef(_skeleton.Strings),
                                });
                            }

                            timelines["attachment"] = frames;
                            break;
                        case 1: // SLOT_COLOR
                            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                            {
                                var frame = new SpineTimelineFrame { Time = _input.ReadFloat(), Color1 = _input.ReadColor() };
                                if (frameIndex < frameCount - 1)
                                {
                                    ReadCurve(frame);
                                }

                                frames.Add(frame);
                            }

                            timelines["rgba"] = frames;
                            break;
                        case 2: // SLOT_TWO_COLOR
                            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                            {
                                var frame = new SpineTimelineFrame { Time = _input.ReadFloat(), Color1 = _input.ReadColor() };

                                // 다크 컬러는 a,r,g,b 순서.
                                byte a = _input.ReadByte();
                                byte r = _input.ReadByte();
                                byte g = _input.ReadByte();
                                byte b = _input.ReadByte();
                                frame.Color2 = new SpineColor(r, g, b, a);
                                if (frameIndex < frameCount - 1)
                                {
                                    ReadCurve(frame);
                                }

                                frames.Add(frame);
                            }

                            timelines["rgba2"] = frames;
                            break;
                        default:
                            // 원본은 프레임을 건너뛰지 않고 진행해 이후를 오독한다.
                            throw new SpineConversionException($"알 수 없는 3.8 슬롯 타임라인 번호: {timelineType}");
                    }
                }

                // 원본은 같은 슬롯이 다시 나오면 덮어쓴다(std::map 대입).
                animation.Slots[slotName] = timelines;
            }
        }

        private void ReadBoneTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string boneName = RequireName(BoneAt(_input.ReadVarint(true)).Name, "뼈");
                var timelines = new SpineTimelineSet();
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    int timelineType = _input.ReadByte();
                    int frameCount = _input.ReadVarint(true);
                    switch (timelineType)
                    {
                        case 0: // BONE_ROTATE
                            timelines["rotate"] = ReadTimeline(frameCount, 1);
                            break;
                        case 1: // BONE_TRANSLATE
                            timelines["translate"] = ReadTimeline(frameCount, 2);
                            break;
                        case 2: // BONE_SCALE
                            timelines["scale"] = ReadTimeline(frameCount, 2);
                            break;
                        case 3: // BONE_SHEAR
                            timelines["shear"] = ReadTimeline(frameCount, 2);
                            break;
                        default:
                            throw new SpineConversionException($"알 수 없는 3.8 뼈 타임라인 번호: {timelineType}");
                    }
                }

                animation.Bones[boneName] = timelines;
            }
        }

        private void ReadIkTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string ikName = RequireName(ListAt(_skeleton.IkConstraints, _input.ReadVarint(true), "IK 제약조건").Name, "IK 제약조건");
                int frameCount = _input.ReadVarint(true);
                var frames = new List<SpineTimelineFrame>();
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    var frame = new SpineTimelineFrame
                    {
                        Time = _input.ReadFloat(),
                        Value1 = _input.ReadFloat(),
                        Value2 = _input.ReadFloat(),
                    };
                    frame.BendPositive = _input.ReadSByte() > 0;
                    frame.Compress = _input.ReadBoolean();
                    frame.Stretch = _input.ReadBoolean();
                    if (frameIndex < frameCount - 1)
                    {
                        ReadCurve(frame);
                    }

                    frames.Add(frame);
                }

                animation.Ik[ikName] = frames;
            }
        }

        private void ReadTransformTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string transformName = RequireName(ListAt(_skeleton.TransformConstraints, _input.ReadVarint(true), "transform 제약조건").Name, "transform 제약조건");
                int frameCount = _input.ReadVarint(true);
                var frames = new List<SpineTimelineFrame>();
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    // 3.8 키: rotate·translate·scale·shear 믹스. 4.x 모델의 Y 축 믹스(value3·value5)는 X 축 값을 따른다.
                    var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                    frame.Value1 = _input.ReadFloat();
                    frame.Value2 = _input.ReadFloat();
                    frame.Value3 = frame.Value2;
                    frame.Value4 = _input.ReadFloat();
                    frame.Value5 = frame.Value4;
                    frame.Value6 = _input.ReadFloat();
                    if (frameIndex < frameCount - 1)
                    {
                        ReadCurve(frame);
                    }

                    frames.Add(frame);
                }

                animation.Transform[transformName] = frames;
            }
        }

        private void ReadPathTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string pathName = RequireName(ListAt(_skeleton.PathConstraints, _input.ReadVarint(true), "path 제약조건").Name, "path 제약조건");
                var timelines = new SpineTimelineSet();
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    int timelineType = _input.ReadSByte();
                    int frameCount = _input.ReadVarint(true);
                    switch (timelineType)
                    {
                        case 0: // PATH_POSITION
                            timelines["position"] = ReadTimeline(frameCount, 1);
                            break;
                        case 1: // PATH_SPACING
                            timelines["spacing"] = ReadTimeline(frameCount, 1);
                            break;
                        case 2: // PATH_MIX
                        {
                            var frames = new List<SpineTimelineFrame>();
                            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                            {
                                // 3.8 mix 키는 rotate·translate 두 값 — 4.x 의 mixY(value3)는 mixX 를 따른다.
                                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                                frame.Value1 = _input.ReadFloat();
                                frame.Value2 = _input.ReadFloat();
                                frame.Value3 = frame.Value2;
                                if (frameIndex < frameCount - 1)
                                {
                                    ReadCurve(frame);
                                }

                                frames.Add(frame);
                            }

                            timelines["mix"] = frames;
                            break;
                        }
                        default:
                            throw new SpineConversionException($"알 수 없는 3.8 path 타임라인 번호: {timelineType}");
                    }
                }

                animation.Path[pathName] = timelines;
            }
        }

        private void ReadDeformTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string skinName = SkinAt(_input.ReadVarint(true)).Name;
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string slotName = SlotName(_input.ReadVarint(true));
                    for (int iii = 0, nnn = _input.ReadVarint(true); iii < nnn; iii++)
                    {
                        string attachmentName = _input.ReadRequiredStringRef(_skeleton.Strings, "어태치먼트 이름");
                        var frames = new List<SpineTimelineFrame>();
                        int frameCount = _input.ReadVarint(true);
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                            int end = _input.ReadVarint(true);
                            if (end != 0)
                            {
                                // 원본: start 를 int1 에, [start, start+end) 범위의 정점 end 개를 읽는다.
                                frame.Int1 = _input.ReadVarint(true);
                                _input.ReadFloats(end, frame.Vertices);
                            }

                            if (frameIndex < frameCount - 1)
                            {
                                ReadCurve(frame);
                            }

                            frames.Add(frame);
                        }

                        // 원본처럼 어태치먼트가 있을 때만 스킨·슬롯 항목이 생긴다.
                        animation.Attachments.GetOrAdd(skinName).GetOrAdd(slotName).GetOrAdd(attachmentName)["deform"] = frames;
                    }
                }
            }
        }

        private void ReadDrawOrderTimeline(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string slotName = SlotName(_input.ReadVarint(true));
                    frame.Offsets.Add(new KeyValuePair<string, int>(slotName, _input.ReadVarint(true)));
                }

                animation.DrawOrder.Add(frame);
            }
        }

        private void ReadEventTimeline(SpineAnimation animation)
        {
            int start = _input.Position;
            ReadEventFrames(animation);
            _skeleton.Layout38.AnimationEvents.Add((start, _input.Position));
        }

        private void ReadEventFrames(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                SpineEvent spineEvent = ListAt(_skeleton.Events, _input.ReadVarint(true), "이벤트");
                frame.Str1 = spineEvent.Name;
                frame.Int1 = _input.ReadVarint(false);
                frame.Value1 = _input.ReadFloat();

                // 3.8 은 "자기 문자열이 있는가" 플래그를 먼저 쓴다. 없으면 이벤트 기본 문자열.
                bool hasFreeString = _input.ReadBoolean();
                frame.HasOwnString = hasFreeString;
                frame.Str2 = hasFreeString == true ? _input.ReadString() : spineEvent.StringValue;
                if (string.IsNullOrEmpty(spineEvent.AudioPath) == false)
                {
                    frame.Value2 = _input.ReadFloat();
                    frame.Value3 = _input.ReadFloat();
                }

                animation.Events.Add(frame);
            }
        }

        /// <summary>원본 readTimeline: [시간, 값1, (값2)] 다음에 마지막 키가 아니면 커브.</summary>
        private List<SpineTimelineFrame> ReadTimeline(int frameCount, int valueCount)
        {
            var frames = new List<SpineTimelineFrame>();
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                frame.Value1 = _input.ReadFloat();
                if (valueCount > 1)
                {
                    frame.Value2 = _input.ReadFloat();
                }

                if (frameIndex < frameCount - 1)
                {
                    ReadCurve(frame);
                }

                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>커브 종류 바이트(1 계단, 2 베지어 + 4값). 원본처럼 그 밖의 값은 선형으로 둔다.</summary>
        private void ReadCurve(SpineTimelineFrame frame)
        {
            switch (_input.ReadByte())
            {
                case CurveStepped:
                    frame.CurveType = SpineCurveType.Stepped;
                    break;
                case CurveBezier:
                    frame.CurveType = SpineCurveType.Bezier;
                    _input.ReadFloats(4, frame.Curve);
                    break;
            }
        }

        private void ReadBoneNames(List<string> into)
        {
            ReadNames(into, index => RequireName(BoneAt(index).Name, "뼈"));
        }

        private void ReadNames(List<string> into, System.Func<int, string> nameAt)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                into.Add(nameAt(_input.ReadVarint(true)));
            }
        }

        private string SlotName(int index)
        {
            return RequireName(SlotAt(index).Name, "슬롯");
        }

        private SpineBone BoneAt(int index)
        {
            return ListAt(_skeleton.Bones, index, "뼈");
        }

        private SpineSlot SlotAt(int index)
        {
            return ListAt(_skeleton.Slots, index, "슬롯");
        }

        private SpineSkin SkinAt(int index)
        {
            return ListAt(SpineSkin.BinaryIndexTable(_skeleton.Skins), index, "스킨");
        }

        /// <summary>원본은 이름이 없는 항목을 이름으로 쓰려 하면 bad_optional_access 로 멈춘다 — 같은 지점에서 막는다.</summary>
        private static string RequireName(string name, string what)
        {
            return name ?? throw new SpineConversionException($"이름이 없는 {what} 을(를) 이름으로 참조했다.");
        }

        private static T ListAt<T>(List<T> list, int index, string what)
        {
            if (index < 0 || index >= list.Count)
            {
                throw new SpineConversionException($"{what} 인덱스({index})가 범위 밖이다(개수 {list.Count}).");
            }

            return list[index];
        }
    }
}
