using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 3.8 JSON 라이터(원본 SkeletonData38JsonWriter). 기본값과 같은 키는 생략하고, 키 순서·구조를 원본과 같게 쓴다.
    /// 곡선은 모델에 든 3.x 상대 베지어 4개 값을 <c>curve</c>·<c>c2</c>·<c>c3</c>·<c>c4</c> 로 쓴다 — 4.x 에서 온 모델이면
    /// 버전 경계 패스(<see cref="Spine3xConversions"/>)가 먼저 3.x 곡선으로 바꿔 둬야 한다.
    /// </summary>
    public static class Spine38JsonWriter
    {
        public static JObject Write(SpineSkeleton skeleton)
        {
            var root = new JObject();
            var header = new JObject();
            if (skeleton.HashString != null)
            {
                header["hash"] = skeleton.HashString;
            }
            else if (skeleton.Hash != 0)
            {
                header["hash"] = SpineJson.HashToBase64(skeleton.Hash);
            }

            SetIfNotNull(header, "spine", skeleton.Version);
            header["x"] = SpineJson.Number(skeleton.X);
            header["y"] = SpineJson.Number(skeleton.Y);
            header["width"] = SpineJson.Number(skeleton.Width);
            header["height"] = SpineJson.Number(skeleton.Height);
            if (skeleton.Nonessential == true)
            {
                SetIfNot(header, "fps", skeleton.Fps, 30f);
                SetIfNotNull(header, "images", skeleton.ImagesPath);
                SetIfNotNull(header, "audio", skeleton.AudioPath);
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
                    json["transform"] = SpineNames.Inherit[(int)bone.Inherit];
                }

                SetIfTrue(json, "skin", bone.SkinRequired);
                SetColor(json, "color", bone.Color, true);
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

            // 3.8 은 X/Y 성분을 나누지 않는다 — mixY·mixScaleY 는 쓰지 않는다(원본과 같음).
            foreach (SpineTransformConstraint transform in skeleton.TransformConstraints)
            {
                JObject json = ConstraintHeader(transform.Name, transform.Order, transform.SkinRequired, transform.Bones, transform.Target);
                SetIfNot(json, "rotateMix", transform.MixRotate, 1f);
                SetIfNot(json, "translateMix", transform.MixX, 1f);
                SetIfNot(json, "scaleMix", transform.MixScaleX, 1f);
                SetIfNot(json, "shearMix", transform.MixShearY, 1f);
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
                SetIfNot(json, "rotateMix", path.MixRotate, 1f);
                SetIfNot(json, "translateMix", path.MixX, 1f);
                Array(root, "path").Add(json);
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
            SetNames(json, "bones", bones);
            SetIfNotNull(json, "target", target);
            return json;
        }

        private static void WriteSkins(JObject root, SpineSkeleton skeleton)
        {
            foreach (SpineSkin skin in skeleton.Skins)
            {
                // 3.8 스킨에는 색이 없다 — 4.2 에서 온 스킨 색은 쓰지 않는다(원본과 같음).
                var json = new JObject { ["name"] = skin.Name };
                SetNames(json, "bones", skin.Bones);
                SetNames(json, "ik", skin.Ik);
                SetNames(json, "transform", skin.Transform);
                SetNames(json, "path", skin.Path);
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
                    break;
                case SpineMeshAttachment mesh:
                    json["width"] = SpineJson.Number(mesh.Width);
                    json["height"] = SpineJson.Number(mesh.Height);
                    SetColor(json, "color", mesh.Color, true);
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
                    json["parent"] = linkedMesh.ParentMesh;
                    if (linkedMesh.Timelines != 1)
                    {
                        // 원본은 정수 0 을 쓰지만, 3.8 의 "deform" 은 bool 이다(spine-csharp 3.8 은 (bool) 캐스트라 정수면 로드에 실패한다).
                        // 런타임이 읽을 수 있게 bool 로 쓴다(원본과의 의도된 차이).
                        json["deform"] = linkedMesh.Timelines != 0;
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

            // 모델(4.x) 슬롯 타임라인 이름을 3.8 이름으로 옮긴다: rgba(없으면 rgb) → color, rgba2(없으면 rgb2) → twoColor.
            // 원본처럼 프레임이 있을 때만 키를 만든다.
            foreach (KeyValuePair<string, SpineTimelineSet> slot in animation.Slots)
            {
                var slotJson = new JObject();
                if (slot.Value.TryGetValue("attachment", out List<SpineTimelineFrame> attachmentFrames) == true)
                {
                    foreach (SpineTimelineFrame frame in attachmentFrames)
                    {
                        var frameJson = new JObject();
                        SetIfNot(frameJson, "time", frame.Time, 0f);
                        // 3.8 은 "어태치먼트 없음" 키에 name 을 null 로 적는다.
                        frameJson["name"] = frame.Str1 != null ? (JToken)frame.Str1 : JValue.CreateNull();
                        Array(slotJson, "attachment").Add(frameJson);
                    }
                }

                List<SpineTimelineFrame> colorFrames = FindFrames(slot.Value, "rgba") ?? FindFrames(slot.Value, "rgb");
                if (colorFrames != null)
                {
                    foreach (SpineTimelineFrame frame in colorFrames)
                    {
                        var frameJson = new JObject();
                        SetIfNot(frameJson, "time", frame.Time, 0f);
                        SetColor(frameJson, "color", frame.Color1, true);
                        WriteCurve(frame, frameJson);
                        Array(slotJson, "color").Add(frameJson);
                    }
                }

                List<SpineTimelineFrame> twoColorFrames = FindFrames(slot.Value, "rgba2") ?? FindFrames(slot.Value, "rgb2");
                if (twoColorFrames != null)
                {
                    foreach (SpineTimelineFrame frame in twoColorFrames)
                    {
                        var frameJson = new JObject();
                        SetIfNot(frameJson, "time", frame.Time, 0f);
                        SetColor(frameJson, "light", frame.Color1, true);
                        SetColor(frameJson, "dark", frame.Color2, false);
                        WriteCurve(frame, frameJson);
                        Array(slotJson, "twoColor").Add(frameJson);
                    }
                }

                Child(json, "slots")[slot.Key] = slotJson;
            }

            foreach (KeyValuePair<string, SpineTimelineSet> bone in animation.Bones)
            {
                var boneJson = new JObject();
                WriteValueTimeline(bone.Value, "rotate", boneJson, 1, "angle", null, 0f);
                WriteValueTimeline(bone.Value, "translate", boneJson, 2, "x", "y", 0f);
                WriteValueTimeline(bone.Value, "scale", boneJson, 2, "x", "y", 1f);
                WriteValueTimeline(bone.Value, "shear", boneJson, 2, "x", "y", 0f);
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
                    SetIfNot(frameJson, "rotateMix", frame.Value1, 1f);
                    SetIfNot(frameJson, "translateMix", frame.Value2, 1f);
                    SetIfNot(frameJson, "scaleMix", frame.Value4, 1f);
                    SetIfNot(frameJson, "shearMix", frame.Value6, 1f);
                });
            }

            foreach (KeyValuePair<string, SpineTimelineSet> path in animation.Path)
            {
                var pathJson = new JObject();
                WriteValueTimeline(path.Value, "position", pathJson, 1, "position", null, 0f);
                WriteValueTimeline(path.Value, "spacing", pathJson, 1, "spacing", null, 0f);
                if (path.Value.TryGetValue("mix", out List<SpineTimelineFrame> mixFrames) == true)
                {
                    foreach (SpineTimelineFrame frame in mixFrames)
                    {
                        var frameJson = new JObject();
                        SetIfNot(frameJson, "time", frame.Time, 0f);
                        SetIfNot(frameJson, "rotateMix", frame.Value1, 1f);
                        SetIfNot(frameJson, "translateMix", frame.Value2, 1f);
                        WriteCurve(frame, frameJson);
                        Array(pathJson, "mix").Add(frameJson);
                    }
                }

                Child(json, "path")[path.Key] = pathJson;
            }

            // 3.8 은 deform 만 있다(sequence 는 4.1+) — deform 이 없는 어태치먼트 타임라인은 건너뛴다(원본과 같음).
            foreach (KeyValuePair<string, SpineMap<SpineMap<SpineTimelineSet>>> skin in animation.Attachments)
            {
                foreach (KeyValuePair<string, SpineMap<SpineTimelineSet>> slot in skin.Value)
                {
                    foreach (KeyValuePair<string, SpineTimelineSet> attachment in slot.Value)
                    {
                        if (attachment.Value.TryGetValue("deform", out List<SpineTimelineFrame> deformFrames) == false)
                        {
                            continue;
                        }

                        Child(Child(Child(json, "deform"), skin.Key), slot.Key)[attachment.Key] = CurvedList(deformFrames, (frame, frameJson) =>
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

        /// <summary>3.x 곡선: "stepped" 또는 c1 을 <c>curve</c> 에, c2·c3·c4 는 기본값(0·1·1)이 아닐 때만 쓴다.</summary>
        private static void WriteCurve(SpineTimelineFrame frame, JObject frameJson)
        {
            if (frame.CurveType == SpineCurveType.Stepped)
            {
                frameJson["curve"] = "stepped";
            }
            else if (frame.CurveType == SpineCurveType.Bezier)
            {
                List<float> curve = frame.Curve;
                if (curve.Count > 0)
                {
                    frameJson["curve"] = SpineJson.Number(curve[0]);
                }

                if (curve.Count > 1)
                {
                    SetIfNot(frameJson, "c2", curve[1], 0f);
                }

                if (curve.Count > 2)
                {
                    SetIfNot(frameJson, "c3", curve[2], 1f);
                }

                if (curve.Count > 3)
                {
                    SetIfNot(frameJson, "c4", curve[3], 1f);
                }
            }
        }

        private static JArray CurvedList(List<SpineTimelineFrame> frames, System.Action<SpineTimelineFrame, JObject> writeValues)
        {
            var array = new JArray();
            foreach (SpineTimelineFrame frame in frames)
            {
                var frameJson = new JObject();
                SetIfNot(frameJson, "time", frame.Time, 0f);
                writeValues(frame, frameJson);
                WriteCurve(frame, frameJson);
                array.Add(frameJson);
            }

            return array;
        }

        /// <summary>
        /// 원본 writeTimeline. 원본은 키를 먼저 만들고 프레임을 이어 붙이므로, 빈 타임라인이면 배열이 아니라 <c>null</c> 이 남는다 — 그대로 따른다.
        /// </summary>
        private static void WriteValueTimeline(SpineTimelineSet timelines, string key, JObject owner, int valueCount, string key1, string key2, float defaultValue)
        {
            if (timelines.TryGetValue(key, out List<SpineTimelineFrame> frames) == false)
            {
                return;
            }

            if (frames.Count == 0)
            {
                owner[key] = JValue.CreateNull();
                return;
            }

            owner[key] = CurvedList(frames, (frame, frameJson) =>
            {
                SetIfNot(frameJson, key1, frame.Value1, defaultValue);
                if (valueCount > 1)
                {
                    SetIfNot(frameJson, key2, frame.Value2, defaultValue);
                }
            });
        }

        private static List<SpineTimelineFrame> FindFrames(SpineTimelineSet timelines, string key)
        {
            return timelines.TryGetValue(key, out List<SpineTimelineFrame> frames) == true ? frames : null;
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
    }
}
