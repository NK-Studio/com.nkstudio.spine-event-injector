using System;
using System.Collections.Generic;
using System.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 3.8 바이너리(.skel) 라이터. 원본 SkeletonData38BinaryWriter.cpp 를 줄 단위로 옮겼다(+ SkeletonConverter.cpp 의 기본 스킨 정리).
    /// 원본은 이름으로 인덱스를 찾다 못 찾으면 조용히 0(클리핑 끝 슬롯은 -1)을 쓰고, 3.8 에 없는 타임라인을 개수에는 세고 본문은 건너뛰어
    /// 깨진 파일을 낸다. 여기서는 그런 경우를 예외로 막는다 — 정상 입력에서는 원본과 바이트 단위로 같다.
    /// </summary>
    public sealed class Spine38BinaryWriter
    {
        private readonly SpineBinaryOutput _output = new SpineBinaryOutput();
        private readonly SpineSkeleton _skeleton;
        private readonly Action<string> _log;

        private Spine38BinaryWriter(SpineSkeleton skeleton, Action<string> log)
        {
            _skeleton = skeleton;
            _log = log ?? (_ => { });
        }

        /// <summary>모델을 3.8 바이너리로 쓴다. 쓰는 과정에서 모델을 정리한다(기본 스킨 위치, 문자열 테이블).</summary>
        public static byte[] Write(SpineSkeleton skeleton, Action<string> log = null)
        {
            return new Spine38BinaryWriter(skeleton, log).WriteSkeleton();
        }

        private byte[] WriteSkeleton()
        {
            PrepareSkins();

            // 3.8 은 해시를 문자열로 쓴다. 3.8 에서 읽은 원문이 있으면 그대로, 없으면(4.x 에서 옴) 64비트 해시의 base64.
            _output.WriteString(_skeleton.HashString ?? SpineJson.HashToBase64(_skeleton.Hash));
            _output.WriteString(_skeleton.Version);
            _output.WriteFloat(_skeleton.X);
            _output.WriteFloat(_skeleton.Y);
            _output.WriteFloat(_skeleton.Width);
            _output.WriteFloat(_skeleton.Height);
            _output.WriteBoolean(_skeleton.Nonessential);
            if (_skeleton.Nonessential == true)
            {
                _output.WriteFloat(_skeleton.Fps);
                _output.WriteString(_skeleton.ImagesPath);
                _output.WriteString(_skeleton.AudioPath);
            }

            WriteStringTable();
            WriteBones();
            WriteSlots();
            WriteIkConstraints();
            WriteTransformConstraints();
            WritePathConstraints();
            WriteSkins();
            WriteEvents();
            _output.WriteVarint(_skeleton.Animations.Count, true);
            foreach (SpineAnimation animation in _skeleton.Animations)
            {
                WriteAnimation(animation);
            }

            return _output.ToArray();
        }

        /// <summary>원본 writeSkeletonData(4.3 이 아닌 skel): 기본 스킨이 없으면 맨 앞에 넣고, 기본 스킨을 앞으로 안정 분할한다.</summary>
        private void PrepareSkins()
        {
            if (_skeleton.Skins.Any(skin => skin.Name == "default") == false)
            {
                _skeleton.Skins.Insert(0, new SpineSkin { Name = "default" });
            }

            _skeleton.Skins = _skeleton.Skins.Where(skin => skin.Name == "default")
                .Concat(_skeleton.Skins.Where(skin => skin.Name != "default"))
                .ToList();

            // 원본은 기본 스킨이 둘 이상이면 하나만 쓰고 스킨 수는 (전체 - 1) 로 적어 깨진 파일을 낸다.
            if (_skeleton.Skins.Count(skin => skin.Name == "default") > 1)
            {
                throw new SpineConversionException("이름이 'default' 인 스킨이 둘 이상이다.");
            }
        }

        private void WriteStringTable()
        {
            // 원본 std::set<std::string> — UTF-8 바이트 순서.
            var strings = new SortedSet<string>(SpineUtf8Comparer.Instance);
            foreach (SpineSlot slot in _skeleton.Slots)
            {
                if (slot.AttachmentName != null)
                {
                    strings.Add(slot.AttachmentName);
                }
            }

            foreach (SpineSkin skin in _skeleton.Skins)
            {
                if (skin.Name != "default")
                {
                    strings.Add(skin.Name);
                }

                foreach (SpineMap<SpineAttachment> slotAttachments in skin.Attachments.Values)
                {
                    foreach (KeyValuePair<string, SpineAttachment> entry in slotAttachments)
                    {
                        strings.Add(entry.Key);
                        if (entry.Value.Name != entry.Key)
                        {
                            strings.Add(entry.Value.Name);
                        }

                        // XXX : 원본은 경로를 쓰지 않는 종류(boundingbox·path·point·clipping)도 경로가 이름과 다르면 테이블에 넣는다.
                        if (entry.Value.Path != entry.Value.Name)
                        {
                            strings.Add(entry.Value.Path);
                        }
                    }
                }
            }

            // 3.8 은 이벤트 이름도 문자열 참조다.
            foreach (SpineEvent spineEvent in _skeleton.Events)
            {
                strings.Add(spineEvent.Name);
            }

            foreach (SpineAnimation animation in _skeleton.Animations)
            {
                foreach (SpineTimelineSet slotTimelines in animation.Slots.Values)
                {
                    if (slotTimelines.TryGetValue("attachment", out List<SpineTimelineFrame> frames) == false)
                    {
                        continue;
                    }

                    foreach (SpineTimelineFrame frame in frames)
                    {
                        if (frame.Str1 != null)
                        {
                            strings.Add(frame.Str1);
                        }
                    }
                }

                foreach (SpineMap<SpineMap<SpineTimelineSet>> skinTimelines in animation.Attachments.Values)
                {
                    foreach (SpineMap<SpineTimelineSet> slotTimelines in skinTimelines.Values)
                    {
                        foreach (string attachmentName in slotTimelines.Keys)
                        {
                            strings.Add(attachmentName);
                        }
                    }
                }
            }

            // 원본 모델의 이름·경로는 std::string 이라 null 이 없다. C# 모델의 null 은 "참조 없음"(0)으로 쓰이게 테이블에서 뺀다.
            strings.Remove(null);
            _skeleton.Strings = strings.ToList();
            _output.SetStringTable(_skeleton.Strings);
            _output.WriteVarint(_skeleton.Strings.Count, true);
            foreach (string value in _skeleton.Strings)
            {
                _output.WriteString(value);
            }
        }

        private void WriteBones()
        {
            _output.WriteVarint(_skeleton.Bones.Count, true);
            foreach (SpineBone bone in _skeleton.Bones)
            {
                _output.WriteString(bone.Name);
                if (bone.Parent != null)
                {
                    _output.WriteVarint(BoneIndex(bone.Parent), true);
                }

                _output.WriteFloat(bone.Rotation);
                _output.WriteFloat(bone.X);
                _output.WriteFloat(bone.Y);
                _output.WriteFloat(bone.ScaleX);
                _output.WriteFloat(bone.ScaleY);
                _output.WriteFloat(bone.ShearX);
                _output.WriteFloat(bone.ShearY);
                _output.WriteFloat(bone.Length);
                _output.WriteVarint((int)bone.Inherit, true);
                _output.WriteBoolean(bone.SkinRequired);
                if (_skeleton.Nonessential == true)
                {
                    _output.WriteColor(bone.Color ?? SpineColor.DefaultBone);
                }
            }
        }

        private void WriteSlots()
        {
            _output.WriteVarint(_skeleton.Slots.Count, true);
            foreach (SpineSlot slot in _skeleton.Slots)
            {
                _output.WriteString(slot.Name);
                _output.WriteVarint(BoneIndex(slot.Bone), true);
                _output.WriteColor(slot.Color ?? SpineColor.White);

                // 3.8 런타임은 다크 컬러를 int(0x??rrggbb)로 읽는다 — [미사용][r][g][b] 로 쓴다(리더 주석 참고, 원본과 의도적으로 다름).
                if (slot.DarkColor.HasValue == true)
                {
                    SpineColor dark = slot.DarkColor.Value;
                    _output.WriteByte(dark.A);
                    _output.WriteByte(dark.R);
                    _output.WriteByte(dark.G);
                    _output.WriteByte(dark.B);
                }
                else
                {
                    _output.WriteColor(SpineColor.White);
                }
                _output.WriteStringRef(slot.AttachmentName);
                _output.WriteVarint((int)slot.BlendMode, true);
            }
        }

        private void WriteIkConstraints()
        {
            _output.WriteVarint(_skeleton.IkConstraints.Count, true);
            foreach (SpineIkConstraint ik in _skeleton.IkConstraints)
            {
                _output.WriteString(ik.Name);
                _output.WriteVarint(ik.Order, true);
                _output.WriteBoolean(ik.SkinRequired);
                WriteBoneIndices(ik.Bones);
                _output.WriteVarint(BoneIndex(ik.Target), true);
                _output.WriteFloat(ik.Mix);
                _output.WriteFloat(ik.Softness);
                _output.WriteSByte(ik.BendPositive == true ? 1 : -1);
                _output.WriteBoolean(ik.Compress);
                _output.WriteBoolean(ik.Stretch);
                _output.WriteBoolean(ik.Uniform);
            }
        }

        private void WriteTransformConstraints()
        {
            _output.WriteVarint(_skeleton.TransformConstraints.Count, true);
            foreach (SpineTransformConstraint transform in _skeleton.TransformConstraints)
            {
                _output.WriteString(transform.Name);
                _output.WriteVarint(transform.Order, true);
                _output.WriteBoolean(transform.SkinRequired);
                WriteBoneIndices(transform.Bones);
                _output.WriteVarint(BoneIndex(transform.Target), true);
                _output.WriteBoolean(transform.Local);
                _output.WriteBoolean(transform.Relative);
                _output.WriteFloat(transform.OffsetRotation);
                _output.WriteFloat(transform.OffsetX);
                _output.WriteFloat(transform.OffsetY);
                _output.WriteFloat(transform.OffsetScaleX);
                _output.WriteFloat(transform.OffsetScaleY);
                _output.WriteFloat(transform.OffsetShearY);

                // 3.8 은 Y 축 믹스가 없다 — X 축 값만 쓴다(원본도 mixY·mixScaleY 를 버린다).
                _output.WriteFloat(transform.MixRotate);
                _output.WriteFloat(transform.MixX);
                _output.WriteFloat(transform.MixScaleX);
                _output.WriteFloat(transform.MixShearY);
            }
        }

        private void WritePathConstraints()
        {
            _output.WriteVarint(_skeleton.PathConstraints.Count, true);
            foreach (SpinePathConstraint path in _skeleton.PathConstraints)
            {
                _output.WriteString(path.Name);
                _output.WriteVarint(path.Order, true);
                _output.WriteBoolean(path.SkinRequired);
                WriteBoneIndices(path.Bones);
                _output.WriteVarint(SlotIndex(path.Target), true);
                _output.WriteVarint((int)path.PositionMode, true);
                _output.WriteVarint((int)path.SpacingMode, true);
                _output.WriteVarint((int)path.RotateMode, true);
                _output.WriteFloat(path.OffsetRotation);
                _output.WriteFloat(path.Position);
                _output.WriteFloat(path.Spacing);
                _output.WriteFloat(path.MixRotate);
                _output.WriteFloat(path.MixX);
            }
        }

        private void WriteSkins()
        {
            // PrepareSkins 가 기본 스킨을 하나만, 맨 앞에 두었다.
            WriteSkin(_skeleton.Skins[0], true);
            _output.WriteVarint(_skeleton.Skins.Count - 1, true);
            foreach (SpineSkin skin in _skeleton.Skins)
            {
                if (skin.Name != "default")
                {
                    WriteSkin(skin, false);
                }
            }
        }

        private void WriteSkin(SpineSkin skin, bool isDefaultSkin)
        {
            if (isDefaultSkin == false)
            {
                _output.WriteStringRef(skin.Name);
                WriteBoneIndices(skin.Bones);
                WriteIndices(skin.Ik, name => IndexOf(_skeleton.IkConstraints, ik => ik.Name == name, "IK 제약조건", name));
                WriteIndices(skin.Transform, name => IndexOf(_skeleton.TransformConstraints, transform => transform.Name == name, "transform 제약조건", name));
                WriteIndices(skin.Path, name => IndexOf(_skeleton.PathConstraints, path => path.Name == name, "path 제약조건", name));
                if (skin.Physics.Count > 0)
                {
                    // 원본은 3.8 에 없는 physics 목록을 말없이 버린다.
                    _log($"경고: 스킨 '{skin.Name}' 의 physics 제약조건 {skin.Physics.Count}개는 3.8 에 없어 버린다.");
                }
            }

            _output.WriteVarint(skin.Attachments.Count, true);
            foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
            {
                _output.WriteVarint(SlotIndex(slotEntry.Key), true);
                _output.WriteVarint(slotEntry.Value.Count, true);
                foreach (KeyValuePair<string, SpineAttachment> entry in slotEntry.Value)
                {
                    WriteAttachment(entry.Key, entry.Value);
                }
            }
        }

        private void WriteAttachment(string attachmentName, SpineAttachment attachment)
        {
            _output.WriteStringRef(attachmentName);
            _output.WriteStringRef(attachment.Name != attachmentName ? attachment.Name : null);
            _output.WriteByte((int)attachment.Type);
            switch (attachment)
            {
                case SpineRegionAttachment region:
                    WritePath(attachment);
                    _output.WriteFloat(region.Rotation);
                    _output.WriteFloat(region.X);
                    _output.WriteFloat(region.Y);
                    _output.WriteFloat(region.ScaleX);
                    _output.WriteFloat(region.ScaleY);
                    _output.WriteFloat(region.Width);
                    _output.WriteFloat(region.Height);
                    _output.WriteColor(region.Color ?? SpineColor.White);
                    break;
                case SpineBoundingBoxAttachment box:
                    _output.WriteVarint(box.VertexCount, true);
                    WriteVertices(box.Vertices, box.Vertices.Count != box.VertexCount * 2);
                    WriteNonessentialColor(box.Color);
                    break;
                case SpineMeshAttachment mesh:
                {
                    WritePath(attachment);
                    _output.WriteColor(mesh.Color ?? SpineColor.White);
                    int vertexCount = mesh.Uvs.Count / 2;
                    _output.WriteVarint(vertexCount, true);
                    _output.WriteFloats(mesh.Uvs);
                    WriteShortArray(mesh.Triangles);
                    WriteVertices(mesh.Vertices, mesh.Vertices.Count != vertexCount * 2);

                    // 4.2 라이터와 달리 원본 3.8 라이터는 저장된 hull 길이를 그대로 쓴다.
                    _output.WriteVarint(mesh.HullLength, true);
                    if (_skeleton.Nonessential == true)
                    {
                        WriteShortArray(mesh.Edges);
                        _output.WriteFloat(mesh.Width);
                        _output.WriteFloat(mesh.Height);
                    }

                    break;
                }
                case SpineLinkedMeshAttachment linkedMesh:
                    WritePath(attachment);
                    _output.WriteColor(linkedMesh.Color ?? SpineColor.White);

                    // 3.8 은 스킨을 이름 참조로 쓴다(null = 기본 스킨).
                    _output.WriteStringRef(linkedMesh.Skin);
                    _output.WriteStringRef(linkedMesh.ParentMesh);
                    _output.WriteBoolean(linkedMesh.Timelines > 0);
                    if (_skeleton.Nonessential == true)
                    {
                        _output.WriteFloat(linkedMesh.Width);
                        _output.WriteFloat(linkedMesh.Height);
                    }

                    break;
                case SpinePathAttachment path:
                    _output.WriteBoolean(path.Closed);
                    _output.WriteBoolean(path.ConstantSpeed);
                    _output.WriteVarint(path.VertexCount, true);
                    WriteVertices(path.Vertices, path.Vertices.Count != path.VertexCount * 2);
                    _output.WriteFloats(path.Lengths);
                    WriteNonessentialColor(path.Color);
                    break;
                case SpinePointAttachment point:
                    _output.WriteFloat(point.Rotation);
                    _output.WriteFloat(point.X);
                    _output.WriteFloat(point.Y);
                    WriteNonessentialColor(point.Color);
                    break;
                case SpineClippingAttachment clipping:
                    // 원본은 끝 슬롯을 못 찾으면 -1 을 varint 로 쓴다(깨진 파일) — 여기서는 예외.
                    _output.WriteVarint(SlotIndex(clipping.EndSlot), true);
                    _output.WriteVarint(clipping.VertexCount, true);
                    WriteVertices(clipping.Vertices, clipping.Vertices.Count != clipping.VertexCount * 2);
                    WriteNonessentialColor(clipping.Color);
                    break;
                default:
                    throw new SpineConversionException($"3.8 로 쓸 수 없는 어태치먼트: {attachment.GetType().Name}");
            }
        }

        /// <summary>경로가 이름과 같으면 참조 없음(0)을 써서 이름을 따르게 한다.</summary>
        private void WritePath(SpineAttachment attachment)
        {
            _output.WriteStringRef(attachment.Path != attachment.Name ? attachment.Path : null);
        }

        private void WriteNonessentialColor(SpineColor? color)
        {
            if (_skeleton.Nonessential == true)
            {
                _output.WriteColor(color ?? SpineColor.White);
            }
        }

        private void WriteVertices(List<float> vertices, bool isWeighted)
        {
            _output.WriteBoolean(isWeighted);
            if (isWeighted == false)
            {
                _output.WriteFloats(vertices);
                return;
            }

            int index = 0;
            while (index < vertices.Count)
            {
                int boneCount = (int)vertices[index++];
                _output.WriteVarint(boneCount, true);
                for (int i = 0; i < boneCount; i++)
                {
                    if (index + 4 > vertices.Count)
                    {
                        // 원본은 배열 밖을 읽는다(정의되지 않은 동작).
                        throw new SpineConversionException("가중치 정점 배열이 중간에 끊겼다.");
                    }

                    _output.WriteVarint((int)vertices[index++], true);
                    _output.WriteFloat(vertices[index++]);
                    _output.WriteFloat(vertices[index++]);
                    _output.WriteFloat(vertices[index++]);
                }
            }
        }

        /// <summary>3.8 의 short 배열: varint 길이 + 빅엔디언 2바이트 값(원본 배열은 unsigned short 라 16비트로 자른다).</summary>
        private void WriteShortArray(List<int> values)
        {
            _output.WriteVarint(values.Count, true);
            foreach (int value in values)
            {
                int shortValue = value & 0xFFFF;
                _output.WriteByte(shortValue >> 8);
                _output.WriteByte(shortValue & 0xff);
            }
        }

        private void WriteEvents()
        {
            _output.WriteVarint(_skeleton.Events.Count, true);
            foreach (SpineEvent spineEvent in _skeleton.Events)
            {
                _output.WriteStringRef(spineEvent.Name);
                _output.WriteVarint(spineEvent.IntValue, false);
                _output.WriteFloat(spineEvent.FloatValue);
                _output.WriteString(spineEvent.StringValue);
                _output.WriteString(spineEvent.AudioPath);
                if (string.IsNullOrEmpty(spineEvent.AudioPath) == false)
                {
                    _output.WriteFloat(spineEvent.Volume);
                    _output.WriteFloat(spineEvent.Balance);
                }
            }
        }

        private void WriteAnimation(SpineAnimation animation)
        {
            _output.WriteString(animation.Name);
            WriteSlotTimelines(animation);
            WriteBoneTimelines(animation);
            WriteIkTimelines(animation);
            WriteTransformTimelines(animation);
            WritePathTimelines(animation);
            WriteDeformTimelines(animation);
            WriteDrawOrderTimeline(animation);
            WriteEventTimeline(animation);
        }

        private void WriteSlotTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Slots.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Slots)
            {
                _output.WriteVarint(SlotIndex(entry.Key), true);

                // 3.8 엔 알파 단독 타임라인이 없다 — 원본처럼 세지도 쓰지도 않는다.
                int timelineCount = 0;
                foreach (string timelineName in entry.Value.Keys)
                {
                    if (SpineNames.IndexOf(SpineNames.SlotTimeline, timelineName, "슬롯 타임라인") != SlotAlpha)
                    {
                        timelineCount++;
                    }
                }

                _output.WriteVarint(timelineCount, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    List<SpineTimelineFrame> frames = timeline.Value;
                    switch (timeline.Key)
                    {
                        case "alpha":
                            continue;
                        case "attachment":
                            _output.WriteByte(0); // SLOT_ATTACHMENT
                            _output.WriteVarint(frames.Count, true);
                            foreach (SpineTimelineFrame frame in frames)
                            {
                                _output.WriteFloat(frame.Time);
                                _output.WriteStringRef(frame.Str1);
                            }

                            break;
                        case "rgba":
                        case "rgb":
                            _output.WriteByte(1); // SLOT_COLOR
                            _output.WriteVarint(frames.Count, true);
                            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                            {
                                _output.WriteFloat(frames[frameIndex].Time);
                                _output.WriteColor(RequireColor(frames[frameIndex].Color1));
                                if (frameIndex < frames.Count - 1)
                                {
                                    WriteCurve(frames[frameIndex]);
                                }
                            }

                            break;
                        case "rgba2":
                        case "rgb2":
                            _output.WriteByte(2); // SLOT_TWO_COLOR
                            _output.WriteVarint(frames.Count, true);
                            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                            {
                                _output.WriteFloat(frames[frameIndex].Time);
                                _output.WriteColor(RequireColor(frames[frameIndex].Color1));

                                // 다크 컬러는 a,r,g,b 순서.
                                SpineColor dark = RequireColor(frames[frameIndex].Color2);
                                _output.WriteByte(dark.A);
                                _output.WriteByte(dark.R);
                                _output.WriteByte(dark.G);
                                _output.WriteByte(dark.B);
                                if (frameIndex < frames.Count - 1)
                                {
                                    WriteCurve(frames[frameIndex]);
                                }
                            }

                            break;
                    }
                }
            }
        }

        private const int SlotAlpha = 5;

        private void WriteBoneTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Bones.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Bones)
            {
                _output.WriteVarint(BoneIndex(entry.Key), true);

                // 원본은 inherit 타임라인도 개수에 세고 본문은 건너뛰어 깨진 파일을 낸다 — 개수가 맞지 않게 되므로 막는다.
                if (entry.Value.ContainsKey("inherit") == true)
                {
                    throw new SpineConversionException($"뼈 '{entry.Key}' 의 inherit 타임라인은 3.8 바이너리로 쓸 수 없다.");
                }

                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    List<SpineTimelineFrame> frames = timeline.Value;
                    int type;
                    switch (timeline.Key)
                    {
                        case "rotate":
                            type = 0; // BONE_ROTATE
                            break;
                        case "translate":
                        case "translatex":
                        case "translatey":
                            type = 1; // BONE_TRANSLATE
                            break;
                        case "scale":
                        case "scalex":
                        case "scaley":
                            type = 2; // BONE_SCALE
                            break;
                        case "shear":
                        case "shearx":
                        case "sheary":
                            type = 3; // BONE_SHEAR
                            break;
                        default:
                            throw new SpineConversionException($"알 수 없는 뼈 타임라인: '{timeline.Key}'");
                    }

                    _output.WriteByte(type);
                    _output.WriteVarint(frames.Count, true);
                    switch (timeline.Key)
                    {
                        case "rotate":
                            WriteTimeline(frames, 1);
                            break;
                        case "translatex":
                        case "scalex":
                        case "shearx":
                            // 3.8 은 축별 타임라인이 없다 — 다른 축은 0 으로 채운다(원본 그대로).
                            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                            {
                                _output.WriteFloat(frames[frameIndex].Time);
                                _output.WriteFloat(frames[frameIndex].Value1);
                                _output.WriteFloat(0f);
                                if (frameIndex < frames.Count - 1)
                                {
                                    WriteCurve(frames[frameIndex]);
                                }
                            }

                            break;
                        case "translatey":
                        case "scaley":
                        case "sheary":
                            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                            {
                                _output.WriteFloat(frames[frameIndex].Time);
                                _output.WriteFloat(0f);
                                _output.WriteFloat(frames[frameIndex].Value1);
                                if (frameIndex < frames.Count - 1)
                                {
                                    WriteCurve(frames[frameIndex]);
                                }
                            }

                            break;
                        default:
                            WriteTimeline(frames, 2);
                            break;
                    }
                }
            }
        }

        private void WriteIkTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Ik.Count, true);
            foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Ik)
            {
                _output.WriteVarint(IndexOf(_skeleton.IkConstraints, ik => ik.Name == entry.Key, "IK 제약조건", entry.Key), true);
                List<SpineTimelineFrame> frames = entry.Value;
                _output.WriteVarint(frames.Count, true);
                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    SpineTimelineFrame frame = frames[frameIndex];
                    _output.WriteFloat(frame.Time);
                    _output.WriteFloat(frame.Value1);
                    _output.WriteFloat(frame.Value2);
                    _output.WriteSByte(frame.BendPositive == true ? 1 : -1);
                    _output.WriteBoolean(frame.Compress);
                    _output.WriteBoolean(frame.Stretch);
                    if (frameIndex < frames.Count - 1)
                    {
                        WriteCurve(frame);
                    }
                }
            }
        }

        private void WriteTransformTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Transform.Count, true);
            foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Transform)
            {
                _output.WriteVarint(IndexOf(_skeleton.TransformConstraints, transform => transform.Name == entry.Key, "transform 제약조건", entry.Key), true);
                List<SpineTimelineFrame> frames = entry.Value;
                _output.WriteVarint(frames.Count, true);
                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    // 3.8 키: rotate(value1)·translate(value2)·scale(value4)·shear(value6). Y 축 믹스(value3·value5)는 버린다.
                    SpineTimelineFrame frame = frames[frameIndex];
                    _output.WriteFloat(frame.Time);
                    _output.WriteFloat(frame.Value1);
                    _output.WriteFloat(frame.Value2);
                    _output.WriteFloat(frame.Value4);
                    _output.WriteFloat(frame.Value6);
                    if (frameIndex < frames.Count - 1)
                    {
                        WriteCurve(frame);
                    }
                }
            }
        }

        private void WritePathTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Path.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Path)
            {
                _output.WriteVarint(IndexOf(_skeleton.PathConstraints, path => path.Name == entry.Key, "path 제약조건", entry.Key), true);
                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    int type = SpineNames.IndexOf(SpineNames.PathTimeline, timeline.Key, "path 타임라인");
                    _output.WriteSByte(type);
                    _output.WriteVarint(timeline.Value.Count, true);

                    // position·spacing 은 값 1개, mix 는 rotate·translate 2개(4.x 의 mixY 는 버린다).
                    WriteTimeline(timeline.Value, timeline.Key == "mix" ? 2 : 1);
                }
            }
        }

        private void WriteDeformTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Attachments.Count, true);
            foreach (KeyValuePair<string, SpineMap<SpineMap<SpineTimelineSet>>> skinEntry in animation.Attachments)
            {
                _output.WriteVarint(IndexOf(SpineSkin.BinaryIndexTable(_skeleton.Skins), skin => skin.Name == skinEntry.Key, "스킨", skinEntry.Key), true);
                _output.WriteVarint(skinEntry.Value.Count, true);
                foreach (KeyValuePair<string, SpineMap<SpineTimelineSet>> slotEntry in skinEntry.Value)
                {
                    _output.WriteVarint(SlotIndex(slotEntry.Key), true);

                    // 원본은 deform 이 없는(sequence 만 있는) 항목도 개수에 세고 본문은 건너뛰어 깨진 파일을 낸다 — 막는다.
                    foreach (KeyValuePair<string, SpineTimelineSet> attachmentEntry in slotEntry.Value)
                    {
                        if (attachmentEntry.Value.ContainsKey("deform") == false)
                        {
                            throw new SpineConversionException(
                                $"어태치먼트 '{skinEntry.Key}/{slotEntry.Key}/{attachmentEntry.Key}' 에 deform 이 아닌 타임라인(sequence 등)만 있어 3.8 바이너리로 쓸 수 없다.");
                        }
                    }

                    _output.WriteVarint(slotEntry.Value.Count, true);
                    foreach (KeyValuePair<string, SpineTimelineSet> attachmentEntry in slotEntry.Value)
                    {
                        if (attachmentEntry.Value.Count > 1)
                        {
                            _log($"경고: 어태치먼트 '{attachmentEntry.Key}' 의 deform 외 타임라인은 3.8 에 없어 버린다.");
                        }

                        List<SpineTimelineFrame> frames = attachmentEntry.Value["deform"];
                        _output.WriteStringRef(attachmentEntry.Key);
                        _output.WriteVarint(frames.Count, true);
                        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                        {
                            SpineTimelineFrame frame = frames[frameIndex];
                            _output.WriteFloat(frame.Time);
                            _output.WriteVarint(frame.Vertices.Count, true);
                            if (frame.Vertices.Count > 0)
                            {
                                _output.WriteVarint(frame.Int1, true);
                                _output.WriteFloats(frame.Vertices);
                            }

                            if (frameIndex < frames.Count - 1)
                            {
                                WriteCurve(frame);
                            }
                        }
                    }
                }
            }
        }

        private void WriteDrawOrderTimeline(SpineAnimation animation)
        {
            _output.WriteVarint(animation.DrawOrder.Count, true);
            foreach (SpineTimelineFrame frame in animation.DrawOrder)
            {
                _output.WriteFloat(frame.Time);
                _output.WriteVarint(frame.Offsets.Count, true);
                foreach (KeyValuePair<string, int> offset in frame.Offsets)
                {
                    _output.WriteVarint(SlotIndex(offset.Key), true);
                    _output.WriteVarint(offset.Value, true);
                }
            }
        }

        private void WriteEventTimeline(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Events.Count, true);
            foreach (SpineTimelineFrame frame in animation.Events)
            {
                _output.WriteFloat(frame.Time);
                int eventIndex = IndexOf(_skeleton.Events, spineEvent => spineEvent.Name == frame.Str1, "이벤트", frame.Str1);
                SpineEvent eventData = _skeleton.Events[eventIndex];
                _output.WriteVarint(eventIndex, true);
                _output.WriteVarint(frame.Int1, false);
                _output.WriteFloat(frame.Value1);

                // 이벤트 기본 문자열과 다를 때만 "자기 문자열 있음" 플래그와 문자열을 쓴다.
                if (frame.Str2 != eventData.StringValue)
                {
                    _output.WriteBoolean(true);
                    _output.WriteString(frame.Str2);
                }
                else
                {
                    _output.WriteBoolean(false);
                }

                if (string.IsNullOrEmpty(eventData.AudioPath) == false)
                {
                    _output.WriteFloat(frame.Value2);
                    _output.WriteFloat(frame.Value3);
                }
            }
        }

        /// <summary>원본 writeTimeline: [시간, 값1, (값2)] 다음에 마지막 키가 아니면 커브.</summary>
        private void WriteTimeline(List<SpineTimelineFrame> frames, int valueCount)
        {
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                _output.WriteFloat(frames[frameIndex].Time);
                _output.WriteFloat(frames[frameIndex].Value1);
                if (valueCount > 1)
                {
                    _output.WriteFloat(frames[frameIndex].Value2);
                }

                if (frameIndex < frames.Count - 1)
                {
                    WriteCurve(frames[frameIndex]);
                }
            }
        }

        /// <summary>커브 종류 바이트 + 베지어면 앞의 4값(3.8 은 값이 몇 개든 커브 하나).</summary>
        private void WriteCurve(SpineTimelineFrame frame)
        {
            _output.WriteByte((int)frame.CurveType);
            if (frame.CurveType == SpineCurveType.Bezier)
            {
                if (frame.Curve.Count < 4)
                {
                    // 원본은 배열 밖을 읽는다(정의되지 않은 동작).
                    throw new SpineConversionException($"베지어 커브 값이 4개보다 적다({frame.Curve.Count}).");
                }

                _output.WriteFloat(frame.Curve[0]);
                _output.WriteFloat(frame.Curve[1]);
                _output.WriteFloat(frame.Curve[2]);
                _output.WriteFloat(frame.Curve[3]);
            }
        }

        private static SpineColor RequireColor(SpineColor? color)
        {
            return color ?? throw new SpineConversionException("색 타임라인 키에 색이 없다.");
        }

        private void WriteBoneIndices(List<string> boneNames)
        {
            WriteIndices(boneNames, BoneIndex);
        }

        private void WriteIndices(List<string> names, Func<string, int> indexOf)
        {
            _output.WriteVarint(names.Count, true);
            foreach (string name in names)
            {
                _output.WriteVarint(indexOf(name), true);
            }
        }

        private int BoneIndex(string name)
        {
            return IndexOf(_skeleton.Bones, bone => bone.Name != null && bone.Name == name, "뼈", name);
        }

        private int SlotIndex(string name)
        {
            return IndexOf(_skeleton.Slots, slot => slot.Name != null && slot.Name == name, "슬롯", name);
        }

        private static int IndexOf<T>(List<T> list, Predicate<T> match, string what, string name)
        {
            int index = list.FindIndex(match);
            if (index < 0)
            {
                throw new SpineConversionException($"{what} '{name}' 을(를) 찾을 수 없다.");
            }

            return index;
        }
    }
}
