using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 4.2 / 4.3 바이너리(.skel) 라이터. 원본 SkeletonData42BinaryWriter·SkeletonData43BinaryWriter 를 합쳤다(차이는 <see cref="_isVersion43"/> 분기).
    /// 원본은 이름으로 인덱스를 찾다 못 찾으면 조용히 0 을 쓰지만, 여기서는 깨진 참조를 그대로 내보내지 않게 예외로 막는다.
    /// </summary>
    public sealed class SpineSkeletonBinaryWriter
    {
        private readonly SpineBinaryOutput _output = new SpineBinaryOutput();
        private readonly SpineSkeleton _skeleton;
        private readonly bool _isVersion43;
        private readonly Action<string> _log;

        private SpineSkeletonBinaryWriter(SpineSkeleton skeleton, SpineVersion version, Action<string> log)
        {
            _skeleton = skeleton;
            _isVersion43 = version == SpineVersion.Version43;
            _log = log ?? (_ => { });
        }

        /// <summary>모델을 바이너리로 쓴다. 쓰는 과정에서 모델을 정리한다(기본 스킨 위치, 문자열 테이블, 4.3 제약조건 등).</summary>
        public static byte[] Write(SpineSkeleton skeleton, SpineVersion version, Action<string> log = null)
        {
            return new SpineSkeletonBinaryWriter(skeleton, version, log).WriteSkeleton();
        }

        private byte[] WriteSkeleton()
        {
            PrepareSkins();
            if (_isVersion43 == true)
            {
                if (_skeleton.Constraints43.Count == 0)
                {
                    // 4.2 에서 온 모델 — 레거시 제약조건을 4.3 JSON 모양의 통합 배열로 옮긴다.
                    JObject json = Spine43Json.FromLegacyJson(SpineSkeletonJsonWriter.Write(_skeleton));
                    _skeleton.Constraints43 = json["constraints"] as JArray ?? new JArray();
                }

                PropagateLinkedMeshTimelineSlots();
            }

            _output.WriteInt((int)(_skeleton.Hash & 0xffffffff));
            _output.WriteInt((int)(_skeleton.Hash >> 32));
            _output.WriteString(_skeleton.Version);
            _output.WriteFloat(_skeleton.X);
            _output.WriteFloat(_skeleton.Y);
            _output.WriteFloat(_skeleton.Width);
            _output.WriteFloat(_skeleton.Height);
            _output.WriteFloat(_skeleton.ReferenceScale);
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
            if (_isVersion43 == true)
            {
                _output.WriteVarint(_skeleton.Constraints43.Count, true);
                foreach (JObject constraint in _skeleton.Constraints43)
                {
                    Spine43Constraints.Write(_output, constraint, _skeleton);
                }
            }
            else
            {
                WriteLegacyConstraints();
            }

            WriteSkins();
            WriteEvents();
            _output.WriteVarint(_skeleton.Animations.Count, true);
            foreach (SpineAnimation animation in _skeleton.Animations)
            {
                WriteAnimation(animation);
            }

            if (_isVersion43 == true)
            {
                foreach (JObject constraint in _skeleton.Constraints43)
                {
                    if ((string)constraint["type"] != "slider")
                    {
                        continue;
                    }

                    string animationName = (string)constraint["animation"];
                    int index = _skeleton.Animations.FindIndex(animation => animation.Name == animationName);
                    if (index < 0)
                    {
                        throw new SpineConversionException($"slider 가 구동하는 애니메이션이 없다: '{animationName}'");
                    }

                    _output.WriteVarint(index, true);
                }
            }

            return _output.ToArray();
        }

        /// <summary>기본 스킨을 맨 앞으로. 4.2 는 기본 스킨이 반드시 있어야 하고, 4.3 은 비어 있는 기본 스킨을 빼고 쓴다.</summary>
        private void PrepareSkins()
        {
            if (_isVersion43 == false && _skeleton.Skins.Any(skin => skin.Name == "default") == false)
            {
                _skeleton.Skins.Insert(0, new SpineSkin { Name = "default" });
            }

            // 안정 분할: 기본 스킨만 앞으로 보내고 나머지 순서는 유지한다.
            List<SpineSkin> ordered = _skeleton.Skins.Where(skin => skin.Name == "default")
                .Concat(_skeleton.Skins.Where(skin => skin.Name != "default"))
                .ToList();
            if (_isVersion43 == true)
            {
                ordered.RemoveAll(skin => skin.Name == "default" && skin.Attachments.Count == 0);
            }

            _skeleton.Skins = ordered;
        }

        /// <summary>
        /// JSON 은 "링크드 메시가 다른 슬롯의 메시를 따르면 디폼도 따른다"를 추론에 맡기지만, 4.3 바이너리는 원본 메시의
        /// timelineSlots 에 그 슬롯을 명시한다. 4.2·JSON 에서 온 모델이면 여기서 채운다.
        /// </summary>
        private void PropagateLinkedMeshTimelineSlots()
        {
            foreach (SpineSkin skin in _skeleton.Skins)
            {
                foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
                {
                    foreach (SpineAttachment attachment in slotEntry.Value.Values)
                    {
                        if (attachment is SpineLinkedMeshAttachment rootLink == false)
                        {
                            continue;
                        }

                        SpineAttachment source = attachment;
                        string sourceSlot = slotEntry.Key;
                        for (int depth = 0; source is SpineLinkedMeshAttachment linked; depth++)
                        {
                            if (depth >= 100)
                            {
                                throw new SpineConversionException("링크드 메시의 원본이 순환한다.");
                            }

                            sourceSlot = linked.SourceSlot ?? sourceSlot;
                            string sourceSkin = linked.Skin ?? "default";
                            SpineSkin owner = _skeleton.Skins.FirstOrDefault(candidate => candidate.Name == sourceSkin)
                                ?? throw new SpineConversionException($"링크드 메시의 원본 스킨이 없다: '{sourceSkin}'");
                            if (owner.Attachments.TryGetValue(sourceSlot, out SpineMap<SpineAttachment> ownerSlot) == false
                                || ownerSlot.TryGetValue(linked.ParentMesh, out source) == false)
                            {
                                throw new SpineConversionException($"링크드 메시의 원본이 없다: {sourceSkin}/{sourceSlot}/{linked.ParentMesh}");
                            }
                        }

                        if (source is SpineMeshAttachment mesh == false)
                        {
                            throw new SpineConversionException($"링크드 메시 '{attachment.Name}' 의 원본이 메시가 아니다.");
                        }

                        bool inheritsTimelines = rootLink.Timelines != 0;
                        if (inheritsTimelines == true && sourceSlot != slotEntry.Key && mesh.TimelineSlots.Contains(slotEntry.Key) == false)
                        {
                            mesh.TimelineSlots.Add(slotEntry.Key);
                        }
                    }
                }
            }
        }

        private void WriteStringTable()
        {
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
                foreach (SpineMap<SpineAttachment> slotAttachments in skin.Attachments.Values)
                {
                    foreach (KeyValuePair<string, SpineAttachment> entry in slotAttachments)
                    {
                        strings.Add(entry.Key);
                        strings.Add(entry.Value.Name);
                        strings.Add(entry.Value.Path);
                        if (entry.Value is SpineLinkedMeshAttachment linkedMesh)
                        {
                            strings.Add(linkedMesh.ParentMesh);
                        }
                    }
                }
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
                if (_isVersion43 == true)
                {
                    _output.WriteByte((int)bone.Inherit);
                    _output.WriteFloat(bone.Length);
                }
                else
                {
                    _output.WriteFloat(bone.Length);
                    _output.WriteVarint((int)bone.Inherit, true);
                }

                _output.WriteBoolean(bone.SkinRequired);
                if (_skeleton.Nonessential == true)
                {
                    _output.WriteColor(bone.Color ?? SpineColor.DefaultBone);
                    _output.WriteString(bone.Icon);
                    if (_isVersion43 == true)
                    {
                        _output.WriteFloat(bone.IconSize);
                        _output.WriteFloat(bone.IconRotation);
                    }

                    _output.WriteBoolean(bone.Visible);
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
                if (_skeleton.Nonessential == true)
                {
                    _output.WriteBoolean(slot.Visible);
                }
            }
        }

        private void WriteLegacyConstraints()
        {
            _output.WriteVarint(_skeleton.IkConstraints.Count, true);
            foreach (SpineIkConstraint ik in _skeleton.IkConstraints)
            {
                _output.WriteString(ik.Name);
                _output.WriteVarint(ik.Order, true);
                WriteBoneIndices(ik.Bones);
                _output.WriteVarint(BoneIndex(ik.Target), true);
                int flags = 0;
                if (ik.SkinRequired == true)
                {
                    flags |= 1;
                }

                if (ik.BendPositive == true)
                {
                    flags |= 2;
                }

                if (ik.Compress == true)
                {
                    flags |= 4;
                }

                if (ik.Stretch == true)
                {
                    flags |= 8;
                }

                if (ik.Uniform == true)
                {
                    flags |= 16;
                }

                if (ik.Mix != 0f)
                {
                    flags |= 32;
                    if (ik.Mix != 1f)
                    {
                        flags |= 64;
                    }
                }

                if (ik.Softness != 0f)
                {
                    flags |= 128;
                }

                _output.WriteByte(flags);
                if ((flags & 64) != 0)
                {
                    _output.WriteFloat(ik.Mix);
                }

                if ((flags & 128) != 0)
                {
                    _output.WriteFloat(ik.Softness);
                }
            }

            _output.WriteVarint(_skeleton.TransformConstraints.Count, true);
            foreach (SpineTransformConstraint transform in _skeleton.TransformConstraints)
            {
                _output.WriteString(transform.Name);
                _output.WriteVarint(transform.Order, true);
                WriteBoneIndices(transform.Bones);
                _output.WriteVarint(BoneIndex(transform.Target), true);

                // 첫 플래그: 0 이 아닌 오프셋만 적는다. 두 번째 플래그: shearY 오프셋과 0 이 아닌 mix 들.
                float[] firstValues = { transform.OffsetRotation, transform.OffsetX, transform.OffsetY, transform.OffsetScaleX, transform.OffsetScaleY };
                int flags = (transform.SkinRequired == true ? 1 : 0) | (transform.Local == true ? 2 : 0) | (transform.Relative == true ? 4 : 0);
                for (int i = 0; i < firstValues.Length; i++)
                {
                    if (firstValues[i] != 0f)
                    {
                        flags |= 8 << i;
                    }
                }

                _output.WriteByte(flags);
                WriteFlaggedFloats(flags, 8, firstValues);

                float[] secondValues = { transform.OffsetShearY, transform.MixRotate, transform.MixX, transform.MixY, transform.MixScaleX, transform.MixScaleY, transform.MixShearY };
                flags = 0;
                for (int i = 0; i < secondValues.Length; i++)
                {
                    if (secondValues[i] != 0f)
                    {
                        flags |= 1 << i;
                    }
                }

                _output.WriteByte(flags);
                WriteFlaggedFloats(flags, 1, secondValues);
            }

            _output.WriteVarint(_skeleton.PathConstraints.Count, true);
            foreach (SpinePathConstraint path in _skeleton.PathConstraints)
            {
                _output.WriteString(path.Name);
                _output.WriteVarint(path.Order, true);
                _output.WriteBoolean(path.SkinRequired);
                WriteBoneIndices(path.Bones);
                _output.WriteVarint(SlotIndex(path.Target), true);
                int flags = ((int)path.PositionMode & 1) | (((int)path.SpacingMode & 3) << 1) | (((int)path.RotateMode & 3) << 3);
                if (path.OffsetRotation != 0f)
                {
                    flags |= 128;
                }

                _output.WriteByte(flags);
                if ((flags & 128) != 0)
                {
                    _output.WriteFloat(path.OffsetRotation);
                }

                _output.WriteFloat(path.Position);
                _output.WriteFloat(path.Spacing);
                _output.WriteFloat(path.MixRotate);
                _output.WriteFloat(path.MixX);
                _output.WriteFloat(path.MixY);
            }

            _output.WriteVarint(_skeleton.PhysicsConstraints.Count, true);
            foreach (SpinePhysicsConstraint physics in _skeleton.PhysicsConstraints)
            {
                _output.WriteString(physics.Name);
                _output.WriteVarint(physics.Order, true);
                _output.WriteVarint(BoneIndex(physics.Bone), true);
                float[] optionalValues = { physics.X, physics.Y, physics.Rotate, physics.ScaleX, physics.ShearX };
                int flags = physics.SkinRequired == true ? 1 : 0;
                for (int i = 0; i < optionalValues.Length; i++)
                {
                    if (optionalValues[i] != 0f)
                    {
                        flags |= 2 << i;
                    }
                }

                if (physics.Limit != 5000f)
                {
                    flags |= 64;
                }

                if (physics.Mass != 1f)
                {
                    flags |= 128;
                }

                _output.WriteByte(flags);
                WriteFlaggedFloats(flags, 2, optionalValues);
                if ((flags & 64) != 0)
                {
                    _output.WriteFloat(physics.Limit);
                }

                _output.WriteByte((int)physics.Fps);
                _output.WriteFloat(physics.Inertia);
                _output.WriteFloat(physics.Strength);
                _output.WriteFloat(physics.Damping);
                if ((flags & 128) != 0)
                {
                    _output.WriteFloat(1f / physics.Mass);
                }

                _output.WriteFloat(physics.Wind);
                _output.WriteFloat(physics.Gravity);
                bool[] globals = { physics.InertiaGlobal, physics.StrengthGlobal, physics.DampingGlobal, physics.MassGlobal, physics.WindGlobal, physics.GravityGlobal, physics.MixGlobal };
                flags = 0;
                for (int i = 0; i < globals.Length; i++)
                {
                    if (globals[i] == true)
                    {
                        flags |= 1 << i;
                    }
                }

                if (physics.Mix != 1f)
                {
                    flags |= 128;
                }

                _output.WriteByte(flags);
                if ((flags & 128) != 0)
                {
                    _output.WriteFloat(physics.Mix);
                }
            }
        }

        /// <summary>values[i] 는 flags 의 (firstBit &lt;&lt; i) 비트가 켜져 있을 때만 쓴다.</summary>
        private void WriteFlaggedFloats(int flags, int firstBit, float[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if ((flags & (firstBit << i)) != 0)
                {
                    _output.WriteFloat(values[i]);
                }
            }
        }

        private void WriteSkins()
        {
            SpineSkin defaultSkin = _skeleton.Skins.FirstOrDefault(skin => skin.Name == "default");
            if (defaultSkin != null)
            {
                WriteSkin(defaultSkin, true);
            }
            else
            {
                // 4.3 에서만 온다(4.2 는 PrepareSkins 가 기본 스킨을 보장).
                _output.WriteVarint(0, true);
            }

            _output.WriteVarint(_skeleton.Skins.Count - (defaultSkin != null ? 1 : 0), true);
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
                _output.WriteString(skin.Name);
                if (_skeleton.Nonessential == true)
                {
                    _output.WriteColor(skin.Color ?? SpineColor.White);
                }

                WriteBoneIndices(skin.Bones);
                if (_isVersion43 == true)
                {
                    List<string> constraints = skin.Ik.Concat(skin.Transform).Concat(skin.Path).Concat(skin.Physics).Concat(skin.Slider).ToList();
                    _output.WriteVarint(constraints.Count, true);
                    foreach (string name in constraints)
                    {
                        _output.WriteVarint(Spine43Constraints.ConstraintIndex(_skeleton, name), true);
                    }
                }
                else
                {
                    WriteIndices(skin.Ik, name => IndexOf(_skeleton.IkConstraints, ik => ik.Name == name, "IK 제약조건", name));
                    WriteIndices(skin.Transform, name => IndexOf(_skeleton.TransformConstraints, transform => transform.Name == name, "transform 제약조건", name));
                    WriteIndices(skin.Path, name => IndexOf(_skeleton.PathConstraints, path => path.Name == name, "path 제약조건", name));
                    WriteIndices(skin.Physics, name => IndexOf(_skeleton.PhysicsConstraints, physics => physics.Name == name, "physics 제약조건", name));
                }
            }

            _output.WriteVarint(skin.Attachments.Count, true);
            foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
            {
                _output.WriteVarint(SlotIndex(slotEntry.Key), true);
                _output.WriteVarint(slotEntry.Value.Count, true);
                foreach (KeyValuePair<string, SpineAttachment> entry in slotEntry.Value)
                {
                    _output.WriteStringRef(entry.Key);
                    WriteAttachment(entry.Key, entry.Value, slotEntry.Key);
                }
            }
        }

        private void WriteAttachment(string attachmentName, SpineAttachment attachment, string slotName)
        {
            int flags = (int)attachment.Type & 0x7;
            if (attachment.Name != attachmentName)
            {
                flags |= 8;
            }

            bool hasCustomPath = attachment.Path != attachment.Name;
            switch (attachment)
            {
                case SpineRegionAttachment region:
                    flags |= (hasCustomPath ? 16 : 0) | (region.Color.HasValue ? 32 : 0) | (region.Sequence != null ? 64 : 0) | (region.Rotation != 0f ? 128 : 0);
                    break;
                case SpineBoundingBoxAttachment box:
                    flags |= box.Vertices.Count != box.VertexCount * 2 ? 16 : 0;
                    break;
                case SpineMeshAttachment mesh:
                    flags |= (hasCustomPath ? 16 : 0) | (mesh.Color.HasValue ? 32 : 0) | (mesh.Sequence != null ? 64 : 0) | (mesh.Vertices.Count != mesh.Uvs.Count ? 128 : 0);
                    break;
                case SpineLinkedMeshAttachment linkedMesh:
                    flags |= (hasCustomPath ? 16 : 0) | (linkedMesh.Color.HasValue ? 32 : 0) | (linkedMesh.Sequence != null ? 64 : 0) | (linkedMesh.Timelines != 0 ? 128 : 0);
                    break;
                case SpinePathAttachment path:
                    flags |= (path.Closed ? 16 : 0) | (path.ConstantSpeed ? 32 : 0) | (path.Vertices.Count != path.VertexCount * 2 ? 64 : 0);
                    break;
                case SpineClippingAttachment clipping:
                    flags |= clipping.Vertices.Count != clipping.VertexCount * 2 ? 16 : 0;
                    if (_isVersion43 == true)
                    {
                        flags |= (clipping.Convex ? 32 : 0) | (clipping.Inverse ? 64 : 0);
                    }

                    break;
            }

            _output.WriteByte(flags);
            if ((flags & 8) != 0)
            {
                _output.WriteStringRef(attachment.Name);
            }

            switch (attachment)
            {
                case SpineRegionAttachment region:
                    WriteSharedHeader(flags, attachment.Path, region.Color, region.Sequence);
                    if ((flags & 128) != 0)
                    {
                        _output.WriteFloat(region.Rotation);
                    }

                    _output.WriteFloat(region.X);
                    _output.WriteFloat(region.Y);
                    _output.WriteFloat(region.ScaleX);
                    _output.WriteFloat(region.ScaleY);
                    _output.WriteFloat(region.Width);
                    _output.WriteFloat(region.Height);
                    break;
                case SpineBoundingBoxAttachment box:
                    WriteVertices(box.Vertices, (flags & 16) != 0);
                    WriteNonessentialColor(box.Color);
                    break;
                case SpineMeshAttachment mesh:
                {
                    WriteSharedHeader(flags, attachment.Path, mesh.Color, mesh.Sequence);

                    // 원본 이슈 #4: 저장된 hull 대신 삼각형 수에서 계산한 값을 써야 Spine 공식 도구가 읽는다.
                    int actualHullLength = mesh.Uvs.Count - mesh.Triangles.Count / 3 - 2;
                    _output.WriteVarint(actualHullLength, true);
                    if (actualHullLength != mesh.HullLength)
                    {
                        _log($"경고: 메시 '{attachment.Name}' 의 hull 길이가 맞지 않아 계산값으로 쓴다(저장 {mesh.HullLength}, 계산 {actualHullLength}).");
                    }

                    WriteVertices(mesh.Vertices, (flags & 128) != 0);
                    _output.WriteFloats(mesh.Uvs);
                    _output.WriteShorts(mesh.Triangles);
                    if (_isVersion43 == true)
                    {
                        _output.WriteVarint(mesh.TimelineSlots.Count, true);
                        foreach (string timelineSlot in mesh.TimelineSlots)
                        {
                            _output.WriteVarint(SlotIndex(timelineSlot), true);
                        }
                    }

                    if (_skeleton.Nonessential == true)
                    {
                        _output.WriteVarint(mesh.Edges.Count, true);
                        _output.WriteShorts(mesh.Edges);
                        _output.WriteFloat(mesh.Width);
                        _output.WriteFloat(mesh.Height);
                    }

                    break;
                }
                case SpineLinkedMeshAttachment linkedMesh:
                {
                    WriteSharedHeader(flags, attachment.Path, linkedMesh.Color, linkedMesh.Sequence);
                    int skinIndex = 0;
                    if (linkedMesh.Skin != null)
                    {
                        skinIndex = IndexOf(SpineSkin.BinaryIndexTable(_skeleton.Skins), skin => skin.Name == linkedMesh.Skin, "스킨", linkedMesh.Skin);
                    }

                    if (_isVersion43 == true)
                    {
                        _output.WriteVarint(SlotIndex(linkedMesh.SourceSlot ?? slotName), true);
                    }

                    _output.WriteVarint(skinIndex, true);
                    _output.WriteStringRef(linkedMesh.ParentMesh);
                    if (_skeleton.Nonessential == true)
                    {
                        _output.WriteFloat(linkedMesh.Width);
                        _output.WriteFloat(linkedMesh.Height);
                    }

                    break;
                }
                case SpinePathAttachment path:
                    WriteVertices(path.Vertices, (flags & 64) != 0);
                    _output.WriteFloats(path.Lengths);
                    WriteNonessentialColor(path.Color);
                    break;
                case SpinePointAttachment point:
                    if (_isVersion43 == true)
                    {
                        _output.WriteFloat(point.Rotation);
                        _output.WriteFloat(point.X);
                        _output.WriteFloat(point.Y);
                    }
                    else
                    {
                        _output.WriteFloat(point.X);
                        _output.WriteFloat(point.Y);
                        _output.WriteFloat(point.Rotation);
                    }

                    WriteNonessentialColor(point.Color);
                    break;
                case SpineClippingAttachment clipping:
                    _output.WriteVarint(SlotIndex(clipping.EndSlot), true);
                    WriteVertices(clipping.Vertices, (flags & 16) != 0);
                    WriteNonessentialColor(clipping.Color);
                    break;
            }
        }

        /// <summary>region·mesh·linkedmesh 공통 머리: 경로(16), 색(32), 시퀀스(64).</summary>
        private void WriteSharedHeader(int flags, string path, SpineColor? color, SpineSequence sequence)
        {
            if ((flags & 16) != 0)
            {
                _output.WriteStringRef(path);
            }

            if ((flags & 32) != 0)
            {
                _output.WriteColor(color.Value);
            }

            if ((flags & 64) != 0)
            {
                _output.WriteVarint(sequence.Count, true);
                _output.WriteVarint(sequence.Start, true);
                _output.WriteVarint(sequence.Digits, true);
                _output.WriteVarint(sequence.SetupIndex, true);
            }
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
            if (isWeighted == false)
            {
                _output.WriteVarint(vertices.Count >> 1, true);
                _output.WriteFloats(vertices);
                return;
            }

            int vertexCount = 0;
            for (int index = 0; index < vertices.Count; vertexCount++)
            {
                int boneCount = (int)vertices[index++];
                index += boneCount * 4;
            }

            _output.WriteVarint(vertexCount, true);
            if (_isVersion43 == true)
            {
                // 뼈 배열 길이 = 정점마다 [뼈 수] 1칸 + 뼈마다 [인덱스] 1칸.
                _output.WriteVarint(vertexCount + (vertices.Count - vertexCount) / 4, true);
            }

            for (int index = 0, i = 0; i < vertexCount; i++)
            {
                int boneCount = (int)vertices[index++];
                _output.WriteVarint(boneCount, true);
                for (int ii = 0; ii < boneCount; ii++)
                {
                    _output.WriteVarint((int)vertices[index++], true);
                    _output.WriteFloat(vertices[index++]);
                    _output.WriteFloat(vertices[index++]);
                    _output.WriteFloat(vertices[index++]);
                }
            }
        }

        private void WriteEvents()
        {
            WriteEventDefinitions(_output, _skeleton.Events);
        }

        /// <summary>이벤트 정의 블록(개수 + 정의들). 4.2·4.3 공통.</summary>
        public static void WriteEventDefinitions(SpineBinaryOutput output, List<SpineEvent> events)
        {
            output.WriteVarint(events.Count, true);
            foreach (SpineEvent spineEvent in events)
            {
                output.WriteString(spineEvent.Name);
                output.WriteVarint(spineEvent.IntValue, false);
                output.WriteFloat(spineEvent.FloatValue);
                output.WriteString(spineEvent.StringValue);
                output.WriteString(spineEvent.AudioPath);
                if (string.IsNullOrEmpty(spineEvent.AudioPath) == false)
                {
                    output.WriteFloat(spineEvent.Volume);
                    output.WriteFloat(spineEvent.Balance);
                }
            }
        }

        /// <summary>한 애니메이션의 이벤트 타임라인 블록(키 개수 + 키들). 4.2·4.3 공통.</summary>
        public static void WriteEventTimeline(SpineBinaryOutput output, List<SpineEvent> events, List<SpineTimelineFrame> frames)
        {
            output.WriteVarint(frames.Count, true);
            foreach (SpineTimelineFrame frame in frames)
            {
                output.WriteFloat(frame.Time);
                int eventIndex = IndexOf(events, spineEvent => spineEvent.Name == frame.Str1, "이벤트", frame.Str1);
                SpineEvent eventData = events[eventIndex];
                output.WriteVarint(eventIndex, true);
                output.WriteVarint(frame.Int1, false);
                output.WriteFloat(frame.Value1);
                // 이벤트 기본 문자열과 같으면 null 을 써서 기본값을 따르게 한다.
                output.WriteString(frame.Str2 != eventData.StringValue ? frame.Str2 : null);
                if (eventData.AudioPath != null)
                {
                    output.WriteFloat(frame.Value2);
                    output.WriteFloat(frame.Value3);
                }
            }
        }

        private void WriteAnimation(SpineAnimation animation)
        {
            _output.WriteString(animation.Name);
            // 타임라인 총수는 런타임의 미리 할당 힌트일 뿐이라 원본처럼 0 을 쓴다.
            _output.WriteVarint(0, true);

            WriteSlotTimelines(animation);
            WriteBoneTimelines(animation);

            _output.WriteVarint(animation.Ik.Count, true);
            foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Ik)
            {
                int index = _isVersion43 == true
                    ? Spine43Constraints.ConstraintIndex(_skeleton, entry.Key)
                    : IndexOf(_skeleton.IkConstraints, ik => ik.Name == entry.Key, "IK 제약조건", entry.Key);
                WriteIkTimeline(index, entry.Value);
            }

            _output.WriteVarint(animation.Transform.Count, true);
            foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Transform)
            {
                int index = _isVersion43 == true
                    ? Spine43Constraints.ConstraintIndex(_skeleton, entry.Key)
                    : IndexOf(_skeleton.TransformConstraints, transform => transform.Name == entry.Key, "transform 제약조건", entry.Key);
                List<SpineTimelineFrame> frames = entry.Value;
                _output.WriteVarint(index, true);
                _output.WriteVarint(frames.Count, true);
                _output.WriteVarint(frames.Count * 6, true);
                WriteKeyedTimeline(frames, frame =>
                {
                    _output.WriteFloat(frame.Value1);
                    _output.WriteFloat(frame.Value2);
                    _output.WriteFloat(frame.Value3);
                    _output.WriteFloat(frame.Value4);
                    _output.WriteFloat(frame.Value5);
                    _output.WriteFloat(frame.Value6);
                });
            }

            _output.WriteVarint(animation.Path.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Path)
            {
                int index = _isVersion43 == true
                    ? Spine43Constraints.ConstraintIndex(_skeleton, entry.Key)
                    : IndexOf(_skeleton.PathConstraints, path => path.Name == entry.Key, "path 제약조건", entry.Key);
                _output.WriteVarint(index, true);
                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    _output.WriteByte(SpineNames.IndexOf(SpineNames.PathTimeline, timeline.Key, "path 타임라인"));
                    _output.WriteVarint(timeline.Value.Count, true);
                    int valueCount = timeline.Key == "mix" ? 3 : 1;
                    _output.WriteVarint(timeline.Value.Count * valueCount, true);
                    WriteValueTimeline(timeline.Value, valueCount);
                }
            }

            _output.WriteVarint(animation.Physics.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Physics)
            {
                int index = -1;
                if (entry.Key.Length > 0)
                {
                    index = _isVersion43 == true
                        ? Spine43Constraints.ConstraintIndex(_skeleton, entry.Key)
                        : IndexOf(_skeleton.PhysicsConstraints, physics => physics.Name == entry.Key, "physics 제약조건", entry.Key);
                }

                _output.WriteVarint(index + 1, true);
                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    _output.WriteByte(SpineNames.IndexOf(SpineNames.PhysicsTimeline, timeline.Key, "physics 타임라인"));
                    _output.WriteVarint(timeline.Value.Count, true);
                    if (timeline.Key == "reset")
                    {
                        foreach (SpineTimelineFrame frame in timeline.Value)
                        {
                            _output.WriteFloat(frame.Time);
                        }

                        continue;
                    }

                    _output.WriteVarint(timeline.Value.Count, true);
                    WriteValueTimeline(timeline.Value, 1);
                }
            }

            if (_isVersion43 == true)
            {
                _output.WriteVarint(animation.Slider.Count, true);
                foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Slider)
                {
                    _output.WriteVarint(Spine43Constraints.ConstraintIndex(_skeleton, entry.Key), true);
                    _output.WriteVarint(entry.Value.Count, true);
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                    {
                        _output.WriteByte(timeline.Key == "time" ? 0 : 1);
                        _output.WriteVarint(timeline.Value.Count, true);
                        _output.WriteVarint(timeline.Value.Count, true);
                        WriteValueTimeline(timeline.Value, 1);
                    }
                }
            }

            WriteAttachmentTimelines(animation);

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

            if (_isVersion43 == true)
            {
                WriteDrawOrderFolders(animation);
            }

            WriteEventTimeline(_output, _skeleton.Events, animation.Events);

            if (_isVersion43 == true && _skeleton.Nonessential == true)
            {
                _output.WriteColor(animation.Color ?? SpineColor.White);
            }
        }

        private void WriteSlotTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Slots.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Slots)
            {
                _output.WriteVarint(SlotIndex(entry.Key), true);
                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    List<SpineTimelineFrame> frames = timeline.Value;
                    _output.WriteByte(SpineNames.IndexOf(SpineNames.SlotTimeline, timeline.Key, "슬롯 타임라인"));
                    _output.WriteVarint(frames.Count, true);
                    switch (timeline.Key)
                    {
                        case "attachment":
                            foreach (SpineTimelineFrame frame in frames)
                            {
                                _output.WriteFloat(frame.Time);
                                _output.WriteStringRef(frame.Str1);
                            }

                            break;
                        case "rgba":
                            _output.WriteVarint(frames.Count * 4, true);
                            WriteKeyedTimeline(frames, frame => _output.WriteColor(RequireColor(frame.Color1)));
                            break;
                        case "rgb":
                            _output.WriteVarint(frames.Count * 3, true);
                            WriteKeyedTimeline(frames, frame => _output.WriteColor(RequireColor(frame.Color1), false));
                            break;
                        case "rgba2":
                            _output.WriteVarint(frames.Count * 7, true);
                            WriteKeyedTimeline(frames, frame =>
                            {
                                _output.WriteColor(RequireColor(frame.Color1));
                                _output.WriteColor(RequireColor(frame.Color2), false);
                            });
                            break;
                        case "rgb2":
                            _output.WriteVarint(frames.Count * 6, true);
                            WriteKeyedTimeline(frames, frame =>
                            {
                                _output.WriteColor(RequireColor(frame.Color1), false);
                                _output.WriteColor(RequireColor(frame.Color2), false);
                            });
                            break;
                        case "alpha":
                            _output.WriteVarint(frames.Count, true);
                            WriteKeyedTimeline(frames, frame => _output.WriteByte(AlphaToByte(frame.Value1)));
                            break;
                    }
                }
            }
        }

        private void WriteBoneTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Bones.Count, true);
            foreach (KeyValuePair<string, SpineTimelineSet> entry in animation.Bones)
            {
                _output.WriteVarint(BoneIndex(entry.Key), true);
                _output.WriteVarint(entry.Value.Count, true);
                foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in entry.Value)
                {
                    _output.WriteByte(SpineNames.IndexOf(SpineNames.BoneTimeline, timeline.Key, "뼈 타임라인"));
                    _output.WriteVarint(timeline.Value.Count, true);
                    if (timeline.Key == "inherit")
                    {
                        foreach (SpineTimelineFrame frame in timeline.Value)
                        {
                            _output.WriteFloat(frame.Time);
                            _output.WriteByte((int)frame.Inherit);
                        }

                        continue;
                    }

                    int valueCount = timeline.Key == "translate" || timeline.Key == "scale" || timeline.Key == "shear" ? 2 : 1;
                    _output.WriteVarint(timeline.Value.Count * valueCount, true);
                    WriteValueTimeline(timeline.Value, valueCount);
                }
            }
        }

        private void WriteIkTimeline(int index, List<SpineTimelineFrame> frames)
        {
            _output.WriteVarint(index, true);
            _output.WriteVarint(frames.Count, true);
            _output.WriteVarint(frames.Count * 2, true);
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                SpineTimelineFrame frame = frames[frameIndex];
                int flags = 0;
                if (frame.Value1 != 0f)
                {
                    flags |= 1;
                    if (frame.Value1 != 1f)
                    {
                        flags |= 2;
                    }
                }

                flags |= (frame.Value2 != 0f ? 4 : 0) | (frame.BendPositive ? 8 : 0) | (frame.Compress ? 16 : 0) | (frame.Stretch ? 32 : 0);
                if (frameIndex > 0)
                {
                    // 이전 키 → 이 키 구간의 커브가 이 키의 플래그에 실린다.
                    SpineCurveType previousCurve = frames[frameIndex - 1].CurveType;
                    flags |= previousCurve == SpineCurveType.Stepped ? 64 : previousCurve == SpineCurveType.Bezier ? 128 : 0;
                }

                _output.WriteByte(flags);
                _output.WriteFloat(frame.Time);
                if ((flags & 1) != 0 && (flags & 2) != 0)
                {
                    _output.WriteFloat(frame.Value1);
                }

                if ((flags & 4) != 0)
                {
                    _output.WriteFloat(frame.Value2);
                }

                if ((flags & 128) != 0)
                {
                    _output.WriteFloats(frames[frameIndex - 1].Curve);
                }
            }
        }

        private void WriteAttachmentTimelines(SpineAnimation animation)
        {
            _output.WriteVarint(animation.Attachments.Count, true);
            foreach (KeyValuePair<string, SpineMap<SpineMap<SpineTimelineSet>>> skinEntry in animation.Attachments)
            {
                _output.WriteVarint(IndexOf(SpineSkin.BinaryIndexTable(_skeleton.Skins), skin => skin.Name == skinEntry.Key, "스킨", skinEntry.Key), true);
                _output.WriteVarint(skinEntry.Value.Count, true);
                foreach (KeyValuePair<string, SpineMap<SpineTimelineSet>> slotEntry in skinEntry.Value)
                {
                    _output.WriteVarint(SlotIndex(slotEntry.Key), true);
                    _output.WriteVarint(slotEntry.Value.Values.Sum(timelines => timelines.Count), true);
                    foreach (KeyValuePair<string, SpineTimelineSet> attachmentEntry in slotEntry.Value)
                    {
                        foreach (KeyValuePair<string, List<SpineTimelineFrame>> timeline in attachmentEntry.Value)
                        {
                            List<SpineTimelineFrame> frames = timeline.Value;
                            _output.WriteStringRef(attachmentEntry.Key);
                            _output.WriteByte(SpineNames.IndexOf(SpineNames.AttachmentTimeline, timeline.Key, "어태치먼트 타임라인"));
                            _output.WriteVarint(frames.Count, true);
                            if (timeline.Key == "deform")
                            {
                                WriteDeformTimeline(frames);
                                continue;
                            }

                            foreach (SpineTimelineFrame frame in frames)
                            {
                                _output.WriteFloat(frame.Time);
                                _output.WriteInt((frame.Int1 << 4) | ((int)frame.SequenceMode & 0xf));
                                _output.WriteFloat(frame.Value1);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 디폼은 다른 키 타임라인과 배치가 다르다: [시간0][정점0] [시간1][커브0][정점1] [시간2][커브1][정점2] …
        /// (다음 키의 시간이 커브보다 먼저 오고, 정점은 커브 뒤에 온다.)
        /// </summary>
        private void WriteDeformTimeline(List<SpineTimelineFrame> frames)
        {
            _output.WriteVarint(frames.Count, true);
            _output.WriteFloat(frames[0].Time);
            for (int frameIndex = 0; ; frameIndex++)
            {
                SpineTimelineFrame frame = frames[frameIndex];
                _output.WriteVarint(frame.Vertices.Count, true);
                if (frame.Vertices.Count > 0)
                {
                    _output.WriteVarint(frame.Int1, true);
                    _output.WriteFloats(frame.Vertices);
                }

                if (frameIndex == frames.Count - 1)
                {
                    break;
                }

                _output.WriteFloat(frames[frameIndex + 1].Time);
                _output.WriteSByte((int)frame.CurveType);
                if (frame.CurveType == SpineCurveType.Bezier)
                {
                    _output.WriteFloats(frame.Curve);
                }
            }
        }

        private void WriteDrawOrderFolders(SpineAnimation animation)
        {
            _output.WriteVarint(animation.DrawOrderFolder.Count, true);
            foreach (JToken folder in animation.DrawOrderFolder)
            {
                List<string> slots = folder["slots"].Select(slot => (string)slot).ToList();
                _output.WriteVarint(slots.Count, true);
                foreach (string slot in slots)
                {
                    _output.WriteVarint(SlotIndex(slot), true);
                }

                JArray keys = (JArray)folder["keys"];
                _output.WriteVarint(keys.Count, true);
                foreach (JToken key in keys)
                {
                    _output.WriteFloat(SpineJson.GetFloat(key, "time", 0f));
                    List<JToken> offsets = SpineJson.Items(key, "offsets").ToList();
                    _output.WriteVarint(offsets.Count, true);
                    foreach (JToken offset in offsets)
                    {
                        int slotIndex = slots.IndexOf((string)offset["slot"]);
                        if (slotIndex < 0)
                        {
                            throw new SpineConversionException($"drawOrderFolder 오프셋의 슬롯이 폴더에 없다: '{offset["slot"]}'");
                        }

                        _output.WriteVarint(slotIndex, true);
                        _output.WriteVarint(SpineJson.GetInt(offset, "offset", 0), true);
                    }
                }
            }
        }

        /// <summary>읽기의 거울: 첫 키는 [시간, 값…], 이후 키는 [시간, 값…, 이전 키의 커브].</summary>
        private void WriteKeyedTimeline(List<SpineTimelineFrame> frames, Action<SpineTimelineFrame> writeValues)
        {
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                _output.WriteFloat(frames[frameIndex].Time);
                writeValues(frames[frameIndex]);
                if (frameIndex == 0)
                {
                    continue;
                }

                SpineTimelineFrame previous = frames[frameIndex - 1];
                _output.WriteSByte((int)previous.CurveType);
                if (previous.CurveType == SpineCurveType.Bezier)
                {
                    _output.WriteFloats(previous.Curve);
                }
            }
        }

        private void WriteValueTimeline(List<SpineTimelineFrame> frames, int valueCount)
        {
            WriteKeyedTimeline(frames, frame =>
            {
                _output.WriteFloat(frame.Value1);
                if (valueCount > 1)
                {
                    _output.WriteFloat(frame.Value2);
                }

                if (valueCount > 2)
                {
                    _output.WriteFloat(frame.Value3);
                }
            });
        }

        /// <summary>
        /// HACK : 원본은 (int)(alpha * 255) 로 버리는데, 바이너리에서 읽은 alpha(byte/255) 를 다시 곱하면 부동소수 오차로
        /// 1 이 깎이는 값이 있다(예: 0.29 → 73.99). 반올림해서 바이너리 왕복이 바이트 그대로 남게 한다.
        /// </summary>
        private static int AlphaToByte(float alpha)
        {
            return (int)Math.Round(alpha * 255f, MidpointRounding.AwayFromZero);
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
            return IndexOf(_skeleton.Bones, bone => bone.Name == name, "뼈", name);
        }

        private int SlotIndex(string name)
        {
            return IndexOf(_skeleton.Slots, slot => slot.Name == name, "슬롯", name);
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
