using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 4.2 JSON 라이터(원본 SkeletonData42JsonWriter). 기본값과 같은 키는 생략한다.
    /// 4.3 JSON 과 4.3 바이너리 제약조건은 이 결과를 <see cref="Spine43Json.FromLegacyJson"/>으로 바꿔 만든다.
    /// </summary>
    public static class SpineSkeletonJsonWriter
    {
        public static JObject Write(SpineSkeleton skeleton)
        {
            var root = new JObject();
            var header = new JObject();
            if (skeleton.Hash != 0)
            {
                header["hash"] = SpineJson.HashToBase64(skeleton.Hash);
            }

            if (skeleton.Version != null)
            {
                header["spine"] = skeleton.Version;
            }

            header["x"] = SpineJson.Number(skeleton.X);
            header["y"] = SpineJson.Number(skeleton.Y);
            header["width"] = SpineJson.Number(skeleton.Width);
            header["height"] = SpineJson.Number(skeleton.Height);
            if (skeleton.ReferenceScale != 100f)
            {
                header["referenceScale"] = SpineJson.Number(skeleton.ReferenceScale);
            }

            if (skeleton.Nonessential == true)
            {
                if (skeleton.Fps != 30f)
                {
                    header["fps"] = SpineJson.Number(skeleton.Fps);
                }

                if (skeleton.ImagesPath != null)
                {
                    header["images"] = skeleton.ImagesPath;
                }

                if (skeleton.AudioPath != null)
                {
                    header["audio"] = skeleton.AudioPath;
                }
            }

            root["skeleton"] = header;
            WriteBones(root, skeleton);
            WriteSlots(root, skeleton);
            WriteConstraints(root, skeleton);
            WriteSkins(root, skeleton);
            WriteEvents(root, skeleton);
            foreach (SpineAnimation animation in skeleton.Animations)
            {
                Child(root, "animations")[animation.Name] = WriteAnimation(animation, skeleton);
            }

            return root;
        }

        private static void WriteBones(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineBone bone in skeleton.Bones)
            {
                var json = new JObject();
                SetIfNotNull(json, "name", bone.Name);
                SetIfNotNull(json, "parent", bone.Parent);
                SetIfNot(json, "length", bone.Length, 0f);
                SetIfNot(json, "x", bone.X, 0f);
                SetIfNot(json, "y", bone.Y, 0f);
                SetIfNot(json, "rotation", bone.Rotation, 0f);
                SetIfNot(json, "scaleX", bone.ScaleX, 1f);
                SetIfNot(json, "scaleY", bone.ScaleY, 1f);
                SetIfNot(json, "shearX", bone.ShearX, 0f);
                SetIfNot(json, "shearY", bone.ShearY, 0f);
                if (bone.Inherit != SpineInherit.Normal)
                {
                    json["inherit"] = SpineNames.Inherit[(int)bone.Inherit];
                }

                SetIfTrue(json, "skin", bone.SkinRequired);
                SetColor(json, "color", bone.Color, true);
                if (string.IsNullOrEmpty(bone.Icon) == false)
                {
                    json["icon"] = bone.Icon;
                }

                SetIfTrue(json, "visible", bone.Visible == false, false);
                Array(root, "bones").Add(json);
            }
        }

        private static void WriteSlots(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineSlot slot in skeleton.Slots)
            {
                var json = new JObject();
                SetIfNotNull(json, "name", slot.Name);
                SetIfNotNull(json, "bone", slot.Bone);
                SetColor(json, "color", slot.Color, true);
                SetColor(json, "dark", slot.DarkColor, false);
                SetIfNotNull(json, "attachment", slot.AttachmentName);
                if (slot.BlendMode != SpineBlendMode.Normal)
                {
                    json["blend"] = SpineNames.BlendMode[(int)slot.BlendMode];
                }

                SetIfTrue(json, "visible", slot.Visible == false, false);
                Array(root, "slots").Add(json);
            }
        }

        private static void WriteConstraints(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineIkConstraint ik in skeleton.IkConstraints)
            {
                JObject json = ConstraintHeader(ik.Name, ik.Order, ik.SkinRequired, ik.Bones, ik.Target);
                SetIfNot(json, "mix", ik.Mix, 1f);
                SetIfNot(json, "softness", ik.Softness, 0f);
                SetIfTrue(json, "bendPositive", ik.BendPositive == false, false);
                SetIfTrue(json, "compress", ik.Compress);
                SetIfTrue(json, "stretch", ik.Stretch);
                SetIfTrue(json, "uniform", ik.Uniform);
                Array(root, "ik").Add(json);
            }

            foreach (SpineTransformConstraint transform in skeleton.TransformConstraints)
            {
                JObject json = ConstraintHeader(transform.Name, transform.Order, transform.SkinRequired, transform.Bones, transform.Target);
                SetIfNot(json, "mixRotate", transform.MixRotate, 1f);
                SetIfNot(json, "mixX", transform.MixX, 1f);
                SetIfNot(json, "mixY", transform.MixY, transform.MixX);
                SetIfNot(json, "mixScaleX", transform.MixScaleX, 1f);
                SetIfNot(json, "mixScaleY", transform.MixScaleY, transform.MixScaleX);
                SetIfNot(json, "mixShearY", transform.MixShearY, 1f);
                SetIfNot(json, "rotation", transform.OffsetRotation, 0f);
                SetIfNot(json, "x", transform.OffsetX, 0f);
                SetIfNot(json, "y", transform.OffsetY, 0f);
                SetIfNot(json, "scaleX", transform.OffsetScaleX, 0f);
                SetIfNot(json, "scaleY", transform.OffsetScaleY, 0f);
                SetIfNot(json, "shearY", transform.OffsetShearY, 0f);
                SetIfTrue(json, "relative", transform.Relative);
                SetIfTrue(json, "local", transform.Local);
                Array(root, "transform").Add(json);
            }

            foreach (SpinePathConstraint path in skeleton.PathConstraints)
            {
                JObject json = ConstraintHeader(path.Name, path.Order, path.SkinRequired, path.Bones, path.Target);
                if (path.PositionMode != SpinePositionMode.Percent)
                {
                    json["positionMode"] = SpineNames.PositionMode[(int)path.PositionMode];
                }

                if (path.SpacingMode != SpineSpacingMode.Length)
                {
                    json["spacingMode"] = SpineNames.SpacingMode[(int)path.SpacingMode];
                }

                if (path.RotateMode != SpineRotateMode.Tangent)
                {
                    json["rotateMode"] = SpineNames.RotateMode[(int)path.RotateMode];
                }

                SetIfNot(json, "rotation", path.OffsetRotation, 0f);
                SetIfNot(json, "position", path.Position, 0f);
                SetIfNot(json, "spacing", path.Spacing, 0f);
                SetIfNot(json, "mixRotate", path.MixRotate, 1f);
                SetIfNot(json, "mixX", path.MixX, 1f);
                SetIfNot(json, "mixY", path.MixY, path.MixX);
                Array(root, "path").Add(json);
            }

            foreach (SpinePhysicsConstraint physics in skeleton.PhysicsConstraints)
            {
                var json = new JObject();
                SetIfNotNull(json, "name", physics.Name);
                if (physics.Order != 0)
                {
                    json["order"] = physics.Order;
                }

                SetIfTrue(json, "skin", physics.SkinRequired);
                SetIfNotNull(json, "bone", physics.Bone);
                SetIfNot(json, "x", physics.X, 0f);
                SetIfNot(json, "y", physics.Y, 0f);
                SetIfNot(json, "rotate", physics.Rotate, 0f);
                SetIfNot(json, "scaleX", physics.ScaleX, 0f);
                SetIfNot(json, "shearX", physics.ShearX, 0f);
                SetIfNot(json, "limit", physics.Limit, 5000f);
                SetIfNot(json, "fps", physics.Fps, 60f);
                SetIfNot(json, "inertia", physics.Inertia, 1f);
                SetIfNot(json, "strength", physics.Strength, 100f);
                SetIfNot(json, "damping", physics.Damping, 1f);
                SetIfNot(json, "mass", physics.Mass, 1f);
                SetIfNot(json, "wind", physics.Wind, 0f);
                SetIfNot(json, "gravity", physics.Gravity, 0f);
                SetIfNot(json, "mix", physics.Mix, 1f);
                SetIfTrue(json, "inertiaGlobal", physics.InertiaGlobal);
                SetIfTrue(json, "strengthGlobal", physics.StrengthGlobal);
                SetIfTrue(json, "dampingGlobal", physics.DampingGlobal);
                SetIfTrue(json, "massGlobal", physics.MassGlobal);
                SetIfTrue(json, "windGlobal", physics.WindGlobal);
                SetIfTrue(json, "gravityGlobal", physics.GravityGlobal);
                SetIfTrue(json, "mixGlobal", physics.MixGlobal);
                Array(root, "physics").Add(json);
            }
        }

        private static JObject ConstraintHeader(string name, int order, bool isSkinRequired, List<string> bones, string target)
        {
            var json = new JObject();
            SetIfNotNull(json, "name", name);
            if (order != 0)
            {
                json["order"] = order;
            }

            SetIfTrue(json, "skin", isSkinRequired);
            if (bones.Count > 0)
            {
                json["bones"] = new JArray(bones);
            }

            SetIfNotNull(json, "target", target);
            return json;
        }

        private static void WriteSkins(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineSkin skin in skeleton.Skins)
            {
                var json = new JObject { ["name"] = skin.Name };
                SetColor(json, "color", skin.Color, true);
                SetNames(json, "bones", skin.Bones);
                SetNames(json, "ik", skin.Ik);
                SetNames(json, "transform", skin.Transform);
                SetNames(json, "path", skin.Path);
                SetNames(json, "physics", skin.Physics);
                foreach (KeyValuePair<string, SpineMap<SpineAttachment>> slotEntry in skin.Attachments)
                {
                    foreach (KeyValuePair<string, SpineAttachment> entry in slotEntry.Value)
                    {
                        Child(Child(json, "attachments"), slotEntry.Key)[entry.Key] = WriteAttachment(entry.Key, entry.Value);
                    }
                }

                Array(root, "skins").Add(json);
            }
        }

        private static JObject WriteAttachment(string attachmentName, SpineAttachment attachment)
        {
            var json = new JObject();
            if (attachment.Name != attachmentName)
            {
                json["name"] = attachment.Name;
            }

            if (attachment.Path != attachment.Name)
            {
                json["path"] = attachment.Path;
            }

            if (attachment.Type != SpineAttachmentType.Region)
            {
                json["type"] = SpineNames.AttachmentType[(int)attachment.Type];
            }

            switch (attachment)
            {
                case SpineRegionAttachment region:
                    SetIfNot(json, "x", region.X, 0f);
                    SetIfNot(json, "y", region.Y, 0f);
                    SetIfNot(json, "rotation", region.Rotation, 0f);
                    SetIfNot(json, "scaleX", region.ScaleX, 1f);
                    SetIfNot(json, "scaleY", region.ScaleY, 1f);
                    json["width"] = SpineJson.Number(region.Width);
                    json["height"] = SpineJson.Number(region.Height);
                    SetColor(json, "color", region.Color, true);
                    SetSequence(json, region.Sequence);
                    break;
                case SpineMeshAttachment mesh:
                    json["width"] = SpineJson.Number(mesh.Width);
                    json["height"] = SpineJson.Number(mesh.Height);
                    SetColor(json, "color", mesh.Color, true);
                    SetSequence(json, mesh.Sequence);
                    if (mesh.HullLength != 0)
                    {
                        json["hull"] = mesh.HullLength;
                    }

                    SetInts(json, "triangles", mesh.Triangles);
                    SetInts(json, "edges", mesh.Edges);
                    SetFloats(json, "uvs", mesh.Uvs);
                    SetFloats(json, "vertices", mesh.Vertices);
                    break;
                case SpineLinkedMeshAttachment linkedMesh:
                    json["width"] = SpineJson.Number(linkedMesh.Width);
                    json["height"] = SpineJson.Number(linkedMesh.Height);
                    SetColor(json, "color", linkedMesh.Color, true);
                    SetSequence(json, linkedMesh.Sequence);
                    json["parent"] = linkedMesh.ParentMesh;
                    if (linkedMesh.Timelines != 1)
                    {
                        json["timelines"] = linkedMesh.Timelines;
                    }

                    SetIfNotNull(json, "skin", linkedMesh.Skin);
                    break;
                case SpineBoundingBoxAttachment box:
                    if (box.VertexCount != 0)
                    {
                        json["vertexCount"] = box.VertexCount;
                    }

                    SetColor(json, "color", box.Color, true);
                    SetFloats(json, "vertices", box.Vertices);
                    break;
                case SpinePathAttachment path:
                    if (path.VertexCount != 0)
                    {
                        json["vertexCount"] = path.VertexCount;
                    }

                    SetIfTrue(json, "closed", path.Closed);
                    SetIfTrue(json, "constantSpeed", path.ConstantSpeed == false, false);
                    SetColor(json, "color", path.Color, true);
                    SetFloats(json, "vertices", path.Vertices);
                    SetFloats(json, "lengths", path.Lengths);
                    break;
                case SpinePointAttachment point:
                    SetIfNot(json, "x", point.X, 0f);
                    SetIfNot(json, "y", point.Y, 0f);
                    SetIfNot(json, "rotation", point.Rotation, 0f);
                    SetColor(json, "color", point.Color, true);
                    break;
                case SpineClippingAttachment clipping:
                    if (clipping.VertexCount != 0)
                    {
                        json["vertexCount"] = clipping.VertexCount;
                    }

                    SetIfNotNull(json, "end", clipping.EndSlot);
                    SetColor(json, "color", clipping.Color, true);
                    SetFloats(json, "vertices", clipping.Vertices);
                    break;
            }

            return json;
        }

        private static void WriteEvents(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineEvent spineEvent in skeleton.Events)
            {
                var json = new JObject();
                if (spineEvent.IntValue != 0)
                {
                    json["int"] = spineEvent.IntValue;
                }

                SetIfNot(json, "float", spineEvent.FloatValue, 0f);
                SetIfNotNull(json, "string", spineEvent.StringValue);
                if (spineEvent.AudioPath != null)
                {
                    json["audio"] = spineEvent.AudioPath;
                    SetIfNot(json, "volume", spineEvent.Volume, 1f);
                    SetIfNot(json, "balance", spineEvent.Balance, 0f);
                }

                Child(root, "events")[spineEvent.Name] = json;
            }
        }

        private static JObject WriteAnimation(SpineAnimation animation, SpineSkeleton skeleton)
        {
            var json = new JObject();
            foreach (KeyValuePair<string, SpineTimelineSet> slot in animation.Slots)
            {
                var slotJson = new JObject();
                WriteFrames(slot.Value, "attachment", slotJson, (frame, frameJson) => SetIfNotNull(frameJson, "name", frame.Str1));
                WriteCurvedFrames(slot.Value, "rgba", slotJson, (frame, frameJson) => SetColor(frameJson, "color", frame.Color1, true));
                WriteCurvedFrames(slot.Value, "rgb", slotJson, (frame, frameJson) => SetColor(frameJson, "color", frame.Color1, false));
                WriteValueFrames(slot.Value, "alpha", slotJson, 1, "value", null, 0f);
                WriteCurvedFrames(slot.Value, "rgba2", slotJson, (frame, frameJson) =>
                {
                    SetColor(frameJson, "light", frame.Color1, true);
                    SetColor(frameJson, "dark", frame.Color2, false);
                });
                WriteCurvedFrames(slot.Value, "rgb2", slotJson, (frame, frameJson) =>
                {
                    SetColor(frameJson, "light", frame.Color1, false);
                    SetColor(frameJson, "dark", frame.Color2, false);
                });
                Child(json, "slots")[slot.Key] = slotJson;
            }

            foreach (KeyValuePair<string, SpineTimelineSet> bone in animation.Bones)
            {
                var boneJson = new JObject();
                WriteValueFrames(bone.Value, "rotate", boneJson, 1, "value", null, 0f);
                WriteValueFrames(bone.Value, "translate", boneJson, 2, "x", "y", 0f);
                WriteValueFrames(bone.Value, "translatex", boneJson, 1, "value", null, 0f);
                WriteValueFrames(bone.Value, "translatey", boneJson, 1, "value", null, 0f);
                WriteValueFrames(bone.Value, "scale", boneJson, 2, "x", "y", 1f);
                WriteValueFrames(bone.Value, "scalex", boneJson, 1, "value", null, 1f);
                WriteValueFrames(bone.Value, "scaley", boneJson, 1, "value", null, 1f);
                WriteValueFrames(bone.Value, "shear", boneJson, 2, "x", "y", 0f);
                WriteValueFrames(bone.Value, "shearx", boneJson, 1, "value", null, 0f);
                WriteValueFrames(bone.Value, "sheary", boneJson, 1, "value", null, 0f);
                WriteFrames(bone.Value, "inherit", boneJson, (frame, frameJson) =>
                {
                    if (frame.Inherit != SpineInherit.Normal)
                    {
                        frameJson["inherit"] = SpineNames.Inherit[(int)frame.Inherit];
                    }
                });
                Child(json, "bones")[bone.Key] = boneJson;
            }

            foreach (KeyValuePair<string, List<SpineTimelineFrame>> ik in animation.Ik)
            {
                Child(json, "ik")[ik.Key] = CurvedList(ik.Value, (frame, frameJson) =>
                {
                    SetIfNot(frameJson, "mix", frame.Value1, 1f);
                    SetIfNot(frameJson, "softness", frame.Value2, 0f);
                    SetIfTrue(frameJson, "bendPositive", frame.BendPositive == false, false);
                    SetIfTrue(frameJson, "compress", frame.Compress);
                    SetIfTrue(frameJson, "stretch", frame.Stretch);
                });
            }

            foreach (KeyValuePair<string, List<SpineTimelineFrame>> transform in animation.Transform)
            {
                Child(json, "transform")[transform.Key] = CurvedList(transform.Value, (frame, frameJson) =>
                {
                    SetIfNot(frameJson, "mixRotate", frame.Value1, 1f);
                    SetIfNot(frameJson, "mixX", frame.Value2, 1f);
                    SetIfNot(frameJson, "mixY", frame.Value3, frame.Value2);
                    SetIfNot(frameJson, "mixScaleX", frame.Value4, 1f);
                    SetIfNot(frameJson, "mixScaleY", frame.Value5, frame.Value4);
                    SetIfNot(frameJson, "mixShearY", frame.Value6, 1f);
                });
            }

            foreach (KeyValuePair<string, SpineTimelineSet> path in animation.Path)
            {
                var pathJson = new JObject();
                WriteValueFrames(path.Value, "position", pathJson, 1, "value", null, 0f);
                WriteValueFrames(path.Value, "spacing", pathJson, 1, "value", null, 0f);
                WriteCurvedFrames(path.Value, "mix", pathJson, (frame, frameJson) =>
                {
                    SetIfNot(frameJson, "mixRotate", frame.Value1, 1f);
                    SetIfNot(frameJson, "mixX", frame.Value2, 1f);
                    SetIfNot(frameJson, "mixY", frame.Value3, frame.Value2);
                });
                Child(json, "path")[path.Key] = pathJson;
            }

            foreach (KeyValuePair<string, SpineTimelineSet> physics in animation.Physics)
            {
                var physicsJson = new JObject();
                WriteFrames(physics.Value, "reset", physicsJson, (frame, frameJson) => { });
                WriteValueFrames(physics.Value, "inertia", physicsJson, 1, "value", null, 0f);
                WriteValueFrames(physics.Value, "strength", physicsJson, 1, "value", null, 0f);
                WriteValueFrames(physics.Value, "damping", physicsJson, 1, "value", null, 0f);
                WriteValueFrames(physics.Value, "mass", physicsJson, 1, "value", null, 0f);
                WriteValueFrames(physics.Value, "wind", physicsJson, 1, "value", null, 0f);
                WriteValueFrames(physics.Value, "gravity", physicsJson, 1, "value", null, 0f);
                // 원본 버그 수정: 원본은 mix 를 기본값 0 기준으로 써서, 값 0 인 키가 생략됐다가 읽을 때 1 로 바뀐다.
                // 읽기(기본값 1)와 맞춰 쓴다 — 원본 JSON 출력과는 "mix 1 키의 생략 여부"만 다르다.
                WriteValueFrames(physics.Value, "mix", physicsJson, 1, "value", null, 1f);
                Child(json, "physics")[physics.Key] = physicsJson;
            }

            foreach (KeyValuePair<string, SpineMap<SpineMap<SpineTimelineSet>>> skin in animation.Attachments)
            {
                foreach (KeyValuePair<string, SpineMap<SpineTimelineSet>> slot in skin.Value)
                {
                    foreach (KeyValuePair<string, SpineTimelineSet> attachment in slot.Value)
                    {
                        var attachmentJson = new JObject();
                        WriteCurvedFrames(attachment.Value, "deform", attachmentJson, (frame, frameJson) =>
                        {
                            if (frame.Vertices.Count > 0)
                            {
                                if (frame.Int1 != 0)
                                {
                                    frameJson["offset"] = frame.Int1;
                                }

                                frameJson["vertices"] = SpineJson.Numbers(frame.Vertices);
                            }
                        });

                        float lastDelay = 0f;
                        WriteFrames(attachment.Value, "sequence", attachmentJson, (frame, frameJson) =>
                        {
                            if (frame.Value1 != lastDelay)
                            {
                                frameJson["delay"] = SpineJson.Number(frame.Value1);
                            }

                            lastDelay = frame.Value1;
                            if (frame.Int1 != 0)
                            {
                                frameJson["index"] = frame.Int1;
                            }

                            if (frame.SequenceMode != SpineSequenceMode.Hold)
                            {
                                frameJson["mode"] = SpineNames.SequenceMode[(int)frame.SequenceMode];
                            }
                        });
                        Child(Child(Child(json, "attachments"), skin.Key), slot.Key)[attachment.Key] = attachmentJson;
                    }
                }
            }

            foreach (SpineTimelineFrame frame in animation.DrawOrder)
            {
                var frameJson = new JObject();
                SetIfNot(frameJson, "time", frame.Time, 0f);
                foreach (KeyValuePair<string, int> offset in frame.Offsets)
                {
                    Array(frameJson, "offsets").Add(new JObject { ["slot"] = offset.Key, ["offset"] = offset.Value });
                }

                Array(json, "drawOrder").Add(frameJson);
            }

            foreach (SpineTimelineFrame frame in animation.Events)
            {
                SpineEvent eventData = skeleton.Events.Find(candidate => candidate.Name == frame.Str1)
                    ?? throw new SpineConversionException($"애니메이션 '{animation.Name}' 이 정의되지 않은 이벤트를 쓴다: '{frame.Str1}'");
                var frameJson = new JObject();
                SetIfNot(frameJson, "time", frame.Time, 0f);
                SetIfNotNull(frameJson, "name", frame.Str1);
                if (frame.Int1 != eventData.IntValue)
                {
                    frameJson["int"] = frame.Int1;
                }

                SetIfNot(frameJson, "float", frame.Value1, eventData.FloatValue);
                SetIfNotNull(frameJson, "string", frame.Str2);
                if (eventData.AudioPath != null)
                {
                    SetIfNot(frameJson, "volume", frame.Value2, 1f);
                    SetIfNot(frameJson, "balance", frame.Value3, 0f);
                }

                Array(json, "events").Add(frameJson);
            }

            return json;
        }

        private static JArray CurvedList(List<SpineTimelineFrame> frames, System.Action<SpineTimelineFrame, JObject> writeValues)
        {
            var array = new JArray();
            foreach (SpineTimelineFrame frame in frames)
            {
                var frameJson = new JObject();
                SetIfNot(frameJson, "time", frame.Time, 0f);
                writeValues(frame, frameJson);
                if (frame.CurveType == SpineCurveType.Stepped)
                {
                    frameJson["curve"] = "stepped";
                }
                else if (frame.CurveType == SpineCurveType.Bezier)
                {
                    frameJson["curve"] = SpineJson.Numbers(frame.Curve);
                }

                array.Add(frameJson);
            }

            return array;
        }

        private static void WriteFrames(SpineTimelineSet timelines, string key, JObject owner, System.Action<SpineTimelineFrame, JObject> writeValues)
        {
            if (timelines.TryGetValue(key, out List<SpineTimelineFrame> frames) == false)
            {
                return;
            }

            var array = new JArray();
            foreach (SpineTimelineFrame frame in frames)
            {
                var frameJson = new JObject();
                SetIfNot(frameJson, "time", frame.Time, 0f);
                writeValues(frame, frameJson);
                array.Add(frameJson);
            }

            owner[key] = array;
        }

        private static void WriteCurvedFrames(SpineTimelineSet timelines, string key, JObject owner, System.Action<SpineTimelineFrame, JObject> writeValues)
        {
            if (timelines.TryGetValue(key, out List<SpineTimelineFrame> frames) == true)
            {
                owner[key] = CurvedList(frames, writeValues);
            }
        }

        private static void WriteValueFrames(SpineTimelineSet timelines, string key, JObject owner, int valueCount, string key1, string key2, float defaultValue)
        {
            WriteCurvedFrames(timelines, key, owner, (frame, frameJson) =>
            {
                SetIfNot(frameJson, key1, frame.Value1, defaultValue);
                if (valueCount > 1)
                {
                    SetIfNot(frameJson, key2, frame.Value2, defaultValue);
                }
            });
        }

        private static JObject Child(JObject owner, string key)
        {
            if (owner[key] is JObject child)
            {
                return child;
            }

            child = new JObject();
            owner[key] = child;
            return child;
        }

        private static JArray Array(JObject owner, string key)
        {
            if (owner[key] is JArray array)
            {
                return array;
            }

            array = new JArray();
            owner[key] = array;
            return array;
        }

        private static void SetIfNot(JObject json, string key, float value, float defaultValue)
        {
            if (value != defaultValue)
            {
                json[key] = SpineJson.Number(value);
            }
        }

        private static void SetIfTrue(JObject json, string key, bool condition, bool value = true)
        {
            if (condition == true)
            {
                json[key] = value;
            }
        }

        private static void SetIfNotNull(JObject json, string key, string value)
        {
            if (value != null)
            {
                json[key] = value;
            }
        }

        private static void SetColor(JObject json, string key, SpineColor? color, bool hasAlpha)
        {
            if (color.HasValue == true)
            {
                json[key] = SpineJson.FormatColor(color.Value, hasAlpha);
            }
        }

        private static void SetNames(JObject json, string key, List<string> names)
        {
            if (names.Count > 0)
            {
                json[key] = new JArray(names);
            }
        }

        private static void SetFloats(JObject json, string key, List<float> values)
        {
            if (values.Count > 0)
            {
                json[key] = SpineJson.Numbers(values);
            }
        }

        private static void SetInts(JObject json, string key, List<int> values)
        {
            if (values.Count > 0)
            {
                json[key] = new JArray(values.Select(value => (object)value));
            }
        }

        private static void SetSequence(JObject json, SpineSequence sequence)
        {
            if (sequence == null)
            {
                return;
            }

            var sequenceJson = new JObject();
            if (sequence.Count != 0)
            {
                sequenceJson["count"] = sequence.Count;
            }

            if (sequence.Start != 1)
            {
                sequenceJson["start"] = sequence.Start;
            }

            if (sequence.Digits != 0)
            {
                sequenceJson["digits"] = sequence.Digits;
            }

            if (sequence.SetupIndex != 0)
            {
                sequenceJson["setupIndex"] = sequence.SetupIndex;
            }

            json["sequence"] = sequenceJson;
        }
    }
}
