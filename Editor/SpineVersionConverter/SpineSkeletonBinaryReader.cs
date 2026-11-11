using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 4.2 / 4.3 바이너리(.skel) 리더. 원본의 SkeletonData42BinaryReader·SkeletonData43BinaryReader 를 하나로 합쳤다 —
    /// 두 파일은 대부분 같고, 4.3 의 차이는 <see cref="_isVersion43"/> 분기로 드러나 있다.
    /// 4.3 차이 요약: 통합 제약조건 배열, 뼈의 inherit 위치·아이콘 크기/회전, 가중치 정점의 뼈 배열 길이,
    /// 메시 timelineSlots, 링크드 메시 소스 슬롯, 포인트 필드 순서, 클리핑 convex/inverse, slider·drawOrderFolder 타임라인, 애니메이션 색.
    /// </summary>
    public sealed class SpineSkeletonBinaryReader
    {
        private readonly SpineBinaryInput _input;
        private readonly bool _isVersion43;
        private readonly SpineSkeleton _skeleton = new SpineSkeleton();

        private SpineSkeletonBinaryReader(byte[] data, SpineVersion version)
        {
            _input = new SpineBinaryInput(data);
            _isVersion43 = version == SpineVersion.Version43;
        }

        public static SpineSkeleton Read(byte[] data, SpineVersion version)
        {
            return new SpineSkeletonBinaryReader(data, version).ReadSkeleton();
        }

        private SpineSkeleton ReadSkeleton()
        {
            // 해시는 하위 4바이트가 먼저 온다.
            uint lowHash = (uint)_input.ReadInt();
            uint highHash = (uint)_input.ReadInt();
            _skeleton.Hash = ((ulong)highHash << 32) | lowHash;
            _skeleton.Version = _input.ReadRequiredString("버전");

            _skeleton.X = _input.ReadFloat();
            _skeleton.Y = _input.ReadFloat();
            _skeleton.Width = _input.ReadFloat();
            _skeleton.Height = _input.ReadFloat();
            _skeleton.ReferenceScale = _input.ReadFloat();
            _skeleton.Nonessential = _input.ReadBoolean();
            if (_skeleton.Nonessential == true)
            {
                _skeleton.Fps = _input.ReadFloat();
                _skeleton.ImagesPath = _input.ReadString();
                _skeleton.AudioPath = _input.ReadString();
            }

            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                _skeleton.Strings.Add(_input.ReadRequiredString("문자열 테이블 항목"));
            }

            ReadBones();
            ReadSlots();
            if (_isVersion43 == true)
            {
                ReadConstraints43();
            }
            else
            {
                ReadLegacyConstraints();
            }

            ReadSkins();
            ReadEvents();
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                _skeleton.Animations.Add(ReadAnimation());
            }

            if (_isVersion43 == true)
            {
                // slider 가 구동하는 애니메이션은 애니메이션 목록을 다 읽은 뒤에야 이름으로 풀 수 있다.
                foreach (JObject constraint in _skeleton.Constraints43)
                {
                    if ((string)constraint["type"] == "slider")
                    {
                        constraint["animation"] = AnimationAt(_input.ReadVarint(true)).Name;
                    }
                }

                if (_input.IsAtEnd == false)
                {
                    throw new SpineConversionException("4.3 바이너리 끝에 해석하지 못한 데이터가 남았다.");
                }
            }

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
                if (_isVersion43 == true)
                {
                    bone.Inherit = (SpineInherit)_input.ReadByte();
                    bone.Length = _input.ReadFloat();
                }
                else
                {
                    bone.Length = _input.ReadFloat();
                    bone.Inherit = (SpineInherit)_input.ReadVarint(true);
                }

                bone.SkinRequired = _input.ReadBoolean();
                if (_skeleton.Nonessential == true)
                {
                    SpineColor color = _input.ReadColor();
                    if (color != SpineColor.DefaultBone)
                    {
                        bone.Color = color;
                    }

                    bone.Icon = _input.ReadString();
                    if (_isVersion43 == true)
                    {
                        bone.IconSize = _input.ReadFloat();
                        bone.IconRotation = _input.ReadFloat();
                    }

                    bone.Visible = _input.ReadBoolean();
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

                // 다크 컬러는 a,r,g,b 순서로 저장된다. 전부 0xff 면 "다크 컬러 없음".
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
                if (_skeleton.Nonessential == true)
                {
                    slot.Visible = _input.ReadBoolean();
                }

                _skeleton.Slots.Add(slot);
            }
        }

        private void ReadLegacyConstraints()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var ik = new SpineIkConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                };
                ReadBoneNames(ik.Bones);
                ik.Target = BoneAt(_input.ReadVarint(true)).Name;
                int flags = _input.ReadByte();
                ik.SkinRequired = (flags & 1) != 0;
                ik.BendPositive = (flags & 2) != 0;
                ik.Compress = (flags & 4) != 0;
                ik.Stretch = (flags & 8) != 0;
                ik.Uniform = (flags & 16) != 0;
                if ((flags & 32) != 0)
                {
                    ik.Mix = (flags & 64) != 0 ? _input.ReadFloat() : 1f;
                }
                else
                {
                    ik.Mix = 0f;
                }

                if ((flags & 128) != 0)
                {
                    ik.Softness = _input.ReadFloat();
                }

                _skeleton.IkConstraints.Add(ik);
            }

            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var transform = new SpineTransformConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                };
                ReadBoneNames(transform.Bones);
                transform.Target = BoneAt(_input.ReadVarint(true)).Name;
                int flags = _input.ReadByte();
                transform.SkinRequired = (flags & 1) != 0;
                transform.Local = (flags & 2) != 0;
                transform.Relative = (flags & 4) != 0;
                transform.OffsetRotation = (flags & 8) != 0 ? _input.ReadFloat() : 0f;
                transform.OffsetX = (flags & 16) != 0 ? _input.ReadFloat() : 0f;
                transform.OffsetY = (flags & 32) != 0 ? _input.ReadFloat() : 0f;
                transform.OffsetScaleX = (flags & 64) != 0 ? _input.ReadFloat() : 0f;
                transform.OffsetScaleY = (flags & 128) != 0 ? _input.ReadFloat() : 0f;
                flags = _input.ReadByte();
                transform.OffsetShearY = (flags & 1) != 0 ? _input.ReadFloat() : 0f;
                transform.MixRotate = (flags & 2) != 0 ? _input.ReadFloat() : 0f;
                transform.MixX = (flags & 4) != 0 ? _input.ReadFloat() : 0f;
                transform.MixY = (flags & 8) != 0 ? _input.ReadFloat() : 0f;
                transform.MixScaleX = (flags & 16) != 0 ? _input.ReadFloat() : 0f;
                transform.MixScaleY = (flags & 32) != 0 ? _input.ReadFloat() : 0f;
                transform.MixShearY = (flags & 64) != 0 ? _input.ReadFloat() : 0f;
                _skeleton.TransformConstraints.Add(transform);
            }

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
                int flags = _input.ReadByte();
                path.PositionMode = (SpinePositionMode)(flags & 1);
                path.SpacingMode = (SpineSpacingMode)((flags >> 1) & 3);
                path.RotateMode = (SpineRotateMode)((flags >> 3) & 3);
                if ((flags & 128) != 0)
                {
                    path.OffsetRotation = _input.ReadFloat();
                }

                path.Position = _input.ReadFloat();
                path.Spacing = _input.ReadFloat();
                path.MixRotate = _input.ReadFloat();
                path.MixX = _input.ReadFloat();
                path.MixY = _input.ReadFloat();
                _skeleton.PathConstraints.Add(path);
            }

            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var physics = new SpinePhysicsConstraint
                {
                    Name = _input.ReadString(),
                    Order = _input.ReadVarint(true),
                    Bone = BoneAt(_input.ReadVarint(true)).Name,
                };
                int flags = _input.ReadByte();
                physics.SkinRequired = (flags & 1) != 0;
                if ((flags & 2) != 0)
                {
                    physics.X = _input.ReadFloat();
                }

                if ((flags & 4) != 0)
                {
                    physics.Y = _input.ReadFloat();
                }

                if ((flags & 8) != 0)
                {
                    physics.Rotate = _input.ReadFloat();
                }

                if ((flags & 16) != 0)
                {
                    physics.ScaleX = _input.ReadFloat();
                }

                if ((flags & 32) != 0)
                {
                    physics.ShearX = _input.ReadFloat();
                }

                physics.Limit = (flags & 64) != 0 ? _input.ReadFloat() : 5000f;
                physics.Fps = _input.ReadByte();
                physics.Inertia = _input.ReadFloat();
                physics.Strength = _input.ReadFloat();
                physics.Damping = _input.ReadFloat();
                physics.Mass = (flags & 128) != 0 ? 1f / _input.ReadFloat() : 1f;
                physics.Wind = _input.ReadFloat();
                physics.Gravity = _input.ReadFloat();
                flags = _input.ReadByte();
                physics.InertiaGlobal = (flags & 1) != 0;
                physics.StrengthGlobal = (flags & 2) != 0;
                physics.DampingGlobal = (flags & 4) != 0;
                physics.MassGlobal = (flags & 8) != 0;
                physics.WindGlobal = (flags & 16) != 0;
                physics.GravityGlobal = (flags & 32) != 0;
                physics.MixGlobal = (flags & 64) != 0;
                physics.Mix = (flags & 128) != 0 ? _input.ReadFloat() : 1f;
                _skeleton.PhysicsConstraints.Add(physics);
            }
        }

        private void ReadConstraints43()
        {
            for (int n = _input.ReadVarint(true); n > 0; n--)
            {
                _skeleton.Constraints43.Add(Spine43Constraints.Read(_input, _skeleton));
            }

            // 다운그레이드가 쓰는 공통 모델(레거시 제약조건 목록)도 채운다 — 4.3 JSON → 레거시 JSON → 4.2 JSON 리더 경로를 그대로 탄다.
            var legacy = new JObject
            {
                ["constraints"] = _skeleton.Constraints43.DeepClone(),
                ["skeleton"] = new JObject { ["spine"] = _skeleton.Version },
                ["skins"] = new JArray(),
            };
            SpineSkeleton constraints = SpineSkeletonJsonReader.Read(Spine43Json.ToLegacyJson(legacy));
            _skeleton.IkConstraints = constraints.IkConstraints;
            _skeleton.TransformConstraints = constraints.TransformConstraints;
            _skeleton.PathConstraints = constraints.PathConstraints;
            _skeleton.PhysicsConstraints = constraints.PhysicsConstraints;
        }

        private void ReadSkins()
        {
            SpineSkin defaultSkin = ReadSkin(true);
            // 4.3 은 어태치먼트가 없는 기본 스킨을 스킨 표에서 빼고 센다.
            if (_isVersion43 == false || defaultSkin.Attachments.Count > 0)
            {
                _skeleton.Skins.Add(defaultSkin);
            }

            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                _skeleton.Skins.Add(ReadSkin(false));
            }

            List<SpineSkin> indexTable = SpineSkin.BinaryIndexTable(_skeleton.Skins);
            foreach (SpineSkin skin in _skeleton.Skins)
            {
                foreach (SpineMap<SpineAttachment> slotAttachments in skin.Attachments.Values)
                {
                    foreach (SpineAttachment attachment in slotAttachments.Values)
                    {
                        if (attachment is SpineLinkedMeshAttachment linkedMesh && linkedMesh.SkinIndex >= 0 && linkedMesh.SkinIndex < indexTable.Count)
                        {
                            linkedMesh.Skin = indexTable[linkedMesh.SkinIndex].Name;
                        }
                    }
                }
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
                skin.Name = _input.ReadRequiredString("스킨 이름");
                if (_skeleton.Nonessential == true)
                {
                    SpineColor color = _input.ReadColor();
                    if (color != SpineColor.White)
                    {
                        skin.Color = color;
                    }
                }

                ReadBoneNames(skin.Bones);
                if (_isVersion43 == true)
                {
                    for (int n = _input.ReadVarint(true); n > 0; n--)
                    {
                        JObject constraint = Spine43Constraints.ConstraintAt(_skeleton, _input.ReadVarint(true));
                        string name = (string)constraint["name"];
                        switch ((string)constraint["type"])
                        {
                            case "ik":
                                skin.Ik.Add(name);
                                break;
                            case "transform":
                                skin.Transform.Add(name);
                                break;
                            case "path":
                                skin.Path.Add(name);
                                break;
                            case "physics":
                                skin.Physics.Add(name);
                                break;
                            case "slider":
                                skin.Slider.Add(name);
                                break;
                        }
                    }
                }
                else
                {
                    ReadNames(skin.Ik, index => ListAt(_skeleton.IkConstraints, index, "IK 제약조건").Name);
                    ReadNames(skin.Transform, index => ListAt(_skeleton.TransformConstraints, index, "transform 제약조건").Name);
                    ReadNames(skin.Path, index => ListAt(_skeleton.PathConstraints, index, "path 제약조건").Name);
                    ReadNames(skin.Physics, index => ListAt(_skeleton.PhysicsConstraints, index, "physics 제약조건").Name);
                }

                slotCount = _input.ReadVarint(true);
            }

            for (int i = 0; i < slotCount; i++)
            {
                string slotName = SlotAt(_input.ReadVarint(true)).Name;
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string attachmentName = _input.ReadRequiredStringRef(_skeleton.Strings, "어태치먼트 이름");
                    // 어태치먼트가 있을 때만 슬롯 항목을 만든다 — 빈 슬롯 항목은 다시 쓸 때 슬롯 수를 바꾼다.
                    skin.Attachments.GetOrAdd(slotName)[attachmentName] = ReadAttachment(attachmentName);
                }
            }

            return skin;
        }

        private SpineAttachment ReadAttachment(string attachmentName)
        {
            int flags = _input.ReadByte();
            string name = (flags & 8) != 0 ? _input.ReadRequiredStringRef(_skeleton.Strings, "어태치먼트 이름") : attachmentName;
            switch ((SpineAttachmentType)(flags & 0x7))
            {
                case SpineAttachmentType.Region:
                {
                    var region = new SpineRegionAttachment { Name = name };
                    region.Path = (flags & 16) != 0 ? _input.ReadRequiredStringRef(_skeleton.Strings, "경로") : name;
                    if ((flags & 32) != 0)
                    {
                        region.Color = _input.ReadColor();
                    }

                    if ((flags & 64) != 0)
                    {
                        region.Sequence = ReadSequence();
                    }

                    if ((flags & 128) != 0)
                    {
                        region.Rotation = _input.ReadFloat();
                    }

                    region.X = _input.ReadFloat();
                    region.Y = _input.ReadFloat();
                    region.ScaleX = _input.ReadFloat();
                    region.ScaleY = _input.ReadFloat();
                    region.Width = _input.ReadFloat();
                    region.Height = _input.ReadFloat();
                    return region;
                }
                case SpineAttachmentType.BoundingBox:
                {
                    var box = new SpineBoundingBoxAttachment { Name = name, Path = name };
                    box.VertexCount = ReadVertices(box.Vertices, (flags & 16) != 0);
                    box.Color = ReadNonessentialColor();
                    return box;
                }
                case SpineAttachmentType.Mesh:
                {
                    var mesh = new SpineMeshAttachment { Name = name };
                    mesh.Path = (flags & 16) != 0 ? _input.ReadRequiredStringRef(_skeleton.Strings, "경로") : name;
                    if ((flags & 32) != 0)
                    {
                        mesh.Color = _input.ReadColor();
                    }

                    if ((flags & 64) != 0)
                    {
                        mesh.Sequence = ReadSequence();
                    }

                    mesh.HullLength = _input.ReadVarint(true);
                    int vertexCount = ReadVertices(mesh.Vertices, (flags & 128) != 0);
                    _input.ReadFloats(vertexCount << 1, mesh.Uvs);
                    _input.ReadShorts((vertexCount * 2 - mesh.HullLength - 2) * 3, mesh.Triangles);
                    if (_isVersion43 == true)
                    {
                        ReadNames(mesh.TimelineSlots, index => SlotAt(index).Name);
                    }

                    if (_skeleton.Nonessential == true)
                    {
                        _input.ReadShorts(_input.ReadVarint(true), mesh.Edges);
                        mesh.Width = _input.ReadFloat();
                        mesh.Height = _input.ReadFloat();
                    }

                    return mesh;
                }
                case SpineAttachmentType.LinkedMesh:
                {
                    var linkedMesh = new SpineLinkedMeshAttachment { Name = name };
                    linkedMesh.Path = (flags & 16) != 0 ? _input.ReadRequiredStringRef(_skeleton.Strings, "경로") : name;
                    if ((flags & 32) != 0)
                    {
                        linkedMesh.Color = _input.ReadColor();
                    }

                    if ((flags & 64) != 0)
                    {
                        linkedMesh.Sequence = ReadSequence();
                    }

                    linkedMesh.Timelines = (flags & 128) != 0 ? 1 : 0;
                    if (_isVersion43 == true)
                    {
                        linkedMesh.SourceSlot = SlotAt(_input.ReadVarint(true)).Name;
                    }

                    linkedMesh.SkinIndex = _input.ReadVarint(true);
                    linkedMesh.ParentMesh = _input.ReadRequiredStringRef(_skeleton.Strings, "링크드 메시 부모");
                    if (_skeleton.Nonessential == true)
                    {
                        linkedMesh.Width = _input.ReadFloat();
                        linkedMesh.Height = _input.ReadFloat();
                    }

                    return linkedMesh;
                }
                case SpineAttachmentType.Path:
                {
                    var path = new SpinePathAttachment
                    {
                        Name = name,
                        Path = name,
                        Closed = (flags & 16) != 0,
                        ConstantSpeed = (flags & 32) != 0,
                    };
                    path.VertexCount = ReadVertices(path.Vertices, (flags & 64) != 0);
                    _input.ReadFloats(path.VertexCount / 3, path.Lengths);
                    path.Color = ReadNonessentialColor();
                    return path;
                }
                case SpineAttachmentType.Point:
                {
                    var point = new SpinePointAttachment { Name = name, Path = name };
                    if (_isVersion43 == true)
                    {
                        point.Rotation = _input.ReadFloat();
                        point.X = _input.ReadFloat();
                        point.Y = _input.ReadFloat();
                    }
                    else
                    {
                        point.X = _input.ReadFloat();
                        point.Y = _input.ReadFloat();
                        point.Rotation = _input.ReadFloat();
                    }

                    point.Color = ReadNonessentialColor();
                    return point;
                }
                case SpineAttachmentType.Clipping:
                {
                    var clipping = new SpineClippingAttachment { Name = name, Path = name };
                    if (_isVersion43 == true)
                    {
                        clipping.Convex = (flags & 32) != 0;
                        clipping.Inverse = (flags & 64) != 0;
                    }

                    clipping.EndSlot = SlotAt(_input.ReadVarint(true)).Name;
                    clipping.VertexCount = ReadVertices(clipping.Vertices, (flags & 16) != 0);
                    clipping.Color = ReadNonessentialColor();
                    return clipping;
                }
                default:
                    throw new SpineConversionException($"알 수 없는 어태치먼트 종류: {flags & 0x7}");
            }
        }

        private SpineColor? ReadNonessentialColor()
        {
            if (_skeleton.Nonessential == false)
            {
                return null;
            }

            SpineColor color = _input.ReadColor();
            return color != SpineColor.White ? color : (SpineColor?)null;
        }

        private SpineSequence ReadSequence()
        {
            return new SpineSequence
            {
                Count = _input.ReadVarint(true),
                Start = _input.ReadVarint(true),
                Digits = _input.ReadVarint(true),
                SetupIndex = _input.ReadVarint(true),
            };
        }

        private int ReadVertices(List<float> vertices, bool isWeighted)
        {
            int vertexCount = _input.ReadVarint(true);
            if (isWeighted == false)
            {
                _input.ReadFloats(vertexCount << 1, vertices);
                return vertexCount;
            }

            if (_isVersion43 == true)
            {
                // 4.3 은 가중치 뼈 배열 길이를 먼저 적는다(런타임 할당용). 모델은 정점에서 다시 계산하므로 버린다.
                _input.ReadVarint(true);
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

            return vertexCount;
        }

        private void ReadEvents()
        {
            _skeleton.BinaryEventsStart = _input.Position;
            ReadEventDefinitions();
            _skeleton.BinaryEventsEnd = _input.Position;
        }

        private void ReadEventDefinitions()
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var spineEvent = new SpineEvent
                {
                    Name = _input.ReadRequiredString("이벤트 이름"),
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
            // 타임라인 총수 — 런타임의 미리 할당용이라 모델에는 필요 없다(이벤트만 바꿔 끼울 때를 위해 위치와 값은 적어 둔다).
            animation.BinaryTimelineCountStart = _input.Position;
            animation.BinaryTimelineCount = _input.ReadVarint(true);
            animation.BinaryTimelineCountEnd = _input.Position;

            ReadSlotTimelines(animation);
            ReadBoneTimelines(animation);
            ReadIkTimelines(animation);
            ReadTransformTimelines(animation);
            ReadPathTimelines(animation);
            ReadPhysicsTimelines(animation);
            if (_isVersion43 == true)
            {
                ReadSliderTimelines(animation);
            }

            ReadAttachmentTimelines(animation);
            ReadDrawOrderTimeline(animation);
            if (_isVersion43 == true)
            {
                ReadDrawOrderFolders(animation);
            }

            animation.BinaryEventsStart = _input.Position;
            ReadEventTimeline(animation);
            animation.BinaryEventsEnd = _input.Position;
            animation.BinaryEventKeyCount = animation.Events.Count;
            if (_isVersion43 == true && _skeleton.Nonessential == true)
            {
                animation.Color = _input.ReadColor();
            }

            return animation;
        }

        private void ReadSlotTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                SpineTimelineSet timelines = animation.Slots.GetOrAdd(SlotAt(_input.ReadVarint(true)).Name);
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string type = SpineNames.NameAt(SpineNames.SlotTimeline, _input.ReadByte(), "슬롯 타임라인");
                    int frameCount = _input.ReadVarint(true);
                    if (type == "attachment")
                    {
                        var frames = new List<SpineTimelineFrame>();
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            frames.Add(new SpineTimelineFrame
                            {
                                Time = _input.ReadFloat(),
                                Str1 = _input.ReadStringRef(_skeleton.Strings),
                            });
                        }

                        timelines[type] = frames;
                        continue;
                    }

                    // 베지어 수 — 런타임 할당용.
                    _input.ReadVarint(true);
                    switch (type)
                    {
                        case "rgba":
                            timelines[type] = ReadKeyedTimeline(frameCount, 4, frame => frame.Color1 = _input.ReadColor());
                            break;
                        case "rgb":
                            timelines[type] = ReadKeyedTimeline(frameCount, 3, frame => frame.Color1 = _input.ReadColor(false));
                            break;
                        case "rgba2":
                            timelines[type] = ReadKeyedTimeline(frameCount, 7, frame =>
                            {
                                frame.Color1 = _input.ReadColor();
                                frame.Color2 = _input.ReadColor(false);
                            });
                            break;
                        case "rgb2":
                            timelines[type] = ReadKeyedTimeline(frameCount, 6, frame =>
                            {
                                frame.Color1 = _input.ReadColor(false);
                                frame.Color2 = _input.ReadColor(false);
                            });
                            break;
                        case "alpha":
                            timelines[type] = ReadKeyedTimeline(frameCount, 1, frame => frame.Value1 = _input.ReadByte() / 255f);
                            break;
                    }
                }
            }
        }

        private void ReadBoneTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                SpineTimelineSet timelines = animation.Bones.GetOrAdd(BoneAt(_input.ReadVarint(true)).Name);
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string type = SpineNames.NameAt(SpineNames.BoneTimeline, _input.ReadByte(), "뼈 타임라인");
                    int frameCount = _input.ReadVarint(true);
                    if (type == "inherit")
                    {
                        var frames = new List<SpineTimelineFrame>();
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            frames.Add(new SpineTimelineFrame
                            {
                                Time = _input.ReadFloat(),
                                Inherit = (SpineInherit)_input.ReadByte(),
                            });
                        }

                        timelines[type] = frames;
                        continue;
                    }

                    _input.ReadVarint(true);
                    bool isTwoValue = type == "translate" || type == "scale" || type == "shear";
                    timelines[type] = ReadValueTimeline(frameCount, isTwoValue == true ? 2 : 1);
                }
            }
        }

        private void ReadIkTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string name = _isVersion43 == true
                    ? Spine43Constraints.ConstraintName(_skeleton, _input.ReadVarint(true))
                    : ListAt(_skeleton.IkConstraints, _input.ReadVarint(true), "IK 제약조건").Name;
                int frameCount = _input.ReadVarint(true);
                _input.ReadVarint(true);

                // IK 는 값마다 플래그 바이트가 붙고, 다음 키의 플래그에 이전 키의 커브 종류(64 계단, 128 베지어)가 실린다.
                var frames = new List<SpineTimelineFrame>();
                int flags = _input.ReadByte();
                SpineTimelineFrame frame = ReadIkKey(flags);
                for (int frameIndex = 0; frameIndex < frameCount - 1; frameIndex++)
                {
                    flags = _input.ReadByte();
                    SpineTimelineFrame next = ReadIkKey(flags);
                    if ((flags & 64) != 0)
                    {
                        frame.CurveType = SpineCurveType.Stepped;
                    }
                    else if ((flags & 128) != 0)
                    {
                        frame.CurveType = SpineCurveType.Bezier;
                        ReadCurve(frame, 2);
                    }

                    frames.Add(frame);
                    frame = next;
                }

                frames.Add(frame);
                animation.Ik[name] = frames;
            }
        }

        private SpineTimelineFrame ReadIkKey(int flags)
        {
            var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
            if ((flags & 1) != 0)
            {
                frame.Value1 = (flags & 2) != 0 ? _input.ReadFloat() : 1f;
            }

            frame.Value2 = (flags & 4) != 0 ? _input.ReadFloat() : 0f;
            frame.BendPositive = (flags & 8) != 0;
            frame.Compress = (flags & 16) != 0;
            frame.Stretch = (flags & 32) != 0;
            return frame;
        }

        private void ReadTransformTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string name = _isVersion43 == true
                    ? Spine43Constraints.ConstraintName(_skeleton, _input.ReadVarint(true))
                    : ListAt(_skeleton.TransformConstraints, _input.ReadVarint(true), "transform 제약조건").Name;
                int frameCount = _input.ReadVarint(true);
                _input.ReadVarint(true);
                animation.Transform[name] = ReadKeyedTimeline(frameCount, 6, frame =>
                {
                    frame.Value1 = _input.ReadFloat();
                    frame.Value2 = _input.ReadFloat();
                    frame.Value3 = _input.ReadFloat();
                    frame.Value4 = _input.ReadFloat();
                    frame.Value5 = _input.ReadFloat();
                    frame.Value6 = _input.ReadFloat();
                });
            }
        }

        private void ReadPathTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string name = _isVersion43 == true
                    ? Spine43Constraints.ConstraintName(_skeleton, _input.ReadVarint(true))
                    : ListAt(_skeleton.PathConstraints, _input.ReadVarint(true), "path 제약조건").Name;
                SpineTimelineSet timelines = animation.Path.GetOrAdd(name);
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string type = SpineNames.NameAt(SpineNames.PathTimeline, _input.ReadByte(), "path 타임라인");
                    int frameCount = _input.ReadVarint(true);
                    _input.ReadVarint(true);
                    timelines[type] = ReadValueTimeline(frameCount, type == "mix" ? 3 : 1);
                }
            }
        }

        private void ReadPhysicsTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                // 0 은 "전체 physics", 그 외는 1-기반 제약조건 인덱스.
                int index = _input.ReadVarint(true) - 1;
                string name = string.Empty;
                if (index >= 0)
                {
                    name = _isVersion43 == true
                        ? Spine43Constraints.ConstraintName(_skeleton, index)
                        : ListAt(_skeleton.PhysicsConstraints, index, "physics 제약조건").Name;
                }

                SpineTimelineSet timelines = animation.Physics.GetOrAdd(name);
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string type = SpineNames.NameAt(SpineNames.PhysicsTimeline, _input.ReadByte(), "physics 타임라인");
                    int frameCount = _input.ReadVarint(true);
                    if (type == "reset")
                    {
                        var frames = new List<SpineTimelineFrame>();
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            frames.Add(new SpineTimelineFrame { Time = _input.ReadFloat() });
                        }

                        timelines[type] = frames;
                        continue;
                    }

                    _input.ReadVarint(true);
                    timelines[type] = ReadValueTimeline(frameCount, 1);
                }
            }
        }

        private void ReadSliderTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string name = Spine43Constraints.ConstraintName(_skeleton, _input.ReadVarint(true));
                for (int k = _input.ReadVarint(true); k > 0; k--)
                {
                    int type = _input.ReadByte();
                    int frameCount = _input.ReadVarint(true);
                    _input.ReadVarint(true);
                    if (type > 1)
                    {
                        throw new SpineConversionException($"알 수 없는 slider 타임라인 번호: {type}");
                    }

                    animation.Slider.GetOrAdd(name)[type == 0 ? "time" : "mix"] = ReadValueTimeline(frameCount, 1);
                }
            }
        }

        private void ReadAttachmentTimelines(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                string skinName = SkinAt(_input.ReadVarint(true)).Name;
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string slotName = SlotAt(_input.ReadVarint(true)).Name;
                    for (int iii = 0, nnn = _input.ReadVarint(true); iii < nnn; iii++)
                    {
                        string attachmentName = _input.ReadRequiredStringRef(_skeleton.Strings, "어태치먼트 이름");
                        string type = SpineNames.NameAt(SpineNames.AttachmentTimeline, _input.ReadByte(), "어태치먼트 타임라인");
                        int frameCount = _input.ReadVarint(true);
                        List<SpineTimelineFrame> frames = type == "deform" ? ReadDeformTimeline(frameCount) : ReadSequenceTimeline(frameCount);
                        animation.Attachments.GetOrAdd(skinName).GetOrAdd(slotName).GetOrAdd(attachmentName)[type] = frames;
                    }
                }
            }
        }

        private List<SpineTimelineFrame> ReadDeformTimeline(int frameCount)
        {
            _input.ReadVarint(true);
            var frames = new List<SpineTimelineFrame>();
            float time = _input.ReadFloat();
            for (int frameIndex = 0; ; frameIndex++)
            {
                var frame = new SpineTimelineFrame { Time = time };
                int end = _input.ReadVarint(true);
                if (end != 0)
                {
                    frame.Int1 = _input.ReadVarint(true);
                    _input.ReadFloats(end, frame.Vertices);
                }

                frames.Add(frame);
                if (frameIndex == frameCount - 1)
                {
                    break;
                }

                time = _input.ReadFloat();
                ReadCurveType(frame, 1);
            }

            return frames;
        }

        private List<SpineTimelineFrame> ReadSequenceTimeline(int frameCount)
        {
            var frames = new List<SpineTimelineFrame>();
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                int modeAndIndex = _input.ReadInt();
                frame.SequenceMode = (SpineSequenceMode)(modeAndIndex & 0xf);
                frame.Int1 = modeAndIndex >> 4;
                frame.Value1 = _input.ReadFloat();
                frames.Add(frame);
            }

            return frames;
        }

        private void ReadDrawOrderTimeline(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                for (int ii = 0, nn = _input.ReadVarint(true); ii < nn; ii++)
                {
                    string slotName = SlotAt(_input.ReadVarint(true)).Name;
                    frame.Offsets.Add(new KeyValuePair<string, int>(slotName, _input.ReadVarint(true)));
                }

                animation.DrawOrder.Add(frame);
            }
        }

        private void ReadDrawOrderFolders(SpineAnimation animation)
        {
            for (int n = _input.ReadVarint(true); n > 0; n--)
            {
                var slots = new JArray();
                for (int k = _input.ReadVarint(true); k > 0; k--)
                {
                    slots.Add(SlotAt(_input.ReadVarint(true)).Name);
                }

                var keys = new JArray();
                for (int k = _input.ReadVarint(true); k > 0; k--)
                {
                    var key = new JObject { ["time"] = SpineJson.Number(_input.ReadFloat()) };
                    var offsets = new JArray();
                    for (int count = _input.ReadVarint(true); count > 0; count--)
                    {
                        int slotIndex = _input.ReadVarint(true);
                        if (slotIndex >= slots.Count)
                        {
                            throw new SpineConversionException($"drawOrderFolder 의 슬롯 인덱스({slotIndex})가 폴더 밖이다.");
                        }

                        offsets.Add(new JObject { ["slot"] = slots[slotIndex].DeepClone(), ["offset"] = _input.ReadVarint(true) });
                    }

                    key["offsets"] = offsets;
                    keys.Add(key);
                }

                animation.DrawOrderFolder.Add(new JObject { ["slots"] = slots, ["keys"] = keys });
            }
        }

        private void ReadEventTimeline(SpineAnimation animation)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
                SpineEvent spineEvent = ListAt(_skeleton.Events, _input.ReadVarint(true), "이벤트");
                frame.Str1 = spineEvent.Name;
                frame.Int1 = _input.ReadVarint(false);
                frame.Value1 = _input.ReadFloat();
                frame.Str2 = _input.ReadString() ?? spineEvent.StringValue;
                if (spineEvent.AudioPath != null)
                {
                    frame.Value2 = _input.ReadFloat();
                    frame.Value3 = _input.ReadFloat();
                }

                animation.Events.Add(frame);
            }
        }

        /// <summary>
        /// 대부분의 키 타임라인 공통 배치: [시간, 값…] 다음 키부터는 [시간, 값…, 커브 종류(+베지어 커브)].
        /// 커브는 "이 키에서 다음 키로 가는" 보간이라 이전 프레임에 붙는다.
        /// </summary>
        private List<SpineTimelineFrame> ReadKeyedTimeline(int frameCount, int curveValueCount, System.Action<SpineTimelineFrame> readValues)
        {
            var frames = new List<SpineTimelineFrame>();
            var frame = new SpineTimelineFrame { Time = _input.ReadFloat() };
            readValues(frame);
            for (int frameIndex = 0; frameIndex < frameCount - 1; frameIndex++)
            {
                var next = new SpineTimelineFrame { Time = _input.ReadFloat() };
                readValues(next);
                ReadCurveType(frame, curveValueCount);
                frames.Add(frame);
                frame = next;
            }

            frames.Add(frame);
            return frames;
        }

        private List<SpineTimelineFrame> ReadValueTimeline(int frameCount, int valueCount)
        {
            return ReadKeyedTimeline(frameCount, valueCount, frame =>
            {
                frame.Value1 = _input.ReadFloat();
                if (valueCount > 1)
                {
                    frame.Value2 = _input.ReadFloat();
                }

                if (valueCount > 2)
                {
                    frame.Value3 = _input.ReadFloat();
                }
            });
        }

        private void ReadCurveType(SpineTimelineFrame frame, int curveValueCount)
        {
            switch ((SpineCurveType)_input.ReadSByte())
            {
                case SpineCurveType.Stepped:
                    frame.CurveType = SpineCurveType.Stepped;
                    break;
                case SpineCurveType.Bezier:
                    frame.CurveType = SpineCurveType.Bezier;
                    ReadCurve(frame, curveValueCount);
                    break;
            }
        }

        private void ReadCurve(SpineTimelineFrame frame, int curveValueCount)
        {
            _input.ReadFloats(curveValueCount * 4, frame.Curve);
        }

        private void ReadBoneNames(List<string> into)
        {
            ReadNames(into, index => BoneAt(index).Name);
        }

        private void ReadNames(List<string> into, System.Func<int, string> nameAt)
        {
            for (int i = 0, n = _input.ReadVarint(true); i < n; i++)
            {
                into.Add(nameAt(_input.ReadVarint(true)));
            }
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

        private SpineAnimation AnimationAt(int index)
        {
            return ListAt(_skeleton.Animations, index, "애니메이션");
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
