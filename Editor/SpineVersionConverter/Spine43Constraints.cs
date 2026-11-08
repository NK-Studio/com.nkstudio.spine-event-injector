using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// 4.3 바이너리의 통합 제약조건 항목 읽기/쓰기(원본 Constraints43.cpp). 항목은 4.3 JSON 과 같은 모양의 JObject 로 다룬다.
    /// 종류 번호는 바이너리와 내부 순서가 다르다: 바이너리 1 = path, 2 = transform (내부는 1 = transform, 2 = path).
    /// </summary>
    public static class Spine43Constraints
    {
        private static readonly string[] Types = { "ik", "transform", "path", "physics", "slider" };
        private static readonly string[] Properties = { "rotate", "x", "y", "scaleX", "scaleY", "shearY" };
        private static readonly string[] Offsets = { "rotation", "x", "y", "scaleX", "scaleY", "shearY" };
        private static readonly string[] Mixes = { "mixRotate", "mixX", "mixY", "mixScaleX", "mixScaleY", "mixShearY" };
        private static readonly string[] ScaleModes = { "none", "uniform", "volume" };
        private static readonly string[] PhysicsProperties = { "x", "y", "rotate", "scaleX", "shearX", "limit" };
        private static readonly string[] Globals = { "inertiaGlobal", "strengthGlobal", "dampingGlobal", "massGlobal", "windGlobal", "gravityGlobal", "mixGlobal" };

        private const int TypeIk = 0;
        private const int TypeTransform = 1;
        private const int TypePath = 2;
        private const int TypePhysics = 3;

        public static JObject ConstraintAt(SpineSkeleton skeleton, int index)
        {
            if (index < 0 || index >= skeleton.Constraints43.Count)
            {
                throw new SpineConversionException($"4.3 제약조건 인덱스({index})가 범위 밖이다.");
            }

            return (JObject)skeleton.Constraints43[index];
        }

        public static string ConstraintName(SpineSkeleton skeleton, int index)
        {
            return (string)ConstraintAt(skeleton, index)["name"];
        }

        public static int ConstraintIndex(SpineSkeleton skeleton, string name)
        {
            for (int i = 0; i < skeleton.Constraints43.Count; i++)
            {
                if ((string)skeleton.Constraints43[i]["name"] == name)
                {
                    return i;
                }
            }

            throw new SpineConversionException($"4.3 제약조건 '{name}' 을(를) 찾을 수 없다.");
        }

        public static JObject Read(SpineBinaryInput input, SpineSkeleton skeleton)
        {
            var constraint = new JObject { ["name"] = input.ReadRequiredString("제약조건 이름") };
            int wireType = input.ReadByte();
            int type = wireType == 1 ? TypePath : wireType == 2 ? TypeTransform : wireType;
            constraint["type"] = SpineNames.NameAt(Types, type, "4.3 제약조건 종류");

            string ReadBone()
            {
                int index = input.ReadVarint(true);
                if (index >= skeleton.Bones.Count)
                {
                    throw new SpineConversionException($"뼈 인덱스({index})가 범위 밖이다.");
                }

                return skeleton.Bones[index].Name;
            }

            if (type < TypePhysics)
            {
                var bones = new JArray();
                for (int n = input.ReadVarint(true); n > 0; n--)
                {
                    bones.Add(ReadBone());
                }

                constraint["bones"] = bones;
                if (type == TypePath)
                {
                    int slotIndex = input.ReadVarint(true);
                    if (slotIndex >= skeleton.Slots.Count)
                    {
                        throw new SpineConversionException($"슬롯 인덱스({slotIndex})가 범위 밖이다.");
                    }

                    constraint["slot"] = skeleton.Slots[slotIndex].Name;
                }
                else
                {
                    constraint[type == TypeIk ? "target" : "source"] = ReadBone();
                }
            }
            else if (type == TypePhysics)
            {
                constraint["bone"] = ReadBone();
            }

            int flags = input.ReadByte();
            constraint["skin"] = (flags & 1) != 0;
            switch (type)
            {
                case TypeIk:
                    if ((flags & 2) != 0)
                    {
                        constraint["scaleY"] = SpineNames.NameAt(ScaleModes, input.ReadByte(), "IK scaleY");
                    }

                    constraint["bendPositive"] = (flags & 4) == 0;
                    constraint["compress"] = (flags & 8) != 0;
                    constraint["stretch"] = (flags & 16) != 0;
                    constraint["mix"] = SpineJson.Number((flags & 32) != 0 ? ((flags & 64) != 0 ? input.ReadFloat() : 1f) : 0f);
                    if ((flags & 128) != 0)
                    {
                        constraint["softness"] = SpineJson.Number(input.ReadFloat());
                    }

                    break;
                case TypeTransform:
                {
                    constraint["localSource"] = (flags & 2) != 0;
                    constraint["localTarget"] = (flags & 4) != 0;
                    constraint["additive"] = (flags & 8) != 0;
                    constraint["clamp"] = (flags & 16) != 0;
                    var properties = new JObject();
                    for (int n = flags >> 5; n > 0; n--)
                    {
                        string from = SpineNames.NameAt(Properties, input.ReadByte(), "transform 속성");
                        var property = new JObject { ["offset"] = SpineJson.Number(input.ReadFloat()) };
                        var to = new JObject();
                        for (int count = input.ReadByte(); count > 0; count--)
                        {
                            string target = SpineNames.NameAt(Properties, input.ReadByte(), "transform 속성");
                            to[target] = new JObject
                            {
                                ["offset"] = SpineJson.Number(input.ReadFloat()),
                                ["max"] = SpineJson.Number(input.ReadFloat()),
                                ["scale"] = SpineJson.Number(input.ReadFloat()),
                            };
                        }

                        // 원본처럼 대상이 하나도 없으면 "to" 키를 만들지 않는다.
                        if (to.Count > 0)
                        {
                            property["to"] = to;
                        }

                        properties[from] = property;
                    }

                    if (properties.Count > 0)
                    {
                        constraint["properties"] = properties;
                    }

                    flags = input.ReadByte();
                    for (int i = 0; i < Offsets.Length; i++)
                    {
                        if ((flags & (1 << i)) != 0)
                        {
                            constraint[Offsets[i]] = SpineJson.Number(input.ReadFloat());
                        }
                    }

                    flags = input.ReadByte();
                    for (int i = 0; i < Mixes.Length; i++)
                    {
                        constraint[Mixes[i]] = SpineJson.Number((flags & (1 << i)) != 0 ? input.ReadFloat() : 0f);
                    }

                    break;
                }
                case TypePath:
                    constraint["positionMode"] = SpineNames.NameAt(SpineNames.PositionMode, (flags >> 1) & 1, "positionMode");
                    constraint["spacingMode"] = SpineNames.NameAt(SpineNames.SpacingMode, (flags >> 2) & 3, "spacingMode");
                    constraint["rotateMode"] = SpineNames.NameAt(SpineNames.RotateMode, (flags >> 4) & 3, "rotateMode");
                    if ((flags & 128) != 0)
                    {
                        constraint["rotation"] = SpineJson.Number(input.ReadFloat());
                    }

                    foreach (string key in new[] { "position", "spacing", "mixRotate", "mixX", "mixY" })
                    {
                        constraint[key] = SpineJson.Number(input.ReadFloat());
                    }

                    break;
                case TypePhysics:
                    for (int i = 0; i < PhysicsProperties.Length; i++)
                    {
                        if ((flags & (2 << i)) == 0)
                        {
                            continue;
                        }

                        float value = input.ReadFloat();
                        // scaleX 값에 scaleY 모드를 겹쳐 싣는다: -1 미만 → uniform, -2 미만 → volume.
                        if (i == 3 && value < -2f)
                        {
                            constraint["scaleY"] = "volume";
                            value = -2f - value;
                        }
                        else if (i == 3 && value < 0f)
                        {
                            constraint["scaleY"] = "uniform";
                            value = -1f - value;
                        }

                        constraint[PhysicsProperties[i]] = SpineJson.Number(value);
                    }

                    constraint["fps"] = input.ReadByte();
                    foreach (string key in new[] { "inertia", "strength", "damping" })
                    {
                        constraint[key] = SpineJson.Number(input.ReadFloat());
                    }

                    constraint["mass"] = SpineJson.Number((flags & 128) != 0 ? 1f / input.ReadFloat() : 1f);
                    constraint["wind"] = SpineJson.Number(input.ReadFloat());
                    constraint["gravity"] = SpineJson.Number(input.ReadFloat());
                    flags = input.ReadByte();
                    for (int i = 0; i < Globals.Length; i++)
                    {
                        constraint[Globals[i]] = (flags & (1 << i)) != 0;
                    }

                    constraint["mix"] = SpineJson.Number((flags & 128) != 0 ? input.ReadFloat() : 1f);
                    break;
                default:
                    // slider: 뼈로 구동되면(64) 값이 "max", 아니면 "time" 이다. max 는 nonessential 일 때만 실린다.
                    constraint["loop"] = (flags & 2) != 0;
                    constraint["additive"] = (flags & 4) != 0;
                    if ((flags & 8) != 0)
                    {
                        constraint[skeleton.Nonessential == true && (flags & 64) != 0 ? "max" : "time"] = SpineJson.Number(input.ReadFloat());
                    }

                    constraint["mix"] = SpineJson.Number((flags & 16) != 0 ? ((flags & 32) != 0 ? input.ReadFloat() : 1f) : 0f);
                    if ((flags & 64) != 0)
                    {
                        constraint["local"] = (flags & 128) != 0;
                        constraint["bone"] = ReadBone();
                        float offset = input.ReadFloat();
                        constraint["property"] = SpineNames.NameAt(Properties, input.ReadByte(), "slider 속성");
                        constraint["from"] = SpineJson.Number(offset);
                        constraint["to"] = SpineJson.Number(input.ReadFloat());
                        constraint["scale"] = SpineJson.Number(input.ReadFloat());
                    }

                    break;
            }

            return constraint;
        }

        public static void Write(SpineBinaryOutput output, JObject constraint, SpineSkeleton skeleton)
        {
            float F(string key, float defaultValue = 0f) => SpineJson.GetFloat(constraint, key, defaultValue);
            bool B(string key) => SpineJson.GetBool(constraint, key, false);
            int BoneIndex(string name) => IndexOf(skeleton.Bones.Select(bone => bone.Name), name, "뼈");

            output.WriteString(SpineJson.GetString(constraint, "name", null) ?? throw new SpineConversionException("4.3 제약조건에 name 이 없다."));
            int type = SpineNames.IndexOf(Types, SpineJson.GetString(constraint, "type", null), "4.3 제약조건 종류");
            output.WriteByte(type == TypeTransform ? 2 : type == TypePath ? 1 : type);
            if (type < TypePhysics)
            {
                JToken bones = SpineJson.Find(constraint, "bones") ?? throw new SpineConversionException($"4.3 제약조건 '{constraint["name"]}' 에 bones 가 없다.");
                output.WriteVarint(bones.Count(), true);
                foreach (JToken bone in bones)
                {
                    output.WriteVarint(BoneIndex((string)bone), true);
                }

                string targetKey = type == TypeIk ? "target" : type == TypeTransform ? "source" : "slot";
                string target = SpineJson.GetString(constraint, targetKey, null);
                output.WriteVarint(type == TypePath ? IndexOf(skeleton.Slots.Select(slot => slot.Name), target, "슬롯") : BoneIndex(target), true);
            }
            else if (type == TypePhysics)
            {
                output.WriteVarint(BoneIndex(SpineJson.GetString(constraint, "bone", null)), true);
            }

            int flags = B("skin") ? 1 : 0;
            switch (type)
            {
                case TypeIk:
                {
                    int mode = SpineNames.IndexOf(ScaleModes, SpineJson.GetString(constraint, "scaleY", "none"), "IK scaleY");
                    float mix = F("mix", 1f);
                    flags |= (mode != 0 ? 2 : 0) | (SpineJson.GetBool(constraint, "bendPositive", true) == false ? 4 : 0) | (B("compress") ? 8 : 0) | (B("stretch") ? 16 : 0);
                    if (mix != 0f)
                    {
                        flags |= 32 | (mix != 1f ? 64 : 0);
                    }

                    if (F("softness") != 0f)
                    {
                        flags |= 128;
                    }

                    output.WriteByte(flags);
                    if (mode != 0)
                    {
                        output.WriteByte(mode);
                    }

                    if ((flags & 64) != 0)
                    {
                        output.WriteFloat(mix);
                    }

                    if ((flags & 128) != 0)
                    {
                        output.WriteFloat(F("softness"));
                    }

                    break;
                }
                case TypeTransform:
                {
                    JObject properties = SpineJson.FindObject(constraint, "properties") ?? new JObject();
                    if (properties.Count > 6)
                    {
                        throw new SpineConversionException($"transform 제약조건 '{constraint["name"]}' 의 속성이 6개를 넘는다.");
                    }

                    flags |= (B("localSource") ? 2 : 0) | (B("localTarget") ? 4 : 0) | (B("additive") ? 8 : 0) | (B("clamp") ? 16 : 0) | (properties.Count << 5);
                    output.WriteByte(flags);
                    foreach (JProperty property in properties.Properties())
                    {
                        output.WriteByte(SpineNames.IndexOf(Properties, property.Name, "transform 속성"));
                        output.WriteFloat(SpineJson.GetFloat(property.Value, "offset", 0f));
                        JObject to = SpineJson.FindObject(property.Value, "to") ?? new JObject();
                        output.WriteByte(to.Count);
                        foreach (JProperty mapping in to.Properties())
                        {
                            output.WriteByte(SpineNames.IndexOf(Properties, mapping.Name, "transform 속성"));
                            output.WriteFloat(SpineJson.GetFloat(mapping.Value, "offset", 0f));
                            output.WriteFloat(SpineJson.GetFloat(mapping.Value, "max", 1f));
                            output.WriteFloat(SpineJson.GetFloat(mapping.Value, "scale", 1f));
                        }
                    }

                    flags = 0;
                    for (int i = 0; i < Offsets.Length; i++)
                    {
                        if (F(Offsets[i]) != 0f)
                        {
                            flags |= 1 << i;
                        }
                    }

                    output.WriteByte(flags);
                    for (int i = 0; i < Offsets.Length; i++)
                    {
                        if ((flags & (1 << i)) != 0)
                        {
                            output.WriteFloat(F(Offsets[i]));
                        }
                    }

                    // 어떤 속성도 대상으로 삼지 않는 성분의 mix 는 의미가 없어 0 으로 쓴다.
                    var values = new float[Mixes.Length];
                    flags = 0;
                    for (int i = 0; i < Mixes.Length; i++)
                    {
                        bool isTargeted = properties.Properties().Any(property => SpineJson.FindObject(property.Value, "to")?.ContainsKey(Properties[i]) == true);
                        float fallback = i == 2 ? F("mixX", 1f) : i == 4 ? F("mixScaleX", 1f) : 1f;
                        values[i] = isTargeted == true ? F(Mixes[i], fallback) : 0f;
                        if (values[i] != 0f)
                        {
                            flags |= 1 << i;
                        }
                    }

                    output.WriteByte(flags);
                    for (int i = 0; i < Mixes.Length; i++)
                    {
                        if ((flags & (1 << i)) != 0)
                        {
                            output.WriteFloat(values[i]);
                        }
                    }

                    break;
                }
                case TypePath:
                    flags |= SpineNames.IndexOf(SpineNames.PositionMode, SpineJson.GetString(constraint, "positionMode", "percent"), "positionMode") << 1;
                    flags |= SpineNames.IndexOf(SpineNames.SpacingMode, SpineJson.GetString(constraint, "spacingMode", "length"), "spacingMode") << 2;
                    flags |= SpineNames.IndexOf(SpineNames.RotateMode, SpineJson.GetString(constraint, "rotateMode", "tangent"), "rotateMode") << 4;
                    if (F("rotation") != 0f)
                    {
                        flags |= 128;
                    }

                    output.WriteByte(flags);
                    if ((flags & 128) != 0)
                    {
                        output.WriteFloat(F("rotation"));
                    }

                    output.WriteFloat(F("position"));
                    output.WriteFloat(F("spacing"));
                    output.WriteFloat(F("mixRotate", 1f));
                    output.WriteFloat(F("mixX", 1f));
                    output.WriteFloat(F("mixY", F("mixX", 1f)));
                    break;
                case TypePhysics:
                {
                    float scale = F("scaleX");
                    string mode = SpineJson.GetString(constraint, "scaleY", "none");
                    if (mode == "uniform")
                    {
                        scale = -1f - scale;
                    }
                    else if (mode == "volume")
                    {
                        scale = -2f - scale;
                    }

                    var values = new float[PhysicsProperties.Length];
                    for (int i = 0; i < PhysicsProperties.Length; i++)
                    {
                        float defaultValue = i == 5 ? 5000f : 0f;
                        values[i] = i == 3 ? scale : F(PhysicsProperties[i], defaultValue);
                        if (values[i] != defaultValue)
                        {
                            flags |= 2 << i;
                        }
                    }

                    if (F("mass", 1f) != 1f)
                    {
                        flags |= 128;
                    }

                    output.WriteByte(flags);
                    for (int i = 0; i < PhysicsProperties.Length; i++)
                    {
                        if ((flags & (2 << i)) != 0)
                        {
                            output.WriteFloat(values[i]);
                        }
                    }

                    output.WriteByte((int)F("fps", 60f));
                    // 4.3 의 inertia·damping 기본값은 4.2(1, 1)와 다르다.
                    output.WriteFloat(F("inertia", 0.5f));
                    output.WriteFloat(F("strength", 100f));
                    output.WriteFloat(F("damping", 0.85f));
                    if ((flags & 128) != 0)
                    {
                        output.WriteFloat(1f / F("mass", 1f));
                    }

                    output.WriteFloat(F("wind"));
                    output.WriteFloat(F("gravity"));
                    flags = 0;
                    for (int i = 0; i < Globals.Length; i++)
                    {
                        if (B(Globals[i]) == true)
                        {
                            flags |= 1 << i;
                        }
                    }

                    if (F("mix", 1f) != 1f)
                    {
                        flags |= 128;
                    }

                    output.WriteByte(flags);
                    if ((flags & 128) != 0)
                    {
                        output.WriteFloat(F("mix", 1f));
                    }

                    break;
                }
                default:
                {
                    bool isDriven = SpineJson.Has(constraint, "bone");
                    float value = isDriven == true ? F("max") : F("time");
                    float mix = F("mix", 1f);
                    flags |= (B("loop") ? 2 : 0) | (B("additive") ? 4 : 0);
                    if (value != 0f && (isDriven == false || skeleton.Nonessential == true))
                    {
                        flags |= 8;
                    }

                    if (mix != 0f)
                    {
                        flags |= 16 | (mix != 1f ? 32 : 0);
                    }

                    if (isDriven == true)
                    {
                        flags |= 64 | (B("local") ? 128 : 0);
                    }

                    output.WriteByte(flags);
                    if ((flags & 8) != 0)
                    {
                        output.WriteFloat(value);
                    }

                    if ((flags & 32) != 0)
                    {
                        output.WriteFloat(mix);
                    }

                    if (isDriven == true)
                    {
                        output.WriteVarint(BoneIndex(SpineJson.GetString(constraint, "bone", null)), true);
                        output.WriteFloat(F("from"));
                        output.WriteByte(SpineNames.IndexOf(Properties, SpineJson.GetString(constraint, "property", null), "slider 속성"));
                        output.WriteFloat(F("to"));
                        output.WriteFloat(F("scale", 1f));
                    }

                    break;
                }
            }
        }

        private static int IndexOf(System.Collections.Generic.IEnumerable<string> names, string name, string what)
        {
            int index = 0;
            foreach (string candidate in names)
            {
                if (candidate == name)
                {
                    return index;
                }

                index++;
            }

            throw new SpineConversionException($"{what} '{name}' 을(를) 찾을 수 없다.");
        }
    }
}
